using Assistant.ModelHost.FakeEngine;
using Assistant.ModelHost.Processes;
using Assistant.ModelHost.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.ModelHost.Tests;

/// <summary>
/// The fake engine set up as the bundled runtime: its executable stands in for llama-server.exe, and a scenario file in
/// a temp folder stands in for the model.
/// </summary>
internal sealed class FakeEngineSetup : IDisposable
{
    private static readonly string ExecutablePath =
        Path.Combine(AppContext.BaseDirectory, "Assistant.ModelHost.FakeEngine.exe");

    private FakeEngineSetup(string directory, string scenarioPath)
    {
        Directory = directory;
        ScenarioPath = scenarioPath;
        SocketDirectory = Path.Combine(directory, "sockets");
        Locator = new StubRuntimeLocator(
            ModelRuntimeStatus.Ready(new ModelRuntime(AppContext.BaseDirectory, ExecutablePath)));
    }

    public string Directory { get; }

    public string ScenarioPath { get; }

    public string SocketDirectory { get; }

    public StubRuntimeLocator Locator { get; }

    /// <summary>The launch whose model is the scenario.</summary>
    public ModelProcessLaunch Launch => new(ScenarioPath);

    /// <summary>How many times the engine has been launched.</summary>
    public int Launches
    {
        get
        {
            var path = FakeEngineReport.CountPath(ScenarioPath);
            return File.Exists(path) ? int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
    }

    /// <summary>A scenario in a new temp folder, under <paramref name="folderName"/> when given.</summary>
    public static FakeEngineSetup Create(FakeEngineScenario scenario, string? folderName = null)
    {
        var directory = TestRuntimes.NewTempDirectory();
        var scenarioDirectory = folderName is null ? directory : Path.Combine(directory, folderName);
        System.IO.Directory.CreateDirectory(scenarioDirectory);
        var scenarioPath = Path.Combine(scenarioDirectory, "model.gguf");
        scenario.Save(scenarioPath);
        return new FakeEngineSetup(directory, scenarioPath);
    }

    /// <summary>Options with quick restarts, so a test sees a whole series in well under a second.</summary>
    public ModelProcessOptions Options(int maxRestarts = 3) => new(SocketDirectory)
    {
        StartTimeout = TimeSpan.FromSeconds(20),
        RestartPolicy = new ModelProcessRestartPolicy
        {
            MaxRestarts = maxRestarts,
            InitialDelay = TimeSpan.FromMilliseconds(20),
            MaxDelay = TimeSpan.FromMilliseconds(200),
        },
    };

    public ModelProcessManager CreateManager(ModelProcessOptions? options = null, ILoggerFactory? loggers = null) =>
        new(
            Locator,
            options ?? Options(),
            TimeProvider.System,
            loggers?.CreateLogger<ModelProcessManager>() ?? NullLogger<ModelProcessManager>.Instance);

    /// <summary>A file in the setup's folder that starts like a GGUF file, such as a multimodal projector.</summary>
    public string CreateGguf(string name) => CreateFile(name, FakeEngineScenario.Magic + "-projector");

    /// <summary>A file in the setup's folder with <paramref name="content"/>.</summary>
    public string CreateFile(string name, string content)
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Another scenario in the setup's folder, as another model.</summary>
    public string CreateScenario(string name, FakeEngineScenario scenario)
    {
        var path = Path.Combine(Directory, name);
        scenario.Save(path);
        return path;
    }

    /// <summary>What launch <paramref name="launch"/> reported.</summary>
    public FakeEngineReport Report(int launch) => FakeEngineReport.Load(ScenarioPath, launch);

    public void Dispose() => TestRuntimes.DeleteDirectory(Directory);
}

/// <summary>Records every state a manager moves through, so a test can wait for one.</summary>
internal sealed class StateRecorder : IDisposable
{
    private readonly IModelProcessManager _manager;
    private readonly List<ModelProcessStateChangedEventArgs> _changes = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StateRecorder(IModelProcessManager manager)
    {
        _manager = manager;
        manager.StateChanged += OnChanged;
    }

    public IReadOnlyList<ModelProcessState> States
    {
        get
        {
            lock (_changes)
            {
                return _changes.Select(change => change.State).ToArray();
            }
        }
    }

    /// <summary>Waits until <paramref name="state"/> has been entered <paramref name="times"/> times.</summary>
    public async Task WaitForAsync(ModelProcessState state, int times = 1)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            Task changed;
            lock (_changes)
            {
                if (_changes.Count(change => change.State == state) >= times)
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(timeout.Token);
        }
    }

    public void Dispose() => _manager.StateChanged -= OnChanged;

    private void OnChanged(object? sender, ModelProcessStateChangedEventArgs change)
    {
        TaskCompletionSource changed;
        lock (_changes)
        {
            _changes.Add(change);
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }
}
