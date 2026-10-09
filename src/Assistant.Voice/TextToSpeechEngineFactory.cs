using Assistant.Core.Settings;
using Assistant.Core.Contracts;
using Assistant.Core.Storage;
using Assistant.Core.Voice;

namespace Assistant.Voice;

/// <summary>
/// Makes the adapter for each of the three text-to-speech engines (PROJECT_SPEC §4.2, step 125). All three are run by the same runtime from their own
/// files; this is the one place that names them, so adding a fourth is a model, a folder and a case in <see cref="SherpaTextToSpeechEngine.Layout"/>.
/// </summary>
public sealed class TextToSpeechEngineFactory : ITextToSpeechEngineFactory
{
    private readonly VoiceModelFolders _folders;
    private readonly int _threads;
    private readonly ISettingsService? _settings;
    private readonly ISecretStore? _secrets;
    private readonly HttpClient? _http;
    private readonly AppPaths? _paths;

    /// <summary>Creates the factory.</summary>
    /// <param name="folders">Where the engines' files are.</param>
    /// <param name="threads">How many threads an engine may use. Two keeps a voice well ahead of its own playback on an ordinary processor and leaves the rest for the language model.</param>
    public TextToSpeechEngineFactory(VoiceModelFolders folders, int threads = 2, ISettingsService? settings = null, ISecretStore? secrets = null, HttpClient? http = null, AppPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _folders = folders;
        _threads = Math.Max(1, threads);
        _settings = settings;
        _secrets = secrets;
        _http = http;
        _paths = paths;
    }

    /// <inheritdoc/>
    public ITextToSpeechEngine Create(TextToSpeechModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Id == TextToSpeechModels.CustomApi.Id && _settings is not null && _secrets is not null && _http is not null)
            return new ApiTextToSpeechEngine(_settings.LoadAsync().GetAwaiter().GetResult().Voice, _secrets, _http, _settings);
        if (_settings is not null && _paths is not null && _settings.LoadAsync().GetAwaiter().GetResult().Voice.TextToSpeechDevice is { } device && device.StartsWith("cuda:", StringComparison.Ordinal))
            return new CudaTextToSpeechEngine(model, _paths, device, modelFolder: _folders.Resolve(model.Id));
        if (TextToSpeechModels.Find(model.Id) is null)
        {
            throw new VoiceEngineException(VoiceEngineFailure.NotInstalled, "There is no such voice.");
        }

        return new SherpaTextToSpeechEngine(model, _folders, _threads);
    }
}
