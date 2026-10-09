using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Memory;
using Assistant.Core.Tools;

namespace Assistant.Tools.Memory;

/// <summary>
/// <c>remember</c>: keeps one thing the user has told the Assistant about themselves, so that it is known in later conversations (Settings, under
/// Memory, where the user reads, rewrites and removes what is kept). It is for what the user states or asks to be remembered, and never for a
/// general question or for anything read from a page, a file or a message. It writes one line to a file on this PC and nothing else, the user sees
/// the step, and they can take it back in Settings, so nobody is asked first. A note that carries a web address is not kept: a note is a fact about
/// the user, and never somewhere to go.
/// </summary>
public static class MemoryTools
{
    /// <summary>The name of the tool that keeps a note.</summary>
    public const string RememberName = "remember";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] Addresses = ["http://", "https://", "www."];

    // Offered only for a request that says something is to be kept, or states who someone is or what the user likes: it runs without a question.
    private static readonly RequestTopic Keeping = new(
        "remember", "forget", "forgot", "note", "memorize", "memorise", "mind", "prefer", "preference", "preferences", "favorite", "favourite",
        "always", "never", "called", "name", "call");

    /// <summary><c>remember</c>, over <paramref name="memory"/>.</summary>
    public static ITool Remember(IMemoryStore memory, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(clock);
        var definition = ToolDefinition.Create(
            RememberName,
            "Save one lasting fact or preference the user has just told you about themselves, so you know it in later conversations: who someone is, what they " +
            "like, how they want something done, or which thing they mean by a name. Use it when the user states such a fact, answers a question of yours with " +
            "one, or asks you to remember something. Do not use it for a general question, for something you looked up, or for anything that comes from a " +
            "page, a file or a message. Call it at most once for a request, and never in place of what the user asked for: when they ask you to do something " +
            "(send a message, switch a device, set a timer), do that. Who someone is to message is remember_person's, not this tool's.",
            [
                new ToolParameter(
                    "note", ToolParameterType.String,
                    "The fact as one short sentence about the user, such as: The user's sister is called Lena. Or: The user wants temperatures in Celsius.",
                    MaxLength: MemoryRules.MaxTextLength),
            ],
            RiskLevel.SideEffect,
            timeout: TimeSpan.FromSeconds(10),
            runsWithoutAsking: true);
        // One note for a request: a model that goes on calling this spends the few steps a request has on it and never gets to what was asked.
        var noted = new System.Collections.Concurrent.ConcurrentDictionary<Guid, string>();
        return new HandlerTool(definition, async (call, arguments, context, cancellationToken) =>
        {
            if (context.Request is { } request && noted.TryGetValue(context.ConversationId, out var earlier) && earlier == request)
            {
                return ToolErrors.Result(
                    call, ToolResultStatus.Failed, ToolErrors.Repeated,
                    "A note was already saved for this request. Do not call remember again: do what the user asked now, or answer them.");
            }

            var note = MemoryRules.Clean(arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("note", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null);
            if (note.Length < 3)
            {
                return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, "Give the fact to remember as one short sentence.", ToolUsage.Describe(definition));
            }

            if (Addresses.Any(address => note.Contains(address, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolErrors.Result(
                    call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, "A note cannot hold a web address. Remember only what the user said about themselves.");
            }

            var kept = await memory.SaveAsync(MemoryEntry.Note(note, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            if (kept is not null && context.Request is { } asked)
            {
                if (noted.Count >= 64)
                {
                    noted.Clear();
                }

                noted[context.ConversationId] = asked;
            }

            return kept is null
                ? ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, "The note could not be saved right now. Tell the user it was not remembered.")
                : new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, Remembered(kept.Text));
        },

        // While someone is being messaged, who they are is remember_person's to keep: this tool is not there to be mistaken for it.
        isOffered: context => context.Request is null || (Keeping.IsAbout(context) && !Assistant.Tools.Messaging.MessagingConversations.IsAbout(context)),
        focused: true);
    }

    private static string Remembered(string note)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("remembered", true);
            writer.WriteString("note", note);
            writer.WriteString(
                "say",
                "Saved. Do not call remember again for this request. If the user asked for something else too, do it now; otherwise tell them in a few words that " +
                "you will remember it (they can change it in Settings, under Memory).");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
