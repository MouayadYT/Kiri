using System.Diagnostics;
using System.Text.Json;
using Assistant.Tools.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>The fake MCP server as a program that the Assistant starts, and what a test needs to use it.</summary>
internal sealed class FakeServerProgram : IDisposable
{
    private readonly TempFolder _folder = new();

    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "Assistant.Tools.FakeMcpServer.exe");

    public string LogPath => _folder.File("received.log");

    public string PidPath => _folder.File("server.pid");

    public string[] Methods
    {
        get
        {
            if (!File.Exists(LogPath))
            {
                return [];
            }

            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
    }

    /// <summary>The program started with <paramref name="arguments"/>, which write what it receives and its process id to files of this test.</summary>
    public McpLaunch Launch(IReadOnlyDictionary<string, string>? environment = null, params string[] arguments) =>
        new(ExePath, ["--log", LogPath, "--pid-file", PidPath, .. arguments], null, environment ?? new Dictionary<string, string>());

    public McpClient Client(McpLaunch launch, McpClientOptions? options = null)
    {
        var settings = options ?? new McpClientOptions { ProbeTimeout = TimeSpan.FromMilliseconds(600) };
        return new McpClient(McpTransportKind.Stdio, (_, _) => ValueTask.FromResult<IMcpTransport>(new StdioMcpTransport(launch, settings)), settings);
    }

    public McpClient Client(params string[] arguments) => Client(Launch(null, arguments));

    public int ReadPid() => int.Parse(File.ReadAllText(PidPath));

    public void Dispose() => _folder.Dispose();
}

public sealed class McpStdioTests : IDisposable
{
    private readonly FakeServerProgram _server = new();

    public void Dispose() => _server.Dispose();

    [Fact]
    public void TheFakeServerProgramIsBesideTheTests() => Assert.True(File.Exists(FakeServerProgram.ExePath), FakeServerProgram.ExePath);

    [Fact]
    public async Task AModernServerIsFoundByItsDiscoverAnswerAndNeverGreetedWithInitialize()
    {
        await using var client = _server.Client("--era", "modern");

        await client.ConnectAsync();

        Assert.True(client.IsConnected);
        Assert.Equal(McpTransportKind.Stdio, client.TransportKind);
        var server = client.Server!;
        Assert.Equal("2026-07-28", server.ProtocolVersion);
        Assert.Equal("Fake", server.Name);
        Assert.Equal("1.2.3", server.Version);
        Assert.True(server.Capabilities.Tools);
        Assert.True(server.Capabilities.ToolsListChanged);
        Assert.Equal(["server/discover"], _server.Methods);
    }

    [Fact]
    public async Task AServerOfTheEarlierProtocolIsGreetedWithInitializeAfterItRejectsDiscover()
    {
        await using var client = _server.Client("--era", "legacy");

        await client.ConnectAsync();

        Assert.Equal("2025-11-25", client.Server!.ProtocolVersion);
        Assert.Equal(["server/discover", "initialize", "notifications/initialized"], _server.Methods);
    }

    [Fact]
    public async Task AProgramThatSaysNothingToDiscoverIsTakenForTheEarlierProtocolAfterTheProbeTime()
    {
        await using var client = _server.Client("--era", "silent");

        var started = Stopwatch.StartNew();
        await client.ConnectAsync();

        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(500), started.Elapsed.ToString());
        Assert.Equal("2025-11-25", client.Server!.ProtocolVersion);
        Assert.Contains("initialize", _server.Methods);
    }

    [Fact]
    public async Task AnEarlierRevisionTheServerSpeaksIsAgreed()
    {
        await using var client = _server.Client("--era", "legacy", "--legacy-version", "2025-03-26");

        await client.ConnectAsync();

        Assert.Equal("2025-03-26", client.Server!.ProtocolVersion);
    }

    [Fact]
    public async Task AServerConnectedToBeforeAsTheEarlierProtocolIsNotAskedTheModernQuestion()
    {
        await using var client = _server.Client(_server.Launch(null, "--era", "silent"), new McpClientOptions { KnownProtocolVersion = "2025-06-18", ProbeTimeout = TimeSpan.FromSeconds(30) });

        var started = Stopwatch.StartNew();
        await client.ConnectAsync();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), "It waited for the probe.");
        Assert.Equal(["initialize", "notifications/initialized"], _server.Methods);
    }

    [Fact]
    public async Task AServerThatWasUpgradedSinceItWasLastConnectedToIsFoundModernAfterAll()
    {
        await using var client = _server.Client(_server.Launch(null, "--era", "modern"), new McpClientOptions { KnownProtocolVersion = "2025-11-25" });

        await client.ConnectAsync();

        Assert.Equal("2026-07-28", client.Server!.ProtocolVersion);
        Assert.Equal(["initialize", "server/discover"], _server.Methods);
    }

    [Theory]
    [InlineData("modern")]
    [InlineData("legacy")]
    public async Task ToolsAreListedWithWhatTheServerSaysOfThem(string era)
    {
        await using var client = _server.Client("--era", era);
        await client.ConnectAsync();

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, tool => tool.Name == "echo" && tool.Annotations.ReadOnlyHint == true && tool.Description == "Echoes a message back.");
        Assert.Contains(tools, tool => tool.Name == "delete_everything" && tool.Annotations.DestructiveHint == true);
        Assert.Contains(tools, tool => tool.Name == "createTask" && tool.Annotations.ReadOnlyHint is null);
        Assert.Equal("string", tools.Single(tool => tool.Name == "echo").InputSchema.GetProperty("properties").GetProperty("message").GetProperty("type").GetString());
        Assert.Equal(tools.Count, tools.Select(tool => tool.Name).Distinct().Count());
    }

    [Fact]
    public async Task AllThePagesOfToolsAreRead()
    {
        await using var all = _server.Client("--era", "modern");
        await all.ConnectAsync();
        var expected = (await all.ListToolsAsync()).Count;

        using var paged = new FakeServerProgram();
        await using var client = paged.Client("--era", "modern", "--page-size", "4");
        await client.ConnectAsync();

        Assert.Equal(expected, (await client.ListToolsAsync()).Count);
        Assert.True(expected > 4);
    }

    [Fact]
    public async Task TheToolsReadAreLimited()
    {
        await using var client = _server.Client(_server.Launch(null, "--era", "modern", "--page-size", "4"), new McpClientOptions { MaxTools = 6 });
        await client.ConnectAsync();
        Assert.Equal(6, (await client.ListToolsAsync()).Count);
    }

    [Theory]
    [InlineData("modern")]
    [InlineData("legacy")]
    public async Task AToolIsCalledWithItsArgumentsAndGivesItsText(string era)
    {
        await using var client = _server.Client("--era", era);
        await client.ConnectAsync();
        var echo = (await client.ListToolsAsync()).Single(tool => tool.Name == "echo");

        var result = await client.CallToolAsync(echo, Sample.Json("""{"message":"hello é \"quoted\" \n line"}"""));

        Assert.False(result.IsError);
        var block = Assert.Single(result.Content);
        Assert.Equal(McpContentKind.Text, block.Kind);
        Assert.Equal("echo: hello é \"quoted\" \n line", block.Text);
    }

    [Fact]
    public async Task AToolThatGivesStructuredContentHasItRead()
    {
        await using var client = _server.Client("--era", "modern");
        await client.ConnectAsync();
        var add = (await client.ListToolsAsync()).Single(tool => tool.Name == "add");

        var result = await client.CallToolAsync(add, Sample.Json("""{"a":1.5,"b":2}"""));

        Assert.Equal(3.5, result.StructuredContent!.Value.GetProperty("sum").GetDouble());
        Assert.Equal("The sum is 3.5", Assert.Single(result.Content).Text);
    }

    [Fact]
    public async Task AToolThatSaysItFailedIsAResultWithTheErrorFlag()
    {
        await using var client = _server.Client("--era", "modern");
        await client.ConnectAsync();
        var fail = (await client.ListToolsAsync()).Single(tool => tool.Name == "fail");

        var result = await client.CallToolAsync(fail, Sample.Json("{}"));

        Assert.True(result.IsError);
        Assert.Contains("the date is in the past", Assert.Single(result.Content).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProtocolErrorIsAnExceptionWithTheCodeAndWhatTheServerSaid()
    {
        await using var client = _server.Client("--era", "modern");
        await client.ConnectAsync();
        var boom = (await client.ListToolsAsync()).Single(tool => tool.Name == "boom");

        var exception = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(boom, Sample.Json("{}")));

        Assert.Equal(McpFailure.Server, exception.Failure);
        Assert.Equal(-32602, exception.RpcCode);
        Assert.Equal("Invalid params: missing field", exception.ServerMessage);
        Assert.DoesNotContain("missing field", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APictureIsReadAsAPictureWithNoDataKept()
    {
        await using var client = _server.Client("--era", "modern");
        await client.ConnectAsync();
        var picture = (await client.ListToolsAsync()).Single(tool => tool.Name == "picture");

        var result = await client.CallToolAsync(picture, Sample.Json("{}"));

        var block = Assert.Single(result.Content);
        Assert.Equal(McpContentKind.Image, block.Kind);
        Assert.Equal("image/png", block.MimeType);
        Assert.Null(block.Text);
    }

    [Fact]
    public async Task APingIsAnsweredByBothEras()
    {
        foreach (var era in new[] { "modern", "legacy" })
        {
            using var program = new FakeServerProgram();
            await using var client = program.Client("--era", era);
            await client.ConnectAsync();
            await client.PingAsync();
        }
    }

    [Fact]
    public async Task ACallThatIsCancelledTellsTheServerAndEndsAtOnce()
    {
        await using var client = _server.Client("--era", "modern");
        await client.ConnectAsync();
        var slow = (await client.ListToolsAsync()).Single(tool => tool.Name == "slow");
        using var cancel = new CancellationTokenSource();

        var call = client.CallToolAsync(slow, Sample.Json("""{"ms":60000}"""), cancel.Token);
        await Task.Delay(300);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await Eventually(() => _server.Methods.Contains("notifications/cancelled"));
    }

    [Fact]
    public async Task ACallThatTakesTooLongFailsWithATimeout()
    {
        await using var client = _server.Client(_server.Launch(null, "--era", "modern"), new McpClientOptions { CallTimeout = TimeSpan.FromMilliseconds(400) });
        await client.ConnectAsync();
        var slow = (await client.ListToolsAsync()).Single(tool => tool.Name == "slow");

        var exception = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(slow, Sample.Json("""{"ms":60000}""")));

        Assert.Equal(McpFailure.TimedOut, exception.Failure);
        await Eventually(() => _server.Methods.Contains("notifications/cancelled"));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task AProgramThatEndsIsReportedAndLaterCallsFail()
    {
        await using var client = _server.Client("--era", "modern", "--exit-after", "2");
        await client.ConnectAsync();
        var disconnected = new TaskCompletionSource();
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        var tools = await client.ListToolsAsync();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(client.IsConnected);
        var exception = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(tools[0], Sample.Json("{}")));
        Assert.Equal(McpFailure.Closed, exception.Failure);
    }

    [Fact]
    public async Task ARequestInFlightWhenTheProgramEndsFailsAsClosed()
    {
        await using var client = _server.Client("--era", "modern", "--exit-after", "3");
        await client.ConnectAsync();
        var echo = (await client.ListToolsAsync()).Single(tool => tool.Name == "echo");

        // The program ends once it has answered the next request (its third); the one after it can only be closed.
        await client.CallToolAsync(echo, Sample.Json("""{"message":"x"}"""));
        await Eventually(() => !client.IsConnected);
        var exception = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(echo, Sample.Json("""{"message":"y"}""")));
        Assert.Equal(McpFailure.Closed, exception.Failure);
    }

    [Fact]
    public async Task DisposingEndsAProgramThatDoesNotEndWhenItsInputCloses()
    {
        var client = _server.Client("--era", "modern", "--ignore-close");
        await client.ConnectAsync();
        var process = Process.GetProcessById(_server.ReadPid());
        Assert.False(process.HasExited);

        await client.DisposeAsync();

        Assert.True(process.WaitForExit(10_000), "The program was left running.");
        process.Dispose();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task DisposingEndsAProgramThatEndsWhenItsInputCloses()
    {
        var client = _server.Client("--era", "legacy");
        await client.ConnectAsync();
        var process = Process.GetProcessById(_server.ReadPid());

        var started = Stopwatch.StartNew();
        await client.DisposeAsync();

        Assert.True(process.WaitForExit(10_000));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), "A program that ends by itself was waited for too long.");
        process.Dispose();
    }

    [Fact]
    public async Task LinesThatAreNotMessagesAndAnErrorStreamAreTolerated()
    {
        await using var client = _server.Client("--era", "modern", "--noise");
        await client.ConnectAsync();

        var echo = (await client.ListToolsAsync()).Single(tool => tool.Name == "echo");
        var result = await client.CallToolAsync(echo, Sample.Json("""{"message":"still works"}"""));

        Assert.Equal("echo: still works", Assert.Single(result.Content).Text);
    }

    [Fact]
    public async Task AProgramThatWritesALotToItsErrorStreamIsNotBlocked()
    {
        await using var client = _server.Client("--era", "modern", "--stderr-flood");
        await client.ConnectAsync();
        Assert.NotEmpty(await client.ListToolsAsync());
    }

    [Fact]
    public async Task OnlyTheEnvironmentWindowsNeedsAndTheIntegrationAddsIsPassedOn()
    {
        const string Leaked = "ASSISTANT_TEST_LEAKED_SECRET";
        Environment.SetEnvironmentVariable(Leaked, "do-not-pass-me-on");
        try
        {
            await using var client = _server.Client(_server.Launch(new Dictionary<string, string> { ["CONFIGURED_BY_INTEGRATION"] = "yes" }, "--era", "modern"));
            await client.ConnectAsync();
            var env = (await client.ListToolsAsync()).Single(tool => tool.Name == "env");

            var names = (await client.CallToolAsync(env, Sample.Json("{}"))).Content[0].Text!.Split(',');

            Assert.DoesNotContain(Leaked, names, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("CONFIGURED_BY_INTEGRATION", names, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("SystemRoot", names, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("PATH", names, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Leaked, null);
        }
    }

    [Fact]
    public async Task ArgumentsAreGivenOneByOneSoSpacesAndQuotesNeedNoShell()
    {
        var path = _server.LogPath;
        var spaced = Path.Combine(Path.GetDirectoryName(path)!, "a folder with spaces");
        Directory.CreateDirectory(spaced);
        var logPath = Path.Combine(spaced, "it's here.log");
        var launch = new McpLaunch(FakeServerProgram.ExePath, ["--era", "modern", "--log", logPath], null, new Dictionary<string, string>());
        await using var client = _server.Client(launch);

        await client.ConnectAsync();

        Assert.Equal(["server/discover"], File.ReadAllText(logPath).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task AProgramThatIsNotThereCannotBeStarted()
    {
        var launch = new McpLaunch(Path.Combine(Path.GetTempPath(), "no-such-folder", "nothing.exe"), [], null, new Dictionary<string, string>());
        await using var client = _server.Client(launch);

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.LaunchFailed, exception.Failure);
    }

    [Fact]
    public async Task AFileThatIsNotAProgramCannotBeStarted()
    {
        var notAProgram = _server.LogPath;
        await File.WriteAllTextAsync(notAProgram, "text");
        await using var client = _server.Client(new McpLaunch(notAProgram, [], null, new Dictionary<string, string>()));

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.LaunchFailed, exception.Failure);
    }

    [Fact]
    public async Task AMessageLargerThanTheLimitEndsTheConnection()
    {
        await using var client = _server.Client(_server.Launch(null, "--era", "modern"), new McpClientOptions { MaxMessageBytes = 32 * 1024 });
        await client.ConnectAsync();
        var big = (await client.ListToolsAsync()).Single(tool => tool.Name == "big");

        var exception = await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync(big, Sample.Json("{}")));

        Assert.Equal(McpFailure.Closed, exception.Failure);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectingAnAbandonedClientTwiceIsRefused()
    {
        await using var client = _server.Client("--era", "modern");
        await client.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task ACallBeforeConnectingFailsAsClosed()
    {
        await using var client = _server.Client("--era", "modern");
        var exception = await Assert.ThrowsAsync<McpException>(() => client.ListToolsAsync());
        Assert.Equal(McpFailure.Closed, exception.Failure);
    }

    [Fact]
    public async Task ConnectingCanBeCancelled()
    {
        await using var client = _server.Client("--era", "silent");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(cancel.Token));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectingThatTakesTooLongFailsWithATimeoutAndEndsTheProgram()
    {
        await using var client = _server.Client(_server.Launch(null, "--era", "silent"), new McpClientOptions { ConnectTimeout = TimeSpan.FromMilliseconds(300), ProbeTimeout = TimeSpan.FromSeconds(30) });

        var exception = await Assert.ThrowsAsync<McpException>(() => client.ConnectAsync());

        Assert.Equal(McpFailure.TimedOut, exception.Failure);
        await WaitUntilGoneAsync(_server.ReadPid());
    }

    // A process that is already gone is what is wanted: it cannot be looked up at all.
    private static async Task WaitUntilGoneAsync(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(await Task.Run(() => process.WaitForExit(10_000)), "The program was left running.");
        }
        catch (ArgumentException)
        {
            // It ended before it could be looked up.
        }
    }

    private static async Task Eventually(Func<bool> condition, int milliseconds = 10_000)
    {
        var until = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(until.ElapsedMilliseconds < milliseconds, "The condition was not met in time.");
            await Task.Delay(25);
        }
    }
}
