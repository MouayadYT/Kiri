using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Windows.Audio;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Shell;

/// <summary>
/// What the quick actions do to Windows (PROJECT_SPEC §4.1), as a small closed set: open the Settings app, lock the session, change the
/// speakers' volume and mute switch, and say where a folder of the user's is. It starts no program a caller names and runs no command
/// line: the one thing it hands to the shell is the fixed address <c>ms-settings:</c>, and a folder is only ever a path Windows itself
/// gave for a known folder. A path is never logged, and a failure is only <see langword="false"/> or <see langword="null"/>.
/// </summary>
public sealed class WindowsSystemActions : ISystemActions
{
    /// <summary>The address of the Settings app.</summary>
    internal const string SettingsAddress = "ms-settings:";

    private readonly Func<ProcessStartInfo, bool> _start;
    private readonly ISystemVolume _volume;
    private readonly Func<bool> _lock;
    private readonly Func<SystemFolder, string?> _folders;

    /// <summary>Creates the actions over the real Windows.</summary>
    public WindowsSystemActions()
        : this(StartWithShell, new SystemVolume(), User32.LockWorkStation, KnownFolders.Get)
    {
    }

    internal WindowsSystemActions(
        Func<ProcessStartInfo, bool> start, ISystemVolume volume, Func<bool> lockWorkstation, Func<SystemFolder, string?> folders)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _volume = volume ?? throw new ArgumentNullException(nameof(volume));
        _lock = lockWorkstation ?? throw new ArgumentNullException(nameof(lockWorkstation));
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));
    }

    /// <inheritdoc/>
    public bool OpenWindowsSettings()
    {
        try
        {
            return _start(new ProcessStartInfo(SettingsAddress) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public bool LockWorkstation() => _lock();

    /// <inheritdoc/>
    public bool SetMuted(bool muted) => _volume.SetMuted(muted);

    /// <inheritdoc/>
    public bool SetVolume(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (!_volume.SetPercent(percent))
        {
            return false;
        }

        // Raising the volume ends mute, as the volume keys do; a volume of nothing leaves the switch as it is.
        return percent == 0 || _volume.SetMuted(false);
    }

    /// <inheritdoc/>
    public int? ChangeVolume(int percent)
    {
        if (!_volume.TryGetState(out var current, out _))
        {
            return null;
        }

        var next = Math.Clamp(current + percent, 0, 100);
        if (!_volume.SetPercent(next) || (percent > 0 && !_volume.SetMuted(false)))
        {
            return null;
        }

        return next;
    }

    /// <inheritdoc/>
    public VolumeState? GetVolume() => _volume.TryGetState(out var percent, out var muted) ? new VolumeState(percent, muted) : null;

    /// <inheritdoc/>
    public bool? GetDoNotDisturb() => DoNotDisturb.Get();

    /// <inheritdoc/>
    public bool SetDoNotDisturb(bool on) => DoNotDisturb.Set(on);

    /// <inheritdoc/>
    public string? GetFolder(SystemFolder folder) => _folders(folder) is { Length: > 0 } path && Path.IsPathFullyQualified(path) ? path : null;

    private static bool StartWithShell(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
        return true;
    }
}

/// <summary>The user's known folders, as Windows says where they are (a Downloads folder the user moved is where they moved it).</summary>
internal static partial class KnownFolders
{
    private static readonly Dictionary<SystemFolder, Guid> Ids = new()
    {
        [SystemFolder.Home] = new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173"),
        [SystemFolder.Desktop] = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
        [SystemFolder.Documents] = new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
        [SystemFolder.Downloads] = new Guid("374DE290-123F-4565-9164-39C4925E467B"),
        [SystemFolder.Pictures] = new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        [SystemFolder.Music] = new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"),
        [SystemFolder.Videos] = new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
    };

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    /// <summary>The full path of <paramref name="folder"/>, or <see langword="null"/> when Windows has none.</summary>
    public static string? Get(SystemFolder folder)
    {
        if (!Ids.TryGetValue(folder, out var id) || SHGetKnownFolderPath(id, 0, 0, out var pointer) < 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }
}
