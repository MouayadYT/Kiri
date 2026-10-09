using Assistant.Core.Settings;

namespace Assistant.Core.Assets;

/// <summary>A text-to-speech engine and where its packaged files stand.</summary>
/// <param name="Model">The engine.</param>
/// <param name="State">Where its files stand.</param>
public sealed record TextToSpeechAssetStatus(TextToSpeechModel Model, AssetGroupState State)
{
    /// <summary>Whether the engine can be used: its files are all there and match what was packaged.</summary>
    public bool IsAvailable => State.IsVerified;

    /// <summary>Whether its files are there, checked or not.</summary>
    public bool IsInstalled => State.IsInstalled;

    /// <summary>What to tell the user.</summary>
    public string Description => AssetStatusText.Describe(State, "voice");
}

/// <summary>
/// Which text-to-speech engines are installed on this PC (PROJECT_SPEC §3.5, §4.2, step 123). Each engine in <see cref="TextToSpeechModels"/> has a
/// folder of its own under the packaged voices folder, named for its identifier, listed in that folder's manifest and checked like a model. They
/// run on this PC from those files: nothing here, or in what uses it, needs a speech server.
/// </summary>
public interface ITextToSpeechAssets
{
    /// <summary>Every engine and where it stands, the default first. Reads no file's contents.</summary>
    IReadOnlyList<TextToSpeechAssetStatus> Peek();

    /// <summary>Checks every engine's files (<see cref="IPackagedAssets.VerifyAsync"/>) and returns where each stands.</summary>
    Task<IReadOnlyList<TextToSpeechAssetStatus>> VerifyAsync(AssetCheckMode mode = AssetCheckMode.Verify, CancellationToken cancellationToken = default);

    /// <summary>Checks the engine <paramref name="modelId"/> and returns where it stands.</summary>
    Task<TextToSpeechAssetStatus> VerifyAsync(string modelId, AssetCheckMode mode = AssetCheckMode.Verify, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes sure the engine <paramref name="modelId"/> can be used: for a caller that wants the check made and refused as an exception before it uses the engine's files (the voice runtime itself checks while it loads, in <c>VoiceModelFolders</c>).
    /// </summary>
    /// <exception cref="AssetIntegrityException">The engine is not installed or its files do not match what was packaged.</exception>
    /// <exception cref="ArgumentException">There is no such engine.</exception>
    Task EnsureAvailableAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>The folder the engine's files belong in, whether or not it is installed.</summary>
    string FolderOf(TextToSpeechModel model);
}

/// <summary>The default <see cref="ITextToSpeechAssets"/>, over <see cref="IPackagedAssets"/>.</summary>
public sealed class TextToSpeechAssets : ITextToSpeechAssets
{
    private readonly IPackagedAssets _assets;

    /// <summary>Creates the service over <paramref name="assets"/>.</summary>
    public TextToSpeechAssets(IPackagedAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
    }

    /// <inheritdoc/>
    public IReadOnlyList<TextToSpeechAssetStatus> Peek() =>
        [.. TextToSpeechModels.All.Select(model => new TextToSpeechAssetStatus(model, _assets.Peek(AssetKind.Voice, model.Id)))];

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TextToSpeechAssetStatus>> VerifyAsync(AssetCheckMode mode = AssetCheckMode.Verify, CancellationToken cancellationToken = default)
    {
        var statuses = new List<TextToSpeechAssetStatus>();
        foreach (var model in TextToSpeechModels.All)
        {
            statuses.Add(await VerifyAsync(model.Id, mode, cancellationToken).ConfigureAwait(false));
        }

        return statuses;
    }

    /// <inheritdoc/>
    public async Task<TextToSpeechAssetStatus> VerifyAsync(string modelId, AssetCheckMode mode = AssetCheckMode.Verify, CancellationToken cancellationToken = default)
    {
        var model = TextToSpeechModels.Find(modelId) ?? throw new ArgumentException("There is no such text-to-speech engine.", nameof(modelId));
        return new TextToSpeechAssetStatus(
            model, await _assets.VerifyAsync(AssetKind.Voice, model.Id, mode, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async Task EnsureAvailableAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var status = await VerifyAsync(modelId, AssetCheckMode.Verify, cancellationToken).ConfigureAwait(false);
        if (!status.IsAvailable)
        {
            throw new AssetIntegrityException(AssetKind.Voice, status.State);
        }
    }

    /// <inheritdoc/>
    public string FolderOf(TextToSpeechModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(_assets.Paths.VoicesDirectory, model.Id);
    }
}
