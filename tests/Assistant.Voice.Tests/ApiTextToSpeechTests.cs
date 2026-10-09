using System.Net;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Assistant.Core.Voice;
using Xunit;

namespace Assistant.Voice.Tests;

public sealed class ApiTextToSpeechTests
{
    [Fact]
    public void CustomEndpointGetsProtectedKeyAndSpeechParametersAndReturnsAudio()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("https://example.test/speech", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("secret-test-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            Assert.Equal("voice-model", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("test-voice", json.RootElement.GetProperty("voice").GetString());
            Assert.Equal("pcm", json.RootElement.GetProperty("response_format").GetString());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0, 0, 64, 0, 128]) };
        });
        using var http = new HttpClient(handler);
        using var engine = new ApiTextToSpeechEngine(new VoiceSettings { SpeechApiEndpoint = "https://example.test/speech", SpeechApiModel = "voice-model", SpeechApiVoice = "test-voice" }, new Secrets(), http);
        engine.Load(); List<float> audio = [];
        engine.Synthesize("Hello.", block => { audio.AddRange(block.ToArray()); return true; }, CancellationToken.None);
        Assert.Equal([0f, 0.5f, -1f], audio);
        Assert.Equal(24000, engine.SampleRate);
    }

    [Fact]
    public void ApiFailureDoesNotExposeResponseBodyOrCredentials()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("private text secret-test-key") }));
        using var engine = new ApiTextToSpeechEngine(new VoiceSettings(), new Secrets(), http);
        engine.Load();
        var exception = Assert.Throws<VoiceEngineException>(() => engine.Synthesize("private text", _ => true, CancellationToken.None));
        Assert.Contains("401", exception.Message);
        Assert.DoesNotContain("secret-test-key", exception.ToString());
        Assert.DoesNotContain("private text", exception.ToString());
    }

    [Theory]
    [InlineData("http://example.test/speech")]
    [InlineData("https://user:password@example.test/speech")]
    public void UnprotectedOrCredentialBearingEndpointsAreRefused(string endpoint)
    {
        using var http = new HttpClient();
        using var engine = new ApiTextToSpeechEngine(new VoiceSettings { SpeechApiEndpoint = endpoint }, new Secrets(), http);
        Assert.Throws<VoiceEngineException>(engine.Load);
    }

    [Fact]
    public void TurningLocalOnlyBackOnStopsAnAlreadyLoadedRemoteEngine()
    {
        var requests = 0;
        var privacy = new SettingsSource(new AppSettings { Privacy = new PrivacySettings { LocalOnly = false } });
        using var http = new HttpClient(new Handler(_ => { requests++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0]) }; }));
        using var engine = new ApiTextToSpeechEngine(new VoiceSettings(), new Secrets(), http, privacy);
        engine.Load(); engine.Synthesize("Allowed", _ => true, CancellationToken.None);
        privacy.Current = privacy.Current with { Privacy = privacy.Current.Privacy with { LocalOnly = true } };
        Assert.Throws<VoiceEngineException>(() => engine.Synthesize("Private", _ => true, CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Fact]
    public void LocalEndpointsWorkWithLocalOnlyAndKeysStayScopedToTheirEndpoint()
    {
        var secrets = new Secrets();
        const string endpoint = "http://localhost:8880/v1/audio/speech";
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0]) }));
        using var engine = new ApiTextToSpeechEngine(new VoiceSettings { SpeechApiEndpoint = endpoint }, secrets, http, new SettingsSource(new AppSettings()));
        engine.Load(); engine.Synthesize("Hello", _ => true, CancellationToken.None);
        Assert.Equal(ApiTextToSpeechEngine.SecretNameFor(endpoint), secrets.LastName);
        Assert.NotEqual(secrets.LastName, ApiTextToSpeechEngine.SecretNameFor("https://example.test/speech"));
        Assert.DoesNotContain("localhost", secrets.LastName!);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("audio/mpeg")]
    [InlineData("audio/wav")]
    public void NonPcmResponsesAreNotPlayed(string type)
    {
        using var http = new HttpClient(new Handler(_ => {
            var content = new ByteArrayContent([0, 0]);
            content.Headers.ContentType = new(type);
            return new(HttpStatusCode.OK) { Content = content };
        }));
        using var engine = new ApiTextToSpeechEngine(new VoiceSettings(), new Secrets(), http);
        engine.Load(); var played = false;
        Assert.Throws<VoiceEngineException>(() => engine.Synthesize("Hello", _ => { played = true; return true; }, CancellationToken.None));
        Assert.False(played);
    }

    [Fact]
    public void PlaybackCancellationIsNotReportedAsATimeout()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0]) }));
        using var engine = new ApiTextToSpeechEngine(new VoiceSettings(), new Secrets(), http);
        engine.Load();
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Synthesize("Hello", _ => false, CancellationToken.None));
    }

    private sealed class SettingsSource(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; set; } = current;
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Current = settings; return Task.CompletedTask; }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class Secrets : ISecretStore
    {
        public string? LastName { get; private set; }
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) { LastName = name; return Task.FromResult<string?>("secret-test-key"); }
        public Task SetAsync(string name, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
