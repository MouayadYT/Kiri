using System.Text.Json.Serialization;

namespace Assistant.Core.Domain;

/// <summary>What an <see cref="AssistantResponseChunk"/> carries.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssistantResponseChunkType>))]
public enum AssistantResponseChunkType
{
    /// <summary>Response text to append to the answer.</summary>
    TextDelta = 0,

    /// <summary>The model requested a tool call.</summary>
    ToolCall = 1,

    /// <summary>A tool call finished.</summary>
    ToolResult = 2,

    /// <summary>Files the answer is grounded in.</summary>
    Sources = 3,

    /// <summary>A notice the UI must show, such as disclosed truncation or a fallback.</summary>
    Notice = 4,

    /// <summary>
    /// A warning the UI must show that some of the user's context, or of the conversation, was left out or cut short
    /// because it did not fit the model's token budget, so the answer does not take all of it into account.
    /// </summary>
    ContextWarning = 5,

    /// <summary>
    /// An integration the Assistant found, reviewed and offers to install (PROJECT_SPEC §4.8, step 108). The UI shows it as a panel with the
    /// facts and Install and Cancel; nothing has been downloaded or run, and nothing is until the user clicks Install.
    /// </summary>
    IntegrationOffer = 6,
}
