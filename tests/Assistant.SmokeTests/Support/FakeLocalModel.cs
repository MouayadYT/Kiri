using System.IO;
using System.IO.Pipes;
using Assistant.Core.Ipc;
using Assistant.Core.ModelHosting;
using Assistant.Core.Settings;
using Assistant.ModelHost.FakeEngine;
using Assistant.ModelHost.Generation;
using Assistant.ModelHost.Models;
using Assistant.ModelHost.Processes;
using Assistant.ModelHost.Runtime;
using Assistant.ModelHost.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.SmokeTests.Support;

/// <summary>
/// A local model for a check. The model host's own parts are the real ones, as they run inside the host program: the protocol over a real named pipe,
/// the request handler, the model controller (load, unload, status), the process manager that starts and watches the engine, and the client that talks to
/// the engine's HTTP server over its UNIX socket. Only the engine program is a stand-in (the fake engine, which streams what its "model file" says), and
/// the host runs in this process, so that its socket lives in the check's own folder and not in the user's profile (the host program itself, and what it
/// does with a real engine, is the model-host tests' to check).
/// </summary>
internal sealed class FakeLocalModel : IAsyncDisposable
{
    private readonly ModelProcessManager _manager;
    private readonly ModelController _controller;
    private readonly LlamaServerChatEngine _engine;

    private FakeLocalModel(
        string modelPath, string? projectorPath, IModelRuntimeLocator locator, ModelProcessManager manager, ModelController controller,
        LlamaServerChatEngine engine)
    {
        ModelPath = modelPath;
        ProjectorPath = projectorPath;
        _manager = manager;
        _controller = controller;
        _engine = engine;
        Launcher = new InProcessLauncher(new ModelHostRequestHandler(
            TimeProvider.System, locator, controller,
            new TextGenerator(controller, engine, TimeProvider.System, NullLogger<TextGenerator>.Instance)), controller);
    }

    /// <summary>The "model" the settings name; the engine's behavior is in it, and what it saw is written beside it.</summary>
    public string ModelPath { get; }

    /// <summary>The multimodal projector that goes with it, for a model that can read pictures, or <see langword="null"/> for one that reads text only.</summary>
    public string? ProjectorPath { get; }

    public IModelHostLauncher Launcher { get; }

    /// <summary>The body of request <paramref name="number"/> (from 1) the engine was sent.</summary>
    public string RequestBody(int number) => File.ReadAllText(FakeChatReply.RequestPath(ModelPath, number));

    /// <summary>Whether the engine saw the client of request <paramref name="number"/> leave before the answer ended.</summary>
    public bool EngineSawTheClientLeave(int number) => File.Exists(FakeChatReply.DisconnectedPath(ModelPath, number));

    /// <summary>How many times the engine program has been started.</summary>
    public int EngineLaunches =>
        File.Exists(FakeEngineReport.CountPath(ModelPath)) ? int.Parse(File.ReadAllText(FakeEngineReport.CountPath(ModelPath))) : 0;

    /// <summary>
    /// Makes the model in <paramref name="scratch"/>, with the engine answering as <paramref name="scenario"/> says, and a projector when
    /// <paramref name="readsPictures"/>, which makes it a model that accepts pictures.
    /// </summary>
    public static FakeLocalModel Create(ScratchFolder scratch, FakeEngineScenario? scenario = null, bool readsPictures = false)
    {
        var modelPath = scratch.File("model", "model.gguf");
        (scenario ?? new FakeEngineScenario()).Save(modelPath);
        string? projectorPath = null;
        if (readsPictures)
        {
            projectorPath = scratch.File("model", "mmproj.gguf");
            File.WriteAllText(projectorPath, FakeEngineScenario.Magic + "-projector");
        }

        var engine = Path.Combine(AppContext.BaseDirectory, "Assistant.ModelHost.FakeEngine.exe");
        var locator = new Located(ModelRuntimeStatus.Ready(new ModelRuntime(AppContext.BaseDirectory, engine)));
        var manager = new ModelProcessManager(
            locator,
            new ModelProcessOptions(Path.Combine(scratch.Path, "s")) { StartTimeout = TimeSpan.FromSeconds(30) },
            TimeProvider.System,
            NullLogger<ModelProcessManager>.Instance);
        var controller = new ModelController(manager, TimeProvider.System, NullLogger<ModelController>.Instance);
        return new FakeLocalModel(modelPath, projectorPath, locator, manager, controller, new LlamaServerChatEngine(manager));
    }

    /// <summary>The app's container started on this model: its host launcher is the one made here.</summary>
    public Action<IServiceCollection> Replace => services => services.AddSingleton(Launcher);

    /// <summary>The settings that name this model, as the user does by picking a file.</summary>
    public AppSettings Use(AppSettings settings) =>
        settings with { Model = settings.Model with { ModelFilePath = ModelPath, ProjectorFilePath = ProjectorPath } };

    public async ValueTask DisposeAsync()
    {
        _controller.Dispose();
        await _manager.DisposeAsync();
        _engine.Dispose();
    }

    private sealed class Located(ModelRuntimeStatus status) : IModelRuntimeLocator
    {
        public ModelRuntimeStatus Locate() => status;
    }

    /// <summary>Starts hosts in this process: each is a session on one end of a real named pipe, with the app's client on the other.</summary>
    private sealed class InProcessLauncher(ModelHostRequestHandler handler, ModelController controller) : IModelHostLauncher
    {
        public async Task<IModelHostConnection> StartAsync(CancellationToken cancellationToken = default)
        {
            var name = LocalPipe.CreateUniqueName("Assistant.Smoke.ModelHost");
            var host = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, LocalPipe.Options);
            var app = new NamedPipeClientStream(".", name, PipeDirection.InOut, LocalPipe.Options);
            await Task.WhenAll(host.WaitForConnectionAsync(cancellationToken), app.ConnectAsync(cancellationToken)).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var session = new ModelHostSession(handler, NullLogger<ModelHostSession>.Instance, controller);
            return new Connection(new ModelHostClient(app, NullLogger<ModelHostClient>.Instance), session.RunAsync(host, CancellationToken.None));
        }
    }

    private sealed class Connection(ModelHostClient client, Task<ModelHostExitReason> serving) : IModelHostConnection
    {
        public ModelHostClient Client => client;

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }
}
