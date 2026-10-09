using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.FakeEngine;
using Assistant.ModelHost.Models;
using Assistant.ModelHost.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>
/// The engine options a model profile carries, from the load request to the engine's command line: they cross the
/// pipe, are checked again by the host, and are appended to the fixed arguments the host chooses itself.
/// </summary>
public sealed class RuntimeArgumentTests
{
    private const string Socket = @"C:\s\e.sock";

    [Fact]
    public void TheCommandLine_HasTheProfilesOptionsAfterTheFixedOnes()
    {
        var launch = new ModelProcessLaunch(@"C:\models\m.gguf") { RuntimeArguments = ["--threads", "6", "--no-kv-offload"] };

        var arguments = LlamaServerCommand.Arguments(launch, Socket);

        Assert.Equal(["--threads", "6", "--no-kv-offload"], arguments.TakeLast(3));
        Assert.Equal("--no-mmproj-auto", arguments[^4]);
        Assert.Equal(Socket, arguments[arguments.ToList().IndexOf("--host") + 1]);
    }

    [Fact]
    public void WithoutOptions_TheCommandLine_IsAsBefore()
    {
        var arguments = LlamaServerCommand.Arguments(new ModelProcessLaunch(@"C:\models\m.gguf"), Socket);

        Assert.Equal("--no-mmproj-auto", arguments[^1]);
    }

    [Theory]
    [InlineData("--host|0.0.0.0")]
    [InlineData("--ctx-size|1")]
    [InlineData("--threads")]
    public void TheCommandLine_RefusesAnOptionThatIsNotAllowed(string arguments)
    {
        var launch = new ModelProcessLaunch(@"C:\models\m.gguf") { RuntimeArguments = arguments.Split('|') };

        Assert.Throws<ArgumentException>(() => LlamaServerCommand.Arguments(launch, Socket));
    }

    [Fact]
    public async Task ALoad_GivesTheEngineTheOptions_AndTheModelsOwnId()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = new ControllerRig(setup);
        var files = new ModelFiles(setup.ScenarioPath)
        {
            ModelId = "chat-4b",
            ContextLength = 4096,
            RuntimeArguments = ["--batch-size", "512", "--ubatch-size", "256"],
        };

        var model = await rig.Controller.LoadAsync(new LoadModelRequest(files.DeriveModelId()) { Files = files }, TestPipes.Timeout());

        var arguments = setup.Report(1).Arguments;
        Assert.Equal("chat-4b", model.Id);
        Assert.Equal("512", ValueAfter(arguments, "--batch-size"));
        Assert.Equal("256", ValueAfter(arguments, "--ubatch-size"));
        Assert.Equal("4096", ValueAfter(arguments, "--ctx-size"));
        Assert.EndsWith(".sock", ValueAfter(arguments, "--host"), StringComparison.Ordinal);
        Assert.Contains("--offline", arguments);
    }

    [Fact]
    public async Task ALoad_WithAnOptionThatIsNotAllowed_FailsBeforeAnyEngineStarts()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var rig = new ControllerRig(setup);
        var files = new ModelFiles(setup.ScenarioPath) { RuntimeArguments = ["--host", "0.0.0.0"] };

        var failed = await Assert.ThrowsAsync<ModelRequestException>(
            () => rig.Controller.LoadAsync(new LoadModelRequest("chat") { Files = files }, TestPipes.Timeout()));

        Assert.Equal(ModelHostErrorCode.ModelLoadFailed, failed.Code);
        Assert.Equal(0, setup.Launches);
    }

    [Fact]
    public void TheOptionsAndTheModelsId_CrossTheProtocol()
    {
        var request = new LoadModelRequest("chat-4b")
        {
            Files = new ModelFiles(@"C:\models\chat-4b\model.gguf")
            {
                ModelId = "chat-4b",
                ContextLength = 4096,
                RuntimeArguments = ["--threads", "6", "--flash-attn", "on"],
            },
        };

        var frame = ModelHostSerializer.Deserialize(ModelHostSerializer.Serialize(7, request));

        var read = Assert.IsType<LoadModelRequest>(frame.Message);
        Assert.Equal("chat-4b", read.Files!.ModelId);
        Assert.Equal(["--threads", "6", "--flash-attn", "on"], read.Files.RuntimeArguments);
        Assert.Equal(4096, read.Files.ContextLength);
    }

    [Fact]
    public void ALoadRequestFromAnOlderApp_HasNoOptions()
    {
        var json = """{"v":1,"id":3,"type":"loadModel","body":{"modelId":"m","files":{"modelPath":"C:\\m.gguf"}}}""";

        var frame = ModelHostSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

        var read = Assert.IsType<LoadModelRequest>(frame.Message);
        Assert.Empty(read.Files!.RuntimeArguments);
        Assert.Null(read.Files.ModelId);
        Assert.Equal("m", read.ModelId);
    }

    [Theory]
    [InlineData("""["--host","0.0.0.0"]""")]
    [InlineData("""["--threads"]""")]
    [InlineData("""["--threads","0"]""")]
    [InlineData("""[null]""")]
    public void ALoadRequestWithOptionsThatAreNotAllowed_IsMalformed(string arguments)
    {
        var json = """{"v":1,"id":3,"type":"loadModel","body":{"modelId":"m","files":{"modelPath":"C:\\m.gguf","runtimeArguments":"""
            + arguments + "}}}";

        var frame = ModelHostSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Null(frame.Message);
        Assert.Equal(ModelHostErrorCode.MalformedMessage, frame.Error);
        Assert.Equal(3, frame.Id);
    }

    [Fact]
    public void ABlankModelId_IsMalformed()
    {
        var json = """{"v":1,"id":3,"type":"loadModel","body":{"modelId":"m","files":{"modelPath":"C:\\m.gguf","modelId":" "}}}""";

        var frame = ModelHostSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Equal(ModelHostErrorCode.MalformedMessage, frame.Error);
    }

    [Fact]
    public void TheRequestsToString_HoldsNoPathAndCountsTheOptions()
    {
        var text = new LoadModelRequest("chat-4b")
        {
            Files = new ModelFiles(@"C:\Users\PRIVATE-NAME-90e2\model.gguf") { RuntimeArguments = ["--threads", "6"] },
        }.ToString();

        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        Assert.Contains("RuntimeArguments = 2", text, StringComparison.Ordinal);
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option) =>
        arguments[arguments.ToList().IndexOf(option) + 1];

    private sealed class ControllerRig : IAsyncDisposable
    {
        private readonly ModelProcessManager _manager;

        public ControllerRig(FakeEngineSetup setup)
        {
            _manager = setup.CreateManager();
            Controller = new ModelController(_manager, TimeProvider.System, NullLogger<ModelController>.Instance);
        }

        public ModelController Controller { get; }

        public async ValueTask DisposeAsync()
        {
            Controller.Dispose();
            await _manager.DisposeAsync();
        }
    }
}
