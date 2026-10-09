using System.IO;
using System.Windows.Input;
using Assistant.Core.Assets;
using Assistant.Core.ModelProfiles;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>One model profile in the Model page's list: what it is, where its files go, and whether they are there.</summary>
public sealed class ModelProfileItem : NotifyingObject
{
    private string _location = string.Empty;
    private string _status = string.Empty;
    private bool _isInstalled;
    private bool _isRecommended;
    private bool _canCheck;
    private bool _isChecking;
    private readonly RelayCommand _check;

    internal ModelProfileItem(ModelProfile profile, bool isDefault, Action<ModelProfileItem>? check = null)
    {
        Profile = profile;
        IsDefault = isDefault;
        Summary = Describe(profile);
        _check = new RelayCommand(_ => check?.Invoke(this), _ => _canCheck && !_isChecking && check is not null);
    }

    /// <summary>The profile.</summary>
    public ModelProfile Profile { get; }

    /// <summary>The profile's identifier.</summary>
    public string Id => Profile.Id;

    /// <summary>What the profile is called.</summary>
    public string DisplayName => Profile.DisplayName;

    /// <summary>Whether it is the profile of the catalog that every PC can run.</summary>
    public bool IsDefault { get; }

    /// <summary>Whether it is the profile recommended for this PC (step 124).</summary>
    public bool IsRecommended
    {
        get => _isRecommended;
        internal set => Set(ref _isRecommended, value);
    }

    /// <summary>What the model is: its size, quantization, whether it reads images, and the memory it likes.</summary>
    public string Summary { get; }

    /// <summary>The folder its model file is expected in.</summary>
    public string Location
    {
        get => _location;
        private set => Set(ref _location, value);
    }

    /// <summary>Whether its files are installed, in words.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Whether its model file is there.</summary>
    public bool IsInstalled
    {
        get => _isInstalled;
        private set => Set(ref _isInstalled, value);
    }

    /// <summary>Whether its files came packaged with the Assistant, so that they can be checked against the package's checksums.</summary>
    public bool CanCheck
    {
        get => _canCheck;
        private set
        {
            if (Set(ref _canCheck, value))
            {
                _check.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether its files are being checked now.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (Set(ref _isChecking, value))
            {
                _check.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Checks the packaged files against the package's checksums, reading every one of them.</summary>
    public ICommand CheckCommand => _check;

    /// <summary>
    /// Shows where the profile's files are and whether they are there, from how it resolves and, for the files that came packaged with the
    /// Assistant, where the package's checksums say they stand.
    /// </summary>
    internal void Show(ResolvedModel? resolved, AssetGroupState? packaged = null)
    {
        if (resolved is null)
        {
            Location = string.Empty;
            Status = "Not available with the current settings.";
            IsInstalled = false;
            CanCheck = false;
            IsChecking = false;
            return;
        }

        Location = Path.GetDirectoryName(resolved.Files.ModelPath) ?? resolved.Files.ModelPath;
        IsInstalled = resolved.IsInstalled;

        // The package lists this profile and its files are the ones in use, or are missing from where the package put them.
        var listed = packaged is { Status: not AssetGroupStatus.NotPackaged } && (resolved.IsPackaged || !resolved.IsInstalled);
        CanCheck = listed && packaged!.IsInstalled;
        IsChecking = listed && packaged!.Status == AssetGroupStatus.Checking;
        Status = !listed ? PlainStatus(resolved) : PackagedStatus(resolved, packaged!);
    }

    private string PlainStatus(ResolvedModel resolved) =>
        !resolved.IsInstalled
            ? "Not installed. Put its model file in this folder to use it."
            : resolved.ProjectorMissing
                ? "Installed, but its projector file is missing, so it can't read images."
                : resolved.BelowRecommendedMemory
                    ? $"Installed. This PC has less memory than the {Profile.RecommendedMemoryGiB} GB it works best with, so it may be slow."
                    : "Installed.";

    // A packaged model is described by what the package says of it first, then by the same cautions as any other.
    private string PackagedStatus(ResolvedModel resolved, AssetGroupState packaged)
    {
        var text = packaged.Status switch
        {
            AssetGroupStatus.Verified => "Installed with the Assistant, and its files match what was packaged.",
            AssetGroupStatus.Unverified => "Installed with the Assistant. Its files are checked before it is first used.",
            _ => AssetStatusText.Describe(packaged, "model"),
        };
        if (!packaged.IsInstalled)
        {
            return text;
        }

        if (resolved.ProjectorMissing)
        {
            text += " Its projector file is missing, so it can't read images.";
        }

        if (resolved.BelowRecommendedMemory)
        {
            text += $" This PC has less memory than the {Profile.RecommendedMemoryGiB} GB it works best with, so it may be slow.";
        }

        return text;
    }

    private static string Describe(ModelProfile profile)
    {
        var reads = profile.SupportsVision ? "reads text and images" : "reads text";
        return $"About {profile.ParametersBillions:0.#} billion parameters · {profile.Quantization} · {reads} · works best with {profile.RecommendedMemoryGiB} GB of memory";
    }
}

/// <summary>A hardware preset the Model page offers, or the automatic choice.</summary>
/// <param name="Id">The preset's identifier, or <see langword="null"/> for the one that suits this PC.</param>
/// <param name="Label">What the user is shown.</param>
public sealed record PresetChoice(string? Id, string Label);
