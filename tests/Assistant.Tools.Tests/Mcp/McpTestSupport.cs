using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>A folder in the temp directory that is removed with it.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assistant-tools-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A program that is still ending holds a file; the folder is in the temp directory and goes in time.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A clock the test moves by hand. It keeps timers, so what waits on it (a delay, the idle check) fires when the test moves it past.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>When the next timer falls due, or <see langword="null"/> when nothing is waiting on the clock.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_gate)
            {
                return _timers.Where(timer => timer.Due is not null).Select(timer => timer.Due).Min();
            }
        }
    }

    /// <summary>Moves the clock on, firing every timer that falls due on the way (a repeating timer as often as it falls due).</summary>
    public void Advance(TimeSpan by)
    {
        var target = GetUtcNow() + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.Due is not null && timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value > _now ? next.Due.Value : _now;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            }

            return true;
        }

        public void Fire()
        {
            lock (clock._gate)
            {
                Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : clock._now + _period;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                Due = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Secrets kept in memory.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    public bool Fails { get; set; }

    public List<string> Deleted { get; } = [];

    public Task SetAsync(string name, string secret, CancellationToken cancellationToken = default)
    {
        Secrets[name] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) =>
        Fails ? throw new SecretStoreException("The store failed.") : Task.FromResult(Secrets.TryGetValue(name, out var secret) ? secret : null);

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        Deleted.Add(name);
        return Task.FromResult(Secrets.Remove(name));
    }
}

/// <summary>An integration store that keeps the list in memory, can be made to fail, and counts its writes.</summary>
internal sealed class MemoryIntegrationStore(params InstalledIntegration[] initial) : IInstalledIntegrationStore
{
    public List<InstalledIntegration> Saved { get; private set; } = [.. initial];

    public int Writes { get; private set; }

    public bool FailWrites { get; set; }

    public bool FailReads { get; set; }

    public Task<IntegrationStoreContents> LoadAsync(CancellationToken cancellationToken = default) =>
        FailReads ? throw new IntegrationException(IntegrationFailure.StoreFailed) : Task.FromResult(new IntegrationStoreContents([.. Saved]));

    public Task SaveAsync(IReadOnlyList<InstalledIntegration> integrations, CancellationToken cancellationToken = default)
    {
        if (FailWrites)
        {
            throw new IntegrationException(IntegrationFailure.StoreFailed);
        }

        Writes++;
        Saved = [.. integrations];
        return Task.CompletedTask;
    }
}

/// <summary>Integrations as tests need them.</summary>
internal static class Sample
{
    /// <summary>A remote server: HTTPS, enabled, nothing trusted.</summary>
    public static InstalledIntegration Remote(string id = "todoist", string name = "Todoist", string endpoint = "https://mcp.example.com/mcp") => new()
    {
        Id = id,
        Name = name,
        Transport = new IntegrationTransport { Kind = McpTransportKind.StreamableHttp, Endpoint = endpoint },
        Enabled = true,
    };

    /// <summary>A server on this PC, reached over HTTP on the loopback address.</summary>
    public static InstalledIntegration Loopback(string id = "notes", string name = "Notes", string endpoint = "http://127.0.0.1:9/mcp") => Remote(id, name, endpoint);

    /// <summary>A program the Assistant starts.</summary>
    public static InstalledIntegration Program(string id = "local", string name = "Local App", string command = @"C:\Tools\server.exe", params string[] arguments) => new()
    {
        Id = id,
        Name = name,
        Transport = new IntegrationTransport { Kind = McpTransportKind.Stdio, Command = command, Arguments = arguments },
        Enabled = true,
    };

    /// <summary>A tool as a server lists it: its input schema is the JSON given.</summary>
    public static McpToolDescriptor Tool(
        string name, string description = "Does a thing.", string schema = """{"type":"object","properties":{"text":{"type":"string","description":"Some text."}},"required":["text"]}""",
        bool? readOnly = null, bool? destructive = null, string? title = null) =>
        new(name, title, description, Json(schema), new McpToolAnnotations(readOnly, destructive, null, null));

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static McpToolResult Text(string text) => new(false, [new McpContentBlock(McpContentKind.Text, text, null, null, null)], null);
}

/// <summary>A client the test controls: what it lists, what a call answers, and whether it connects.</summary>
internal sealed class StubMcpClient : IMcpClient
{
    private bool _connected;

    public McpTransportKind TransportKind { get; set; } = McpTransportKind.StreamableHttp;

    public McpServerInfo? Server { get; set; } = new("Stub", "1.0", "2026-07-28", new McpServerCapabilities(true, true, false, false));

    public bool IsConnected => _connected;

    public List<McpToolDescriptor> Tools { get; } = [];

    public McpException? ConnectFailure { get; set; }

    public Func<Task>? BeforeConnect { get; set; }

    public Func<McpToolDescriptor, JsonElement, CancellationToken, Task<McpToolResult>> OnCall { get; set; } =
        (tool, _, _) => Task.FromResult(Sample.Text("called " + tool.Name));

    public List<(string Tool, string Arguments)> Calls { get; } = [];

    public int ConnectCalls { get; private set; }

    public int ListCalls { get; private set; }

    public int Disposed { get; private set; }

    public event EventHandler? ToolsChanged;

    public event EventHandler? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectCalls++;
        if (BeforeConnect is not null)
        {
            await BeforeConnect().WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        _connected = true;
    }

    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        ListCalls++;
        return Task.FromResult<IReadOnlyList<McpToolDescriptor>>([.. Tools]);
    }

    public Task<McpToolResult> CallToolAsync(McpToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add((tool.Name, arguments.GetRawText()));
        return OnCall(tool, arguments, cancellationToken);
    }

    public McpException? PingFailure { get; set; }

    public int PingCalls { get; private set; }

    public Task PingAsync(CancellationToken cancellationToken = default)
    {
        PingCalls++;
        return PingFailure is null ? Task.CompletedTask : Task.FromException(PingFailure);
    }

    public void RaiseToolsChanged() => ToolsChanged?.Invoke(this, EventArgs.Empty);

    public void Drop()
    {
        _connected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        Disposed++;
        _connected = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Makes the stub clients a test gave it, one per connection, and remembers them.</summary>
internal sealed class StubClientFactory(Func<InstalledIntegration, StubMcpClient> make) : IMcpClientFactory
{
    // Two apps are connected to side by side, so clients are made on two threads at once.
    private readonly object _gate = new();

    public List<StubMcpClient> Created { get; } = [];

    public int CreateCalls
    {
        get
        {
            lock (_gate)
            {
                return Created.Count;
            }
        }
    }

    public IMcpClient Create(InstalledIntegration integration)
    {
        var client = make(integration);
        lock (_gate)
        {
            Created.Add(client);
        }

        return client;
    }
}

/// <summary>Settings a test fixes, with Local Only on or off.</summary>
internal static class TestSettings
{
    public static FixedSettings LocalOnly(bool localOnly) =>
        new() { Current = new Assistant.Core.Settings.AppSettings { Privacy = new Assistant.Core.Settings.PrivacySettings { LocalOnly = localOnly } } };
}
