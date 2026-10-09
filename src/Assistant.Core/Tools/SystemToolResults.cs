using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Assistant.Core.Tools;

/// <summary>
/// The names of the tools that act on Windows itself (PROJECT_SPEC §4.8) and what they return to the model, as JSON. Each does one
/// thing, takes only what it names, and starts no program, command line or script that the model writes: a program is named by what
/// Start calls it, a file by the id the conversation gave it, a folder by one of the user's own, and a volume by a number.
/// </summary>
public static class SystemToolResults
{
    /// <summary>Opens an installed application, by the name Start shows.</summary>
    public const string OpenApplication = "open_application";

    /// <summary>Opens a file or folder the conversation knows, with its default handler.</summary>
    public const string OpenFile = "open_file";

    /// <summary>Shows a file the conversation knows in File Explorer.</summary>
    public const string RevealFile = "reveal_file";

    /// <summary>Opens one of the user's own folders, or a folder the conversation knows, in File Explorer.</summary>
    public const string OpenFolder = "open_folder";

    /// <summary>Reads the speakers' volume.</summary>
    public const string GetVolume = "get_volume";

    /// <summary>Sets the speakers' volume.</summary>
    public const string SetVolume = "set_volume";

    /// <summary>Turns the speakers' sound off.</summary>
    public const string Mute = "mute";

    /// <summary>Turns the speakers' sound on.</summary>
    public const string Unmute = "unmute";

    /// <summary>Takes a picture of the screen and attaches it to the conversation.</summary>
    public const string TakeScreenshot = "take_screenshot";

    // A name or a sentence reads as it is written, not with its quotes and plus signs escaped, in what the model sees.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The JSON of an action that was done: <c>{"done":true,"message":"..."}</c>.</summary>
    public static string Done(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Write(writer =>
        {
            writer.WriteBoolean("done", true);
            writer.WriteString("message", message);
        });
    }

    /// <summary>The JSON of the volume as it is, with a <paramref name="message"/> when an action put it there.</summary>
    public static string Volume(int percent, bool muted, string? message = null) =>
        Write(writer =>
        {
            writer.WriteNumber("volume", percent);
            writer.WriteBoolean("muted", muted);
            if (!string.IsNullOrWhiteSpace(message))
            {
                writer.WriteString("message", message);
            }
        });

    /// <summary>The JSON of a screenshot that was taken: its size and what the model can do with it.</summary>
    public static string Screenshot(int width, int height, string message) =>
        Write(writer =>
        {
            writer.WriteBoolean("taken", true);
            writer.WriteNumber("width", width);
            writer.WriteNumber("height", height);
            writer.WriteString("message", message);
        });

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
