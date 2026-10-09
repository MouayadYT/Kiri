namespace Assistant.Core.Voice;

/// <summary>Where the text-to-speech engine that is chosen stands.</summary>
/// <param name="EngineId">The identifier of the engine (<see cref="Assistant.Core.Settings.TextToSpeechModels"/>) the status is about.</param>
/// <param name="Engine">Where the engine stands.</param>
public sealed record TextToSpeechStatus(string EngineId, VoiceEngineStatus Engine)
{
    /// <summary>Whether the engine is loaded and ready.</summary>
    public bool IsReady => Engine.IsReady;
}

/// <summary>
/// One answer being spoken while it is still being written (PROJECT_SPEC §4.2, step 125). The writer feeds it the answer's text as it arrives and the
/// voice starts at the first whole sentence or clause; the writer is never held up by it. Stopping it silences the voice for this answer only: the
/// answer carries on being written, and nothing later in it is ever said.
/// </summary>
public interface ISpokenResponse
{
    /// <summary>Which response this is.</summary>
    Guid Id { get; }

    /// <summary>
    /// Adds the next words of the answer. It returns at once: the text is cut into pieces and queued, and the voice makes and plays them on its own
    /// threads. Words that come after the response was stopped are ignored.
    /// </summary>
    void Append(string? text);

    /// <summary>The answer is whole: whatever is left is said, and the response finishes when the last of it has been heard.</summary>
    void Complete();

    /// <summary>
    /// Stops the voice for this answer now: what is playing goes quiet, what is queued is dropped, what is being made is given up, and no later text
    /// is said. Does nothing for an answer that is already stopped or finished.
    /// </summary>
    void Stop();

    /// <summary>Whether the response was stopped, by <see cref="Stop"/>, by another response taking over, or because the voice could not speak.</summary>
    bool IsStopped { get; }

    /// <summary>Whether the response is over: everything was heard, or it was stopped.</summary>
    bool IsFinished { get; }

    /// <summary>How long it was from the first words of the answer to the first sound handed to the speakers, or <see langword="null"/> before there is any.</summary>
    TimeSpan? TimeToFirstAudio { get; }

    /// <summary>Raised once, when the response is over (<see cref="IsFinished"/>), on any thread.</summary>
    event EventHandler? Finished;
}

/// <summary>One text that was spoken in a benchmark.</summary>
/// <param name="Name">What the text is for, such as "Short answer".</param>
/// <param name="Characters">How long the text is.</param>
/// <param name="TimeToFirstAudio">From asking for the speech to the first block of it.</param>
/// <param name="SynthesisTime">From asking to the whole text spoken (not played).</param>
/// <param name="AudioDuration">How long the speech lasts.</param>
public sealed record TextToSpeechSample(string Name, int Characters, TimeSpan TimeToFirstAudio, TimeSpan SynthesisTime, TimeSpan AudioDuration)
{
    /// <summary>How many seconds of speech were made for each second it took: more than 1 keeps ahead of playback.</summary>
    public double SpeedMultiple => SynthesisTime > TimeSpan.Zero ? AudioDuration.TotalSeconds / SynthesisTime.TotalSeconds : 0;
}

/// <summary>What the chosen text-to-speech engine does on this PC, measured by speaking a few texts without playing them.</summary>
/// <param name="EngineId">The engine that was measured.</param>
/// <param name="LoadTime">How long loading the engine took the last time it was loaded, or <see langword="null"/> when that was not measured.</param>
/// <param name="Samples">What each text took.</param>
/// <param name="Failure">Why the measurement could not be made, in words for the user, or <see langword="null"/>.</param>
public sealed record TextToSpeechBenchmarkResult(string EngineId, TimeSpan? LoadTime, IReadOnlyList<TextToSpeechSample> Samples, string? Failure = null)
{
    /// <summary>Whether texts were measured.</summary>
    public bool HasSamples => Samples.Count > 0;

    /// <summary>The middle of the times to first audio.</summary>
    public TimeSpan MedianTimeToFirstAudio => HasSamples
        ? Samples.Select(sample => sample.TimeToFirstAudio).OrderBy(time => time).ElementAt(Samples.Count / 2)
        : TimeSpan.Zero;

    /// <summary>The time to first audio of the shortest text, which is what the voice's first piece of an answer is like.</summary>
    public TimeSpan ShortestTimeToFirstAudio => HasSamples ? Samples.OrderBy(sample => sample.Characters).First().TimeToFirstAudio : TimeSpan.Zero;

    /// <summary>Seconds of speech made per second of work over all the texts, more than 1 when the voice keeps ahead of playback.</summary>
    public double SpeedMultiple
    {
        get
        {
            var work = Samples.Sum(sample => sample.SynthesisTime.TotalSeconds);
            return work > 0 ? Samples.Sum(sample => sample.AudioDuration.TotalSeconds) / work : 0;
        }
    }

    /// <summary>The characters made into speech per second of work.</summary>
    public double CharactersPerSecond
    {
        get
        {
            var work = Samples.Sum(sample => sample.SynthesisTime.TotalSeconds);
            return work > 0 ? Samples.Sum(sample => sample.Characters) / work : 0;
        }
    }
}

/// <summary>
/// Speaks the Assistant's answers aloud (PROJECT_SPEC §4.2, step 125), with the text-to-speech engine the user chose, entirely on this PC. The answer's
/// text goes in as it is written, is cut into sentences and clauses and made into speech on a thread of its own while the writer carries on, and the
/// speech is queued to the speakers as it is made, so the voice starts after the first piece and not after the whole answer. The engine is one of three
/// behind <see cref="ITextToSpeechEngine"/>; nothing here depends on which.
/// </summary>
public interface ITextToSpeechService : IDisposable
{
    /// <summary>Which engine is chosen and where it stands.</summary>
    TextToSpeechStatus Status { get; }

    /// <summary>Raised when <see cref="Status"/> changes, on any thread.</summary>
    event EventHandler? StatusChanged;

    /// <summary>Whether an answer is being spoken, or is waiting for its first piece to be spoken. Stopping it ends this.</summary>
    bool IsSpeaking { get; }

    /// <summary>Raised when <see cref="IsSpeaking"/> changes, on any thread.</summary>
    event EventHandler? SpeakingChanged;

    /// <summary>Whether the engine stays loaded however long it is not used. Otherwise it is let go of after a while, to free its memory.</summary>
    bool KeepLoaded { get; set; }

    /// <summary>
    /// Chooses the engine. Anything that is being said stops first, the engine that was loaded is let go of, and with <paramref name="load"/> the new one
    /// is loaded and made ready at once; without it, it loads when it is first needed. Choosing the engine that is chosen already loads it, if asked,
    /// and changes nothing else. Never throws for an engine that cannot be loaded: <see cref="Status"/> says so.
    /// </summary>
    Task SelectEngineAsync(string engineId, bool load = true, CancellationToken cancellationToken = default);

    /// <summary>Loads the chosen engine if it is not loaded, so that the first answer is spoken without waiting for it. Never throws.</summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops speech and releases model files before deleting or reconfiguring a voice.</summary>
    Task UnloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Starts a response, which takes over from any other that is being spoken: that one is stopped. Feed it the answer's text as it comes.
    /// </summary>
    ISpokenResponse BeginResponse();

    /// <summary>Stops everything that is being said, at once, and drops everything that is queued.</summary>
    void StopAll();

    /// <summary>
    /// Measures the chosen engine: how soon each of a few texts starts and how fast they are made, without playing them. Anything that is being said
    /// stops first. Never throws: a failure is in the result.
    /// </summary>
    Task<TextToSpeechBenchmarkResult> BenchmarkAsync(CancellationToken cancellationToken = default);
}
