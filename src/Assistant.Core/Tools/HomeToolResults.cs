using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Assistant.Core.Tools;

/// <summary>A device of the user's home after something was done to it, as the conversation shows it: what it is called, what kind of thing it is and what it is now.</summary>
/// <param name="Id">Home Assistant's id of the device, by which it can be switched again.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Kind">What kind of thing it is: <c>fan</c>, <c>light</c>, <c>climate</c>.</param>
/// <param name="State">What Home Assistant shows it as now (<c>on</c>, <c>off</c>, <c>heat</c>), or empty when it did not say.</param>
public sealed record HomeControlled(string Id, string Name, string Kind, string State);

/// <summary>
/// What the tool that controls a home device returns to the model, as JSON: that it was done, the sentence to pass on, and the device as it is now,
/// which is also what the conversation draws the device's card from.
/// </summary>
public static class HomeToolResults
{
    /// <summary>The name of the tool that controls a home device.</summary>
    public const string ControlHomeDevice = "control_home_device";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The JSON of a device that was controlled: <c>{"done":true,"message":"…","device":{…}}</c>.</summary>
    public static string Controlled(string message, HomeControlled device)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(device);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("done", true);
            writer.WriteString("message", message);
            writer.WriteStartObject("device");
            writer.WriteString("id", device.Id);
            writer.WriteString("name", device.Name);
            writer.WriteString("kind", device.Kind);
            writer.WriteString("state", device.State);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads the device a call controlled out of its result; <see langword="false"/> when the result is not one of a device that was controlled.</summary>
    public static bool TryReadControlled(string? json, out HomeControlled device)
    {
        device = new HomeControlled(string.Empty, string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("device", out var about) || about.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            static string Text(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

            device = new HomeControlled(Text(about, "id"), Text(about, "name"), Text(about, "kind"), Text(about, "state"));
            return device.Id.Length > 0 && device.Name.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
