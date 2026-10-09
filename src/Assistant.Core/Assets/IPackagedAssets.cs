using Assistant.Core.Contracts;

namespace Assistant.Core.Assets;

/// <summary>A model's files, or a voice's, are listed by the packaged manifest and do not match it.</summary>
public sealed class AssetIntegrityException : Exception
{
    /// <summary>Creates the exception for the asset in <paramref name="state"/>.</summary>
    public AssetIntegrityException(AssetKind kind, AssetGroupState state)
        : base("A packaged asset did not pass its integrity check.")
    {
        Kind = kind;
        State = state;
    }

    /// <summary>Which kind of asset it is.</summary>
    public AssetKind Kind { get; }

    /// <summary>Where the asset stands.</summary>
    public AssetGroupState State { get; }
}

/// <summary>
/// The assets that ship with an installed copy of the Assistant (PROJECT_SPEC §3.5, step 123): language models and text-to-speech engines in
/// their own folder beside the program files, each with a manifest of sizes and SHA-256 checksums. This tells what is installed and checks it, so
/// that a model is never loaded from files that are missing or do not match what was packaged.
/// </summary>
public interface IPackagedAssets
{
    /// <summary>Where the packaged assets are.</summary>
    PackagedAssetPaths Paths { get; }

    /// <summary>The identifiers of the groups the manifest of <paramref name="kind"/> lists, in its order. Empty when there is no manifest or it cannot be read.</summary>
    IReadOnlyList<string> GroupIds(AssetKind kind);

    /// <summary>
    /// Where the asset stands without reading any file's contents (<see cref="AssetCheckMode.Peek"/>): whether it is packaged, whether its files are
    /// there and whether this PC already checked them. Cheap, so a page may ask it whenever it is shown.
    /// </summary>
    AssetGroupState Peek(AssetKind kind, string groupId);

    /// <summary>
    /// Checks the asset against its manifest. A check of the same asset that is already running is joined, not repeated, and one caller's
    /// cancellation only ends that caller's wait. The state is published as an <c>AssetStateChanged</c> when the check begins and when it ends.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<AssetGroupState> VerifyAsync(
        AssetKind kind, string groupId, AssetCheckMode mode = AssetCheckMode.Verify, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks every asset of every kind that a manifest lists, one after another: the check the Assistant makes the first time it runs, and
    /// whenever the packaged files change, so that what the Assistant uses has been checked before it is asked for.
    /// </summary>
    Task VerifyAllAsync(AssetCheckMode mode = AssetCheckMode.Verify, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the packaged files <paramref name="files"/> would load: every file of the asset (the model, its projector and so on) that a manifest
    /// lists must be there and match. Files that no manifest lists (a model the user picked, or one in their own models folder) are not this
    /// method's concern and are allowed.
    /// </summary>
    /// <exception cref="AssetIntegrityException">A packaged file is missing, does not match its manifest or could not be checked.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task EnsureModelUsableAsync(ModelFiles files, CancellationToken cancellationToken = default);
}
