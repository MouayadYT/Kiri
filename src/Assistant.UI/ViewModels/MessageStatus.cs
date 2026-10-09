namespace Assistant.UI.ViewModels;

/// <summary>Where an assistant message's answer stands: still coming in, or how it ended.</summary>
public enum MessageStatus
{
    /// <summary>The answer is whole: the model finished it, or it never streamed.</summary>
    Complete = 0,

    /// <summary>The answer is still coming in.</summary>
    Answering = 1,

    /// <summary>The user stopped the answer; what it said before that stays.</summary>
    Stopped = 2,

    /// <summary>The answer could not be finished, and its last paragraph says why; what it said before that stays.</summary>
    Failed = 3,
}
