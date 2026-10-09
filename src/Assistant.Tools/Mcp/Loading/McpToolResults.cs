using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Assistant.Core.Domain;
using Assistant.Core.Tools;

namespace Assistant.Tools.Mcp;

/// <summary>
/// What a connected app's answer to a tool call becomes for the model (PROJECT_SPEC §4.8, P9, step 104): the app's words, as data, marked as
/// coming from the app and cut to a size a conversation can carry. Text is kept; a picture or a sound is only noted, since a model that reads this
/// cannot use them and a long Base64 would only fill its context; a link is named and never followed; a result the tool gave as structured JSON is kept
/// when it came with no text. A tool that says it failed is a failed result with its words, so that the model can correct the call. Nothing the app
/// sends is ever treated as an instruction, and none of it is logged.
/// </summary>
internal static class McpToolResults
{
    /// <summary>The most characters of one text of a result that are kept.</summary>
    public const int MaxTextLength = 16_000;

    /// <summary>The most characters of the whole result that are kept, kept below what the executor accepts (<see cref="ToolExecutor.MaxResultLength"/>).</summary>
    public const int MaxResultLength = 48_000;

    /// <summary>The most characters of an app's error message that are passed on.</summary>
    public const int MaxErrorLength = 500;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The result of <paramref name="call"/> for what <paramref name="result"/> says the tool returned.</summary>
    /// <param name="call">The call.</param>
    /// <param name="appName">The app's name.</param>
    /// <param name="result">What the app returned.</param>
    /// <param name="checkExams">
    /// Whether the tool reads a calendar (step 116): the events in the answer are then checked for exams, and the Assistant's reading is put beside what the app said as
    /// <c>exam_check</c>, marked as a hint (<see cref="Assistant.Tools.Calendar.ExamHints"/>). What the app returned is kept as it is.
    /// </param>
    public static ToolResult Map(ToolCall call, string appName, McpToolResult result, bool checkExams = false)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsError)
        {
            var words = McpText.Clean(string.Join(' ', result.Content.Where(block => block.Kind == McpContentKind.Text).Select(block => block.Text)), MaxErrorLength);
            return ToolErrors.Result(
                call, ToolResultStatus.Failed, ToolErrors.Failed,
                words is null ? $"{appName} reported an error." : $"{appName} reported an error: {words}");
        }

        using var stream = new MemoryStream();
        var truncated = false;
        var remaining = MaxResultLength;
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("source", "connected_app");
            writer.WriteString("app", appName);
            writer.WriteStartArray("content");
            foreach (var block in result.Content)
            {
                if (remaining <= 0)
                {
                    truncated = true;
                    break;
                }

                WriteBlock(writer, block, ref remaining, ref truncated);
            }

            writer.WriteEndArray();

            // Structured content is kept when there is no text to say the same, and when it is not too large.
            if (result.StructuredContent is { } structured && !result.Content.Any(block => block.Kind is McpContentKind.Text or McpContentKind.Resource))
            {
                var raw = structured.GetRawText();
                if (raw.Length <= remaining)
                {
                    writer.WritePropertyName("data");
                    structured.WriteTo(writer);
                }
                else
                {
                    truncated = true;
                }
            }

            if (truncated)
            {
                writer.WriteBoolean("truncated", true);
            }

            // The Assistant's own reading of the events, from the words they came with, beside what the app said and not in place of it.
            if (checkExams && Assistant.Tools.Calendar.ExamHints.Check(result) is { } hints)
            {
                writer.WritePropertyName("exam_check");
                hints.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return new ToolResult(call.Id, call.ToolName ?? string.Empty, ToolResultStatus.Succeeded, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteBlock(Utf8JsonWriter writer, McpContentBlock block, ref int remaining, ref bool truncated)
    {
        writer.WriteStartObject();
        switch (block.Kind)
        {
            case McpContentKind.Text:
                writer.WriteString("type", "text");
                writer.WriteString("text", Shorten(block.Text, ref remaining, ref truncated));
                break;
            case McpContentKind.Resource when block.Text is not null:
                writer.WriteString("type", "text");
                writer.WriteString("text", Shorten(block.Text, ref remaining, ref truncated));
                break;
            case McpContentKind.Resource:
                writer.WriteString("type", "resource");
                writer.WriteString("note", "A resource without text came with the result. It is not shown.");
                break;
            case McpContentKind.ResourceLink:
                writer.WriteString("type", "link");
                if (McpText.Clean(block.Name, 200) is { } name)
                {
                    writer.WriteString("name", name);
                }

                if (McpText.Clean(block.Uri, 500) is { } uri)
                {
                    writer.WriteString("uri", uri);
                }

                writer.WriteString("note", "A link to a resource. It is not opened.");
                break;
            case McpContentKind.Image:
                writer.WriteString("type", "image");
                writer.WriteString("note", "A picture came with the result. It is not shown.");
                break;
            default:
                writer.WriteString("type", "audio");
                writer.WriteString("note", "A sound came with the result. It is not played.");
                break;
        }

        writer.WriteEndObject();
    }

    // The text, cut so that one text is not more than a page and the whole result is not more than a conversation can carry.
    private static string Shorten(string? text, ref int remaining, ref bool truncated)
    {
        text ??= string.Empty;
        var allowed = Math.Min(MaxTextLength, remaining);
        if (text.Length > allowed)
        {
            truncated = true;
            text = text[..Math.Max(0, allowed)];
        }

        remaining -= text.Length;
        return text;
    }
}
