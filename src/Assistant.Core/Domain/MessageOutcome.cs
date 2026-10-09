using System.Text.Json.Serialization;

namespace Assistant.Core.Domain;

/// <summary>How an assistant <see cref="Message"/> ended, which decides whether its answer is drawn with a note about it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MessageOutcome>))]
public enum MessageOutcome
{
    /// <summary>The message is whole: the model finished it, or it never streamed. Every user message is complete.</summary>
    Complete = 0,

    /// <summary>The user stopped the answer; what it said before that stays.</summary>
    Stopped = 1,

    /// <summary>The answer could not be finished, and its last paragraph says why; what it said before that stays.</summary>
    Failed = 2,
}
