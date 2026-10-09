namespace Assistant.Core.Assets;

/// <summary>Where a packaged asset stands (PROJECT_SPEC §3.5, step 123).</summary>
public enum AssetGroupStatus
{
    /// <summary>No manifest lists the asset, so nothing is expected of it: it is not packaged with this copy of the Assistant.</summary>
    NotPackaged,

    /// <summary>A manifest lists the asset, and none of its files is there.</summary>
    Missing,

    /// <summary>A manifest lists the asset, and some of its files are not there.</summary>
    Incomplete,

    /// <summary>Every file is there and has the size the manifest says, but the checksums have not been checked yet.</summary>
    Unverified,

    /// <summary>The checksums are being checked now.</summary>
    Checking,

    /// <summary>Every file is there and has the size and the SHA-256 the manifest says.</summary>
    Verified,

    /// <summary>A file does not have the size or the SHA-256 the manifest says, or the manifest itself cannot be read.</summary>
    Damaged,

    /// <summary>A file could not be read (it is locked, or access is denied), so it could not be checked.</summary>
    Unreadable,
}

/// <summary>How thoroughly an asset is checked.</summary>
public enum AssetCheckMode
{
    /// <summary>Only that each file is there and has its size, with a checksum trusted when this PC checked the very same file before. Reads no file's contents.</summary>
    Peek,

    /// <summary>The size of each file and its SHA-256, except where this PC checked the very same file (same size, same time stamp) before.</summary>
    Verify,

    /// <summary>The SHA-256 of every file, whatever was checked before. It catches a file that changed without its size or time stamp changing.</summary>
    Reverify,
}

/// <summary>Where one packaged asset stands, and which of its files are the problem when it is not whole.</summary>
/// <param name="GroupId">The asset's identifier: the model profile's or the engine's.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="ProblemFiles">The files, as the manifest names them, that are missing, damaged or could not be read. Empty when there is no problem.</param>
public sealed record AssetGroupState(string GroupId, AssetGroupStatus Status, IReadOnlyList<string> ProblemFiles)
{
    /// <summary>Whether the asset's files are all there, whether or not their checksums were checked.</summary>
    public bool IsInstalled => Status is AssetGroupStatus.Unverified or AssetGroupStatus.Checking or AssetGroupStatus.Verified;

    /// <summary>Whether the asset may be used: every file is there and matches the manifest.</summary>
    public bool IsVerified => Status == AssetGroupStatus.Verified;

    /// <summary>A state with no problem files.</summary>
    public static AssetGroupState Of(string groupId, AssetGroupStatus status) => new(groupId, status, []);
}
