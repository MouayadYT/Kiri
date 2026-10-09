using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Assistant.Core.Domain;

namespace Assistant.Core.Tools;

/// <summary>
/// How a call that did not work is told to the model (PROJECT_SPEC §4.8): a JSON object with the reason in words (<c>error</c>), a fixed
/// <c>code</c> that says what kind of failure it was, and, when the call's arguments were what was wrong, how the tool is called
/// (<c>usage</c>), so that the model can correct the call in the next round and not guess. The words never repeat what the model
/// wrote or anything private. The code is for the code that reads results (the executor, the orchestrator, tests); the model reads the
/// words.
/// </summary>
public static class ToolErrors
{
    /// <summary>The call names no tool the app has.</summary>
    public const string UnknownTool = "unknown_tool";

    /// <summary>The arguments are not JSON, or are not what the tool's schema says.</summary>
    public const string InvalidArguments = "invalid_arguments";

    /// <summary>The permission the tool needs is off, or not available in this version.</summary>
    public const string PermissionOff = "permission_off";

    /// <summary>The tool is never run (it could destroy data).</summary>
    public const string NotAllowed = "not_allowed";

    /// <summary>The user did not allow the call when asked.</summary>
    public const string Declined = "declined";

    /// <summary>The user was asked and did not answer in time, so the call was not made.</summary>
    public const string NoAnswer = "no_answer";

    /// <summary>The user could not be asked (nothing could show the question), so the call was not made.</summary>
    public const string CouldNotAsk = "could_not_ask";

    /// <summary>The tool did not finish within its time limit and was given up.</summary>
    public const string TimedOut = "timed_out";

    /// <summary>The tool ran and failed, or threw.</summary>
    public const string Failed = "tool_failed";

    /// <summary>The same call was already made in this answer.</summary>
    public const string Repeated = "repeated_call";

    /// <summary>More calls came in one answer than are run.</summary>
    public const string TooManyCalls = "too_many_calls";

    /// <summary>The tool's result was larger than a result may be.</summary>
    public const string ResultTooLarge = "result_too_large";

    // A sum or a name reads as it is written, not with its plus or its quotes escaped, in what the model sees.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The JSON of a failure.</summary>
    /// <param name="code">What kind of failure it was, one of the codes above.</param>
    /// <param name="message">What went wrong, in words the model can act on.</param>
    /// <param name="usage">How the tool is called, when the arguments were what was wrong; otherwise <see langword="null"/>.</param>
    public static string Json(string code, string message, string? usage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteString("code", code);
            if (!string.IsNullOrWhiteSpace(usage))
            {
                writer.WriteString("usage", usage);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The result of <paramref name="call"/> that failed (or was declined) for the reason given.</summary>
    public static ToolResult Result(
        ToolCall call, ToolResultStatus status, string code, string message, string? usage = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        return new ToolResult(call.Id, call.ToolName ?? string.Empty, status, Json(code, message, usage));
    }

    /// <summary>Reads the code and the words of a failure as <see cref="Json"/> writes it, or of any <c>{"error": ...}</c> a tool returned.</summary>
    /// <returns><see langword="false"/> when <paramref name="outputJson"/> is not a failure.</returns>
    public static bool TryRead(string? outputJson, out string code, out string message)
    {
        code = string.Empty;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            message = error.GetString() ?? string.Empty;
            code = root.TryGetProperty("code", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() ?? string.Empty : Failed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
