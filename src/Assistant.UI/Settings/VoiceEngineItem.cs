using System.Windows.Input;
using Assistant.Core.Assets;
using Assistant.Core.Settings;

namespace Assistant.UI.Settings;

/// <summary>One text-to-speech engine in the Voice page's list: what it is, whether its files are installed on this PC and whether they are whole.</summary>
public sealed class VoiceEngineItem : NotifyingObject
{
    private TextToSpeechAssetStatus _status;
    private bool _userFiles;

    internal VoiceEngineItem(TextToSpeechAssetStatus status, string location)
    {
        _status = status;
        Location = location;
    }

    /// <summary>The engine.</summary>
    public TextToSpeechModel Model => _status.Model;

    /// <summary>What the engine is called.</summary>
    public string DisplayName => _status.Model.DisplayName;

    /// <summary>What sets it apart, in a line.</summary>
    public string Summary => _status.Model.Summary;

    /// <summary>The folder its files belong in, whether or not they are there.</summary>
    public string Location { get; }

    /// <summary>Where its files stand, in words.</summary>
    public string Status => _userFiles
        ? "Installed in your voices folder. It is used as it is, without a check against the package."
        : _status.Description;

    /// <summary>Whether it can be used: its files are all there and match what was packaged, or it is in the user's own voices folder.</summary>
    public bool IsAvailable => _userFiles || _status.IsAvailable;

    /// <summary>Whether its files are there, checked or not.</summary>
    public bool IsInstalled => _userFiles || _status.IsInstalled;

    /// <summary>Whether its files are being checked now.</summary>
    public bool IsChecking => _status.State.Status == AssetGroupStatus.Checking;

    /// <summary>Whether its files are not what they should be: some are missing, changed or could not be read.</summary>
    public bool NeedsAttention => !_userFiles && _status.State.Status is AssetGroupStatus.Incomplete or AssetGroupStatus.Damaged or AssetGroupStatus.Unreadable;

    // The user's own voices folder has this engine's files, which are used before the packaged ones and are not checked.
    internal void ShowUserFiles(bool has)
    {
        if (_userFiles != has)
        {
            _userFiles = has;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(NeedsAttention));
        }
    }

    internal void Show(TextToSpeechAssetStatus status)
    {
        _status = status;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(NeedsAttention));
    }
}
