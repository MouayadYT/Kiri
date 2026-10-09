namespace Assistant.Core.Voice;

/// <summary>Where a voice engine (a speaker, a recognizer or the wake-word listener) stands.</summary>
public enum VoiceEngineState
{
    /// <summary>Nothing has been asked of it, or it was let go of to free memory. It loads when it is first needed.</summary>
    NotLoaded,

    /// <summary>Its files are being read into memory.</summary>
    Loading,

    /// <summary>It is loaded and answers at once.</summary>
    Ready,

    /// <summary>It could not be loaded. <see cref="VoiceEngineStatus.Message"/> says why, in words for the user.</summary>
    Failed,

    /// <summary>Its files are not on this PC, or are damaged.</summary>
    NotInstalled,
}

/// <summary>Where a voice engine stands, with words for the user when it is not ready.</summary>
/// <param name="State">The state.</param>
/// <param name="Message">Why it failed or is not installed, in the Assistant's words (never a path or a file's contents), or <see langword="null"/>.</param>
/// <param name="LoadTime">How long loading it took, once it has been loaded.</param>
public sealed record VoiceEngineStatus(VoiceEngineState State, string? Message = null, TimeSpan? LoadTime = null)
{
    /// <summary>An engine nothing has asked for yet.</summary>
    public static VoiceEngineStatus Unloaded { get; } = new(VoiceEngineState.NotLoaded);

    /// <summary>Whether the engine is loaded and ready.</summary>
    public bool IsReady => State == VoiceEngineState.Ready;
}

/// <summary>What went wrong when a voice engine was loaded or used, in words that never hold a path or anything that was said.</summary>
public enum VoiceEngineFailure
{
    /// <summary>The engine's files are not on this PC.</summary>
    NotInstalled,

    /// <summary>The engine's files do not match what was packaged.</summary>
    FilesFailedCheck,

    /// <summary>The engine could not be loaded from its files.</summary>
    LoadFailed,

    /// <summary>The engine failed while it was working.</summary>
    Failed,
}

/// <summary>A voice engine could not be loaded or used. Its message is safe to show and to log.</summary>
public sealed class VoiceEngineException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">What went wrong.</param>
    /// <param name="message">The words for the user.</param>
    /// <param name="inner">The cause, if there is one. Never shown.</param>
    public VoiceEngineException(VoiceEngineFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    /// <summary>What went wrong.</summary>
    public VoiceEngineFailure Failure { get; }
}
