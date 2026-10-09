using System.Text;
using Assistant.Core.Domain;
using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Generation;

/// <summary>One step of the engine's streamed answer, as <see cref="ChatCompletionStream"/> reads it.</summary>
internal abstract record ChatCompletionEvent;

/// <summary>Text to append to the answer, which is private content (PROJECT_SPEC §3.2).</summary>
internal sealed record ChatTextEvent(string Text) : ChatCompletionEvent
{
    // Keeps the answer out of ToString, and so out of logs.
    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Length = {Text.Length}");
        return true;
    }
}

/// <summary>A tool call the model asked for, whole: the engine streams it in pieces, which are joined first.</summary>
internal sealed record ChatToolCallEvent(ToolCall Call) : ChatCompletionEvent;

/// <summary>The answer is complete: why it stopped, and the token counts when the engine gave them.</summary>
internal sealed record ChatFinishedEvent(GenerationStopReason Reason) : ChatCompletionEvent
{
    public int? PromptTokens { get; init; }

    public int? OutputTokens { get; init; }
}
