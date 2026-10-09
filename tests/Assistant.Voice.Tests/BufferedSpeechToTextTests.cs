using Assistant.Core.Voice;
using Assistant.Voice;
using Xunit;

namespace Assistant.Voice.Tests;

public sealed class BufferedSpeechToTextTests
{
    [Fact]
    public async Task EndpointTranscribesBoundedAudioAndOmitsTheWakeWordSeed()
    {
        var heard = new List<int>();
        using var service = new BufferedSpeechToTextService((audio, _) => { heard.Add(audio.Length); return Task.FromResult("test question"); });
        using var session = service.StartSession(new() { IgnoreWordsBefore = TimeSpan.FromSeconds(0.5) });
        var ended = new TaskCompletionSource<SpeechTranscriptEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Transcribed += (_, transcript) => ended.TrySetResult(transcript);
        session.Push(Enumerable.Repeat((short)10000, 16000).ToArray());
        session.Push(new short[16000]);
        var result = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("test question", result.Text); Assert.Equal(SpeechEndReason.Endpoint, result.Ended);
        Assert.Equal(24000, Assert.Single(heard));
    }
    [Fact]
    public async Task SilenceDoesNotLoadAModelAndEndsAsNoSpeech()
    {
        var loads = 0;
        using var service = new BufferedSpeechToTextService((_, _) => throw new Exception("No decoding expected"), _ => { loads++; return Task.CompletedTask; });
        using var session = service.StartSession(new() { InitialSilence = TimeSpan.FromSeconds(1) });
        var ended = new TaskCompletionSource<SpeechTranscriptEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Transcribed += (_, transcript) => ended.TrySetResult(transcript);
        session.Push(new short[16000]);
        Assert.Equal(SpeechEndReason.NoSpeech, (await ended.Task.WaitAsync(TimeSpan.FromSeconds(5))).Ended);
        Assert.Equal(0, loads);
    }
    [Fact]
    public async Task CancelingRecognitionDoesNotDeliverAPrivateTranscript()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new BufferedSpeechToTextService(async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); return "never delivered"; }
            finally { canceled.SetResult(); }
        });
        var session = service.StartSession(); var transcripts = 0;
        session.Transcribed += (_, _) => transcripts++;
        session.Push(Enumerable.Repeat((short)10000, 16000).ToArray()); session.Finish();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); session.Dispose();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.IsEnded); Assert.Equal(0, transcripts);
    }
}
