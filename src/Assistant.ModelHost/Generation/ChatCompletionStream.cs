using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Assistant.Core.Domain;
using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Generation;

/// <summary>
/// Reads the engine's streamed chat completion, a server-sent event stream of OpenAI-style chunks
/// (<c>data: {...}</c>, ending with <c>data: [DONE]</c>), into <see cref="ChatCompletionEvent"/>s: the answer's text as
/// it comes, then any tool calls whole, then why it stopped with the token counts.
/// </summary>
/// <remarks>
/// Only the fields it needs are read: each chunk's text, tool call pieces and finish reason, and the usage. The rest,
/// such as the model's path that every chunk repeats and any reasoning the model does before it answers, is skipped.
/// An error the engine reports, in the stream or as the response, becomes a <see cref="GenerationException"/> whose
/// code comes from the error's type, never its message.
/// </remarks>
internal static class ChatCompletionStream
{
    private const string ContextExceededType = "exceed_context_size_error";

    /// <summary>Reads the stream to its end.</summary>
    /// <exception cref="GenerationException">
    /// The engine reported an error, sent a chunk that is not JSON, or the stream ended before the answer did.
    /// </exception>
    /// <exception cref="IOException">The connection to the engine broke.</exception>
    public static async IAsyncEnumerable<ChatCompletionEvent> ReadAsync(
        Stream body,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var answer = new Answer();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (TryReadField(line, "data", out var data))
            {
                if (data == "[DONE]")
                {
                    // A stream that says it is done, and never said why, finished.
                    answer.Reason ??= GenerationStopReason.Completed;
                    break;
                }

                if (answer.Read(data) is { Length: > 0 } text)
                {
                    yield return new ChatTextEvent(text);
                }
            }
            else if (TryReadField(line, "error", out var error))
            {
                throw new GenerationException(CodeOfError(error));
            }

            // Blank lines end an event; comments and other fields carry nothing the answer needs.
        }

        if (answer.Reason is not { } reason)
        {
            // The engine broke off, for example because it stopped while it generated.
            throw new GenerationException(ModelHostErrorCode.GenerationFailed);
        }

        foreach (var call in answer.ToolCalls())
        {
            yield return new ChatToolCallEvent(call);
        }

        yield return new ChatFinishedEvent(reason) { PromptTokens = answer.PromptTokens, OutputTokens = answer.OutputTokens };
    }

    /// <summary>
    /// The error an engine's error body stands for: <see cref="ModelHostErrorCode.ContextExceeded"/> for a prompt
    /// longer than the context, otherwise <see cref="ModelHostErrorCode.GenerationFailed"/>.
    /// </summary>
    public static ModelHostErrorCode CodeOfError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var inner))
            {
                root = inner;
            }

            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.ValueEquals(ContextExceededType)
                ? ModelHostErrorCode.ContextExceeded
                : ModelHostErrorCode.GenerationFailed;
        }
        catch (JsonException)
        {
            return ModelHostErrorCode.GenerationFailed;
        }
    }

    // "data: x" and "data:x" are both the field "data" with the value "x".
    private static bool TryReadField(string line, string name, out string value)
    {
        value = "";
        if (line.Length <= name.Length || line[name.Length] != ':' || !line.StartsWith(name, StringComparison.Ordinal))
        {
            return false;
        }

        value = line[(name.Length + 1)..];
        if (value.StartsWith(' '))
        {
            value = value[1..];
        }

        return true;
    }

    // What the chunks said so far.
    private sealed class Answer
    {
        private readonly SortedDictionary<int, PartialCall> _calls = [];

        public GenerationStopReason? Reason { get; set; }

        public int? PromptTokens { get; private set; }

        public int? OutputTokens { get; private set; }

        // Reads one chunk, and returns the text it adds.
        public string? Read(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new GenerationException(ModelHostErrorCode.GenerationFailed);
                }

                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                {
                    throw new GenerationException(CodeOfError(json));
                }

                ReadUsage(root);
                return ReadChoice(root);
            }
            catch (JsonException exception)
            {
                throw new GenerationException(ModelHostErrorCode.GenerationFailed, exception);
            }
        }

        public IEnumerable<ToolCall> ToolCalls() => _calls.Select(entry => new ToolCall(
            entry.Value.Id ?? $"call_{entry.Key}", entry.Value.Name ?? "", entry.Value.Arguments.ToString()));

        private string? ReadChoice(JsonElement root)
        {
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                Reason = finish.ValueEquals("length") ? GenerationStopReason.OutputLimit : GenerationStopReason.Completed;
            }

            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                ReadToolCalls(calls);
            }

            return delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;
        }

        // Each piece names its call by index; the id and name come once, the arguments in pieces to be joined.
        private void ReadToolCalls(JsonElement calls)
        {
            var position = 0;
            foreach (var piece in calls.EnumerateArray())
            {
                if (piece.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var index = piece.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
                    && indexElement.TryGetInt32(out var value)
                    ? value
                    : position;
                position++;
                if (!_calls.TryGetValue(index, out var call))
                {
                    _calls[index] = call = new PartialCall();
                }

                if (piece.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    call.Id ??= id.GetString();
                }

                if (!piece.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    call.Name ??= name.GetString();
                }

                if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
                {
                    call.Arguments.Append(arguments.GetString());
                }
            }
        }

        private void ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.ValueKind == JsonValueKind.Number
            && prompt.TryGetInt32(out var promptTokens)
                && promptTokens >= 0)
            {
                PromptTokens = promptTokens;
            }

            if (usage.TryGetProperty("completion_tokens", out var output) && output.ValueKind == JsonValueKind.Number
            && output.TryGetInt32(out var outputTokens)
                && outputTokens >= 0)
            {
                OutputTokens = outputTokens;
            }
        }
    }

    private sealed class PartialCall
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
