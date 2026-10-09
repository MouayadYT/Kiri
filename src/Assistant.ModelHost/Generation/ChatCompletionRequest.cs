using System.Buffers;
using System.Text.Json;
using Assistant.Core.Domain;
using Assistant.Core.Imaging;
using Assistant.Core.ModelHosting;

namespace Assistant.ModelHost.Generation;

/// <summary>
/// The body of the engine's OpenAI-style chat completion request (<c>POST /v1/chat/completions</c>) for a
/// <see cref="GenerationRequest"/>: the instructions as the system message, the conversation, the tools, the output
/// limit, and a streamed answer that ends with the token counts. The engine applies the model's chat template.
/// </summary>
/// <remarks>
/// The images of a <see cref="GenerateMultimodalRequest"/> go with its last user message, as OpenAI-style content
/// parts: each image as a base64 <c>data:</c> URI, in order, then the message's text. The engine reads them with the
/// model's multimodal projector; it is never given a URL to fetch.
/// </remarks>
internal static class ChatCompletionRequest
{
    /// <summary>Where the engine takes the request.</summary>
    public const string Path = "/v1/chat/completions";

    /// <summary>Writes the request body for <paramref name="request"/> in UTF-8 JSON.</summary>
    /// <exception cref="ArgumentException">A tool's input schema is not JSON.</exception>
    public static byte[] Write(GenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteMessages(writer, request);
            WriteTools(writer, request.Tools);
            if (request.MaxOutputTokens is { } maxTokens)
            {
                writer.WriteNumber("max_tokens", maxTokens);
            }

            if (request.Temperature is { } temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }

            writer.WriteBoolean("stream", true);
            writer.WriteStartObject("stream_options");
            writer.WriteBoolean("include_usage", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMessages(Utf8JsonWriter writer, GenerationRequest request)
    {
        writer.WriteStartArray("messages");
        if (!string.IsNullOrEmpty(request.Instructions))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", request.Instructions);
            writer.WriteEndObject();
        }

        var images = request is GenerateMultimodalRequest multimodal ? multimodal.Images : [];
        var imagesAt = images.Count > 0 ? LastUserMessage(request.Messages) : -1;
        for (var index = 0; index < request.Messages.Count; index++)
        {
            var message = request.Messages[index];
            writer.WriteStartObject();
            writer.WriteString("role", RoleOf(message.Role));
            if (index == imagesAt)
            {
                WriteContentWithImages(writer, message.Text, images);
            }
            else
            {
                writer.WriteString("content", message.Text);
            }

            if (message.Role == MessageRole.Assistant && message.ToolCalls.Count > 0)
            {
                writer.WriteStartArray("tool_calls");
                foreach (var call in message.ToolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", call.Id);
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", call.ToolName);
                    writer.WriteString("arguments", call.ArgumentsJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (message.Role == MessageRole.Tool && message.ToolCallId is not null)
            {
                writer.WriteString("tool_call_id", message.ToolCallId);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // The images first, then the text, as vision models are trained to read them.
    private static void WriteContentWithImages(Utf8JsonWriter writer, string text, IReadOnlyList<ReadOnlyMemory<byte>> images)
    {
        writer.WriteStartArray("content");
        foreach (var image in images)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "image_url");
            writer.WriteStartObject("image_url");
            writer.WriteString("url", DataUri(image.Span));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (text.Length > 0)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string DataUri(ReadOnlySpan<byte> image) =>
        $"data:{ImageFormats.MediaType(ImageFormats.Detect(image))};base64,{Convert.ToBase64String(image)}";

    // The request's images belong to its last user message; a prompt without one gets them on its last message.
    private static int LastUserMessage(IReadOnlyList<PromptMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == MessageRole.User)
            {
                return index;
            }
        }

        return messages.Count - 1;
    }

    private static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<ToolDefinition> tools)
    {
        if (tools.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("tools");
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("parameters");
            try
            {
                writer.WriteRawValue(tool.InputSchemaJson);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException("A tool's input schema is not valid JSON.", nameof(tools), exception);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string RoleOf(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}
