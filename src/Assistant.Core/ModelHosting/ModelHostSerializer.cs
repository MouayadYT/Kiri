using System.Buffers;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// Writes and reads the JSON envelope of one model-host message (<see cref="ModelHostProtocol"/>):
/// <c>{"v":1,"id":7,"type":"health","body":{}}</c>.
/// </summary>
public static class ModelHostSerializer
{
    private const string VersionProperty = "v";
    private const string IdProperty = "id";
    private const string TypeProperty = "type";
    private const string BodyProperty = "body";

    private static readonly ModelHostJsonContext Context = ModelHostJsonContext.Default;

    // The wire name of every message. A name never changes meaning; a new message gets a new name (ModelHostProtocol).
    private static readonly (string Name, JsonTypeInfo TypeInfo)[] Messages =
    [
        ("health", Context.HealthRequest),
        ("loadModel", Context.LoadModelRequest),
        ("unloadModel", Context.UnloadModelRequest),
        ("generateText", Context.GenerateTextRequest),
        ("generateMultimodal", Context.GenerateMultimodalRequest),
        ("cancelGeneration", Context.CancelGenerationRequest),
        ("shutdown", Context.ShutdownRequest),
        ("healthReport", Context.HealthReport),
        ("modelLoadProgress", Context.ModelLoadProgress),
        ("modelLoaded", Context.ModelLoaded),
        ("modelUnloaded", Context.ModelUnloaded),
        ("modelStatus", Context.ModelStatusReport),
        ("textDelta", Context.TextDelta),
        ("toolCall", Context.ToolCallGenerated),
        ("generationEnded", Context.GenerationEnded),
        ("shutdownAccepted", Context.ShutdownAccepted),
        ("error", Context.ModelHostError),
    ];

    private static readonly FrozenDictionary<Type, (string Name, JsonTypeInfo TypeInfo)> ByType =
        Messages.ToFrozenDictionary(message => message.TypeInfo.Type);

    private static readonly FrozenDictionary<string, JsonTypeInfo> ByName =
        Messages.ToFrozenDictionary(message => message.Name, message => message.TypeInfo, StringComparer.Ordinal);

    /// <summary>The wire name of each message type.</summary>
    public static IReadOnlyDictionary<Type, string> MessageNames { get; } =
        Messages.ToFrozenDictionary(message => message.TypeInfo.Type, message => message.Name);

    /// <summary>Writes <paramref name="message"/> in an envelope of the current protocol version.</summary>
    /// <param name="id">The request's id: the new id of a request, or the id of the request a reply answers.</param>
    /// <param name="message">The message.</param>
    /// <returns>The envelope in UTF-8, ready to be framed.</returns>
    /// <exception cref="ArgumentException"><paramref name="message"/> is not a message of the protocol.</exception>
    public static byte[] Serialize(long id, ModelHostMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        if (!ByType.TryGetValue(message.GetType(), out var entry))
        {
            throw new ArgumentException("The message is not part of the model-host protocol.", nameof(message));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(VersionProperty, ModelHostProtocol.Version);
            writer.WriteNumber(IdProperty, id);
            writer.WriteString(TypeProperty, entry.Name);
            writer.WritePropertyName(BodyProperty);
            JsonSerializer.Serialize(writer, message, entry.TypeInfo);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Reads an envelope. It never throws for bad input: a frame that cannot be read comes back with its
    /// <see cref="ModelHostFrame.Error"/>, and with its id whenever the id could be read, so the sender can be answered.
    /// </summary>
    public static ModelHostFrame Deserialize(ReadOnlyMemory<byte> utf8Json)
    {
        long id = 0;
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ModelHostFrame.ForError(0, ModelHostErrorCode.MalformedMessage);
            }

            // The id first, so that every later failure can be answered.
            if (!TryGetNumber(root, IdProperty, out id) || id < 0)
            {
                return ModelHostFrame.ForError(0, ModelHostErrorCode.MalformedMessage);
            }

            if (!TryGetNumber(root, VersionProperty, out var version))
            {
                return ModelHostFrame.ForError(id, ModelHostErrorCode.MalformedMessage);
            }

            if (version != ModelHostProtocol.Version)
            {
                return ModelHostFrame.ForError(id, ModelHostErrorCode.UnsupportedProtocolVersion);
            }

            if (!root.TryGetProperty(TypeProperty, out var type) || type.ValueKind != JsonValueKind.String)
            {
                return ModelHostFrame.ForError(id, ModelHostErrorCode.MalformedMessage);
            }

            if (!ByName.TryGetValue(type.GetString()!, out var typeInfo))
            {
                return ModelHostFrame.ForError(id, ModelHostErrorCode.UnknownMessageType);
            }

            if (!root.TryGetProperty(BodyProperty, out var body) || body.ValueKind != JsonValueKind.Object)
            {
                return ModelHostFrame.ForError(id, ModelHostErrorCode.MalformedMessage);
            }

            return body.Deserialize(typeInfo) is ModelHostMessage message && message.IsWellFormed()
                ? ModelHostFrame.ForMessage(id, message)
                : ModelHostFrame.ForError(id, ModelHostErrorCode.MalformedMessage);
        }
        catch (JsonException)
        {
            return ModelHostFrame.ForError(id, ModelHostErrorCode.MalformedMessage);
        }
    }

    private static bool TryGetNumber(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value);
    }
}
