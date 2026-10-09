using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;

namespace Assistant.Tools.Screen;

/// <summary>
/// <c>read_screen_text</c> (PROJECT_SPEC §4.6, §4.8): reads the words in the screenshot the user attached to the conversation, with where
/// each line is, using the OCR engine on this PC. It supplements the vision model, which is offered it only in a conversation that has a
/// screenshot: for the exact words, numbers, error codes or names the model cannot make out reliably from the picture. The screenshot was
/// captured with the Screen Capture permission, and reading it sends nothing anywhere; what is returned is data, never instructions (P9).
/// </summary>
public sealed class ReadScreenTextTool(IScreenText screens) : ITool
{
    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = new(
        ScreenToolResults.ReadScreenText,
        "Read the words in the screenshot the user attached to this conversation, with where each line is. Use it when you need the " +
        "exact words, numbers, error codes, names or paths from the screenshot that you cannot make out reliably from the picture. " +
        "Do not use it when you can read the picture yourself or the question is not about the screenshot.",
        """
        {
          "type": "object",
          "properties": {
            "contains": {
              "type": "string",
              "description": "Only the lines that contain this text, such as an error code or a name. Leave it out to read every line."
            }
          }
        }
        """,
        RiskLevel.ReadOnly)
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    /// <inheritdoc/>
    public bool IsOffered(ToolContext context) => screens.IsAvailable && screens.HasScreenshot(context.ConversationId);

    /// <inheritdoc/>
    public bool IsFocused(ToolContext context) => IsOffered(context);

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!screens.HasScreenshot(context.ConversationId))
        {
            return Failed(call, "There is no screenshot attached to this conversation any more. Tell the user.");
        }

        if (!screens.IsAvailable)
        {
            return Failed(call, "Text cannot be read from the screenshot: no OCR language is installed on this PC. Tell the user.");
        }

        var contains = arguments.TryGetProperty("contains", out var asked) && asked.ValueKind == JsonValueKind.String ? asked.GetString() : null;
        if (await screens.ReadAsync(context.ConversationId, cancellationToken).ConfigureAwait(false) is not { } text)
        {
            return Failed(call, "The text in the screenshot could not be read. Tell the user.");
        }

        return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, ScreenToolResults.Text(text, contains));
    }

    private static ToolResult Failed(ToolCall call, string message) =>
        new(call.Id, call.ToolName, ToolResultStatus.Failed, FileToolResults.Error(message));
}
