using System.Globalization;
using System.Text.Json;
using Assistant.Core.Domain;
using Assistant.Core.Files;

namespace Assistant.Core.Tools;

/// <summary>
/// What the two file tools return to the model, as JSON, and how the app reads it back (PROJECT_SPEC §4.8). The tools that make it and
/// the chat that shows what a search found read the one shape from here. A file is only ever named by its id and its name: the path
/// never goes to the model.
/// </summary>
public static class FileToolResults
{
    /// <summary>The name of the tool that finds files.</summary>
    public const string SearchFiles = "search_files";

    /// <summary>The name of the tool that reads a file.</summary>
    public const string ReadFileText = "read_file_text";

    // The most files a search puts in front of the model, and the longest name it is told.
    private const int MaxFiles = 10;
    private const int MaxNameLength = 150;

    /// <summary>The JSON of a search that found <paramref name="files"/>.</summary>
    /// <param name="files">The files, with their ids in the conversation.</param>
    /// <param name="note">What to say of how the files were found ("the closest", "of another kind"), or <see langword="null"/>.</param>
    /// <param name="images">Whether the request was for pictures, which the chat shows as a gallery.</param>
    public static string Found(IReadOnlyList<KnownFile> files, string? note, bool images)
    {
        ArgumentNullException.ThrowIfNull(files);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("found", files.Count);
            writer.WriteStartArray("files");
            foreach (var file in files.Take(MaxFiles))
            {
                writer.WriteStartObject();
                writer.WriteString("id", file.Id);
                writer.WriteString("name", Cut(file.Name));
                if (file.Folder is { } folder)
                {
                    writer.WriteString("folder", folder);
                }

                if (file.Item.Type == SearchResultItemType.Folder)
                {
                    writer.WriteString("type", "folder");
                }
                else if (!string.IsNullOrEmpty(file.Item.Extension))
                {
                    writer.WriteString("type", file.Item.Extension.TrimStart('.'));
                }

                if (file.Item.ModifiedAt is { } modified)
                {
                    writer.WriteString("modified", modified.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (images)
            {
                writer.WriteBoolean("images", true);
            }

            if (files.Count > MaxFiles)
            {
                writer.WriteString("more", $"{files.Count - MaxFiles} more files were found and are not listed.");
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                writer.WriteString("note", note);
            }

            if (files.Count > 0)
            {
                writer.WriteString("hint", FoundHint);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>What a list of found files reminds the model of, at the moment it decides what to do with them.</summary>
    public const string FoundHint = "Read a file only if the user asked what is in it. If more than one could be meant, ask which.";

    /// <summary>The JSON of a file that was read: what it is called, how much of it was read, and its text (untrusted, already marked).</summary>
    /// <param name="file">The file's id in the conversation.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="text">The text read, wrapped as untrusted context.</param>
    /// <param name="coverage">What was read of the file ("5 of 6 parts, spread across it"), or <see langword="null"/> when all of it.</param>
    /// <param name="notice">What the user is told about how much was read, or <see langword="null"/>.</param>
    public static string Read(string file, string name, string text, string? coverage, string? notice)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(text);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("file", file);
            writer.WriteString("name", Cut(name));
            if (!string.IsNullOrWhiteSpace(coverage))
            {
                writer.WriteString("read", coverage);
            }

            if (!string.IsNullOrWhiteSpace(notice))
            {
                writer.WriteString("notice", notice);
            }

            writer.WriteString("text", text);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The JSON of a call that did not work, which tells the model why so that it can say so or try again.</summary>
    public static string Error(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads what a finished <see cref="SearchFiles"/> call found: the ids of the files and whether the request was for pictures.
    /// </summary>
    /// <returns><see langword="false"/> when <paramref name="result"/> is not a search that worked, or found nothing.</returns>
    public static bool TryReadFound(ToolResult result, out IReadOnlyList<string> ids, out bool images)
    {
        ArgumentNullException.ThrowIfNull(result);
        ids = [];
        images = false;
        if (result.ToolName != SearchFiles || result.Status != ToolResultStatus.Succeeded
            || Parse(result.OutputJson) is not { } root || !root.TryGetProperty("files", out var files)
            || files.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        ids = [.. files.EnumerateArray()
            .Select(file => file.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
            .OfType<string>()];
        images = root.TryGetProperty("images", out var flag) && flag.ValueKind == JsonValueKind.True;
        return ids.Count > 0;
    }

    /// <summary>Reads what the user is told about how much of a file a finished <see cref="ReadFileText"/> call read, if anything.</summary>
    public static string? ReadNotice(ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ToolName == ReadFileText && result.Status == ToolResultStatus.Succeeded
            && Parse(result.OutputJson) is { } root && root.TryGetProperty("notice", out var notice)
            && notice.ValueKind == JsonValueKind.String
                ? notice.GetString()
                : null;
    }

    private static JsonElement? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Cut(string name) => name.Length > MaxNameLength ? name[..MaxNameLength] + "..." : name;
}
