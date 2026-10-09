using System.Net.Http.Headers;
using System.Net.Http.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Assistant.Core.Voice;

namespace Assistant.Voice;

/// <summary>OpenAI-compatible speech, requesting raw 24 kHz mono PCM; keys never enter settings or logs.</summary>
public sealed class ApiTextToSpeechEngine(VoiceSettings settings, ISecretStore secrets, HttpClient http, ISettingsService? settingsService = null) : ITextToSpeechEngine
{
    public static string SecretNameFor(string endpoint) => "voice.api-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(endpoint))).ToLowerInvariant()[..40];
    private string? _key;
    public TextToSpeechModel Model => TextToSpeechModels.CustomApi;
    public int SampleRate => 24000;

    public void Load()
    {
        if (!SpeechApiAddress.IsValid(settings.SpeechApiEndpoint)) throw new VoiceEngineException(VoiceEngineFailure.LoadFailed, "Enter a valid HTTPS speech endpoint, or a local HTTP endpoint.");
        _key = secrets.GetAsync(SecretNameFor(settings.SpeechApiEndpoint)).GetAwaiter().GetResult();
    }

    public void Synthesize(string text, SpeechAudioHandler onAudio, CancellationToken cancellationToken)
    {
        if (settingsService?.LoadAsync(cancellationToken).GetAwaiter().GetResult().Privacy.LocalOnly == true && !new Uri(settings.SpeechApiEndpoint).IsLoopback)
            throw new VoiceEngineException(VoiceEngineFailure.Failed, "Local Only is on. Allow remote speech in Voice setup, or choose a local endpoint.");
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.SpeechApiEndpoint);
        request.Content = JsonContent.Create(new { model = settings.SpeechApiModel, voice = settings.SpeechApiVoice, input = text, response_format = "pcm" });
        if (!string.IsNullOrEmpty(_key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        var stopped = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) throw new VoiceEngineException(VoiceEngineFailure.Failed, $"The speech API returned HTTP {(int)response.StatusCode}. Check the endpoint, key and model.");
            if (response.Content.Headers.ContentType?.MediaType is { } type && (type.Contains("json", StringComparison.OrdinalIgnoreCase) || type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)))
                throw new VoiceEngineException(VoiceEngineFailure.Failed, "The speech API returned text instead of PCM audio.");
            if (response.Content.Headers.ContentType?.MediaType is { } audioType && audioType is not ("audio/pcm" or "audio/raw" or "application/octet-stream"))
                throw new VoiceEngineException(VoiceEngineFailure.Failed, "Configure this endpoint to return raw PCM audio at 24 kHz.");
            using var input = response.Content.ReadAsStream(timeout.Token);
            var bytes = new byte[8192];
            var floats = new float[4096];
            var pending = -1;
            var total = 0;
            int count;
            while ((count = input.ReadAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult()) > 0)
            {
                total += count;
                if (total > 32_000_000) throw new VoiceEngineException(VoiceEngineFailure.Failed, "The speech API response was too large.");
                if (total == count && count >= 4 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8))
                    throw new VoiceEngineException(VoiceEngineFailure.Failed, "This endpoint returned WAV audio. Configure it to return raw PCM (24 kHz, mono, 16-bit).");
                var samples = 0;
                for (var i = 0; i < count; i++)
                {
                    if (pending < 0) pending = bytes[i];
                    else { floats[samples++] = (short)(pending | bytes[i] << 8) / 32768f; pending = -1; }
                }
                if (samples > 0 && !onAudio(floats.AsSpan(0, samples))) { stopped = true; throw new OperationCanceledException(cancellationToken); }
            }
            if (total == 0 || pending >= 0) throw new VoiceEngineException(VoiceEngineFailure.Failed, "The speech API returned incomplete audio.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { throw new VoiceEngineException(VoiceEngineFailure.Failed, "The speech API could not be reached. Check the endpoint and connection."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !stopped)
        { throw new VoiceEngineException(VoiceEngineFailure.Failed, "The speech API timed out. Try again."); }
    }

    public void Dispose() => _key = null;
}
