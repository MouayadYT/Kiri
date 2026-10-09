namespace Assistant.UI.Orb;

/// <summary>What the assistant orb shows it is doing.</summary>
public enum OrbState
{
    /// <summary>Calm. The orb is as in the reference, with only a faint living motion, and ignores sound.</summary>
    Idle,

    /// <summary>The microphone is on: the bright boundary inside the orb follows how loud the sound is.</summary>
    Listening,

    /// <summary>Working on an answer: a slow, steady shimmer passes along the boundary, whatever the sound.</summary>
    Thinking,

    /// <summary>Something failed: the orb sags and warms to a restrained red, and holds still.</summary>
    Error,
}
