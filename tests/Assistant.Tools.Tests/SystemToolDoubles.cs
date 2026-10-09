using Assistant.Core.Contracts;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;

namespace Assistant.Tools.Tests;

/// <summary>The installed applications, as a test says they are.</summary>
internal sealed class FakeApplications(params InstalledApplication[] applications) : IApplicationCatalog
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<InstalledApplication> Current => applications;

    public bool IsLoaded => true;

    public Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InstalledApplication>>(applications);

    public void WarmUp()
    {
    }

    public void Invalidate()
    {
    }
}

/// <summary>An application launcher that records what it was asked to start, and whether it could.</summary>
internal sealed class RecordingApplicationLauncher : IApplicationLauncher
{
    public List<string> Launched { get; } = [];

    public bool Works { get; set; } = true;

    public bool Launch(string applicationId)
    {
        Launched.Add(applicationId);
        return Works;
    }
}

/// <summary>A file launcher that records what it was asked to open or show, and whether it could.</summary>
internal sealed class RecordingFileLauncher : IFileLauncher
{
    public List<string> Opened { get; } = [];

    public List<string> Revealed { get; } = [];

    public bool Works { get; set; } = true;

    public bool Open(string path)
    {
        Opened.Add(path);
        return Works;
    }

    public bool Reveal(string path)
    {
        Revealed.Add(path);
        return Works;
    }
}

/// <summary>Windows' own list of actions, as a test says it behaves: the volume it holds, what was asked of it, and the folders it knows.</summary>
internal sealed class FakeSystem : ISystemActions
{
    public List<string> Calls { get; } = [];

    public VolumeState? State { get; set; } = new VolumeState(40, false);

    public bool Works { get; set; } = true;

    public Dictionary<SystemFolder, string> Folders { get; } = new()
    {
        [SystemFolder.Downloads] = @"C:\Users\someone\Downloads",
        [SystemFolder.Documents] = @"C:\Users\someone\Documents",
        [SystemFolder.Home] = @"C:\Users\someone",
    };

    public bool OpenWindowsSettings() => Record("settings");

    public bool LockWorkstation() => Record("lock");

    public bool SetMuted(bool muted)
    {
        Record(muted ? "mute" : "unmute");
        if (Works && State is { } state)
        {
            State = state with { Muted = muted };
        }

        return Works;
    }

    public bool SetVolume(int percent)
    {
        Record("volume " + percent);
        if (Works && State is { } state)
        {
            State = new VolumeState(Math.Clamp(percent, 0, 100), percent == 0 && state.Muted);
        }

        return Works;
    }

    public int? ChangeVolume(int percent) => null;

    public VolumeState? GetVolume() => Works ? State : null;

    public string? GetFolder(SystemFolder folder) => Folders.GetValueOrDefault(folder);

    private bool Record(string call)
    {
        Calls.Add(call);
        return Works;
    }
}

/// <summary>A screenshot taker that records what it was asked, and answers as the test says.</summary>
internal sealed class FakeScreenshots : IScreenshotTaker
{
    public List<Guid> Asked { get; } = [];

    public ScreenshotOutcome Outcome { get; set; } = new(ScreenshotStatus.Taken, 1920, 1080);

    public Task<ScreenshotOutcome> TakeAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        Asked.Add(conversationId);
        return Task.FromResult(Outcome);
    }
}
