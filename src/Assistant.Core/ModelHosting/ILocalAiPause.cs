namespace Assistant.Core.ModelHosting;

/// <summary>Who paused the local AI (<see cref="ILocalAiPause"/>).</summary>
public enum LocalAiPauseReason
{
    /// <summary>The local AI is not paused.</summary>
    None = 0,

    /// <summary>The user paused it, with the tray menu's Pause Local AI.</summary>
    User = 1,

    /// <summary>Game mode paused it because a game is running; it resumes by itself when the game ends.</summary>
    GameMode = 2,
}

/// <summary>
/// Lets the user pause the local AI for a while (the tray menu's Pause Local AI, PROJECT_SPEC §4.9): the model is unloaded, which frees
/// the memory it holds, and nothing loads it again until the user resumes. It is not saved: the Assistant starts with the local AI on.
/// Game mode pauses it the same way while a game runs, and says so with its own <see cref="Reason"/>.
/// </summary>
public interface ILocalAiPause
{
    /// <summary>Whether the local AI is paused.</summary>
    bool IsPaused { get; }

    /// <summary>Who paused it, or <see cref="LocalAiPauseReason.None"/> while it is not paused.</summary>
    LocalAiPauseReason Reason => IsPaused ? LocalAiPauseReason.User : LocalAiPauseReason.None;

    /// <summary>Raised when <see cref="IsPaused"/> changes. It may come from any thread.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Pauses the local AI: from now on nothing loads the model, and the model that is loaded, or loading, is unloaded, which ends an
    /// answer that is being written with it. Pausing again changes nothing.
    /// </summary>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses the local AI as <see cref="PauseAsync(CancellationToken)"/> does, for <paramref name="reason"/>. What is already paused stays
    /// paused for the reason it was paused for.
    /// </summary>
    Task PauseAsync(LocalAiPauseReason reason, CancellationToken cancellationToken = default) => PauseAsync(cancellationToken);

    /// <summary>
    /// Lets the model load again. It is not loaded now: the next question loads it, as at the start. Resuming what is not paused
    /// changes nothing.
    /// </summary>
    void Resume();
}
