using System.Text.Json;

namespace Assistant.Core.Assets;

/// <summary>A manifest could not be read. The message says what is wrong, in the Assistant's words, and never repeats the manifest's text.</summary>
public sealed class AssetManifestException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AssetManifestException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Reads an asset manifest strictly (PROJECT_SPEC §3.5, step 123). A manifest says which files may be trusted as the Assistant's own, so what it
/// names is checked: a group is a plain identifier, a file's path stays inside the group's folder and is a name Windows will not read as a device,
/// a checksum is 64 hexadecimal digits, and nothing is accepted twice. A field the reader does not know is ignored, so a later version may add one.
/// </summary>
public static class AssetManifestReader
{
    /// <summary>The most bytes a manifest may have.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The most groups a manifest may list.</summary>
    public const int MaxGroups = 64;

    /// <summary>The most files a group may list: a model has two, a voice engine with its data (Piper's speech data alone) has hundreds.</summary>
    public const int MaxFilesPerGroup = 1000;

    /// <summary>The longest path, in characters, a file may have.</summary>
    public const int MaxPathLength = 200;

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Reads the manifest in <paramref name="json"/> (UTF-8, with or without a byte order mark).</summary>
    /// <exception cref="AssetManifestException">The manifest is too large, is not JSON, is of a version this build does not read, or breaks a rule above.</exception>
    public static AssetManifest Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes)
        {
            throw new AssetManifestException("The asset manifest is too large.");
        }

        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF)
        {
            json = json[3..];
        }

        try
        {
            using var document = JsonDocument.Parse(
                json.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            return Read(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new AssetManifestException("The asset manifest is not valid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new AssetManifestException("The asset manifest does not have the expected shape.", exception);
        }
    }

    /// <summary>Whether <paramref name="id"/> can identify a group: lower case letters, digits and hyphens, not starting or ending with a hyphen.</summary>
    public static bool IsValidGroupId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64
        && id.All(letter => letter is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && id[0] != '-' && id[^1] != '-';

    /// <summary>
    /// Normalizes <paramref name="path"/> to the form a manifest holds (<c>/</c> separators), or returns <see langword="null"/> when it is not a
    /// relative path inside a folder: empty, rooted, with a drive or a stream (<c>:</c>), with an empty, <c>.</c> or <c>..</c> segment, with
    /// characters Windows does not allow in names, a segment that ends in a dot or a space or is a device name, or longer than
    /// <see cref="MaxPathLength"/>.
    /// </summary>
    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength)
        {
            return null;
        }

        var segments = path.Replace('\\', '/').Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".."
                || segment.EndsWith('.') || segment.EndsWith(' ') || segment.StartsWith(' ')
                || segment.Any(character => character < ' ' || "<>:\"|?*".Contains(character))
                || DeviceNames.Contains(segment.Split('.')[0]))
            {
                return null;
            }
        }

        return string.Join('/', segments);
    }

    private static AssetManifest Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AssetManifestException("The asset manifest does not have the expected shape.");
        }

        if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number) || number != AssetManifest.SchemaVersion)
        {
            throw new AssetManifestException("The asset manifest is of a version this Assistant does not read.");
        }

        if (!root.TryGetProperty("groups", out var groupsElement) || groupsElement.ValueKind != JsonValueKind.Array)
        {
            throw new AssetManifestException("The asset manifest lists no groups.");
        }

        if (groupsElement.GetArrayLength() > MaxGroups)
        {
            throw new AssetManifestException("The asset manifest lists too many groups.");
        }

        var groups = new List<AssetGroup>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var groupElement in groupsElement.EnumerateArray())
        {
            var group = ReadGroup(groupElement);
            if (!seen.Add(group.Id))
            {
                throw new AssetManifestException("The asset manifest lists a group twice.");
            }

            groups.Add(group);
        }

        return new AssetManifest(groups);
    }

    private static AssetGroup ReadGroup(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
            || idElement.GetString() is not { } id || !IsValidGroupId(id))
        {
            throw new AssetManifestException("A group in the asset manifest has no valid identifier.");
        }

        if (!element.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new AssetManifestException("A group in the asset manifest lists no files.");
        }

        if (filesElement.GetArrayLength() is 0 or > MaxFilesPerGroup)
        {
            throw new AssetManifestException("A group in the asset manifest lists no files or too many.");
        }

        var files = new List<AssetFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            var file = ReadFile(fileElement);
            if (!seen.Add(file.Path))
            {
                throw new AssetManifestException("A group in the asset manifest lists a file twice.");
            }

            files.Add(file);
        }

        return new AssetGroup(id, files);
    }

    private static AssetFile ReadFile(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String
            || NormalizePath(pathElement.GetString()) is not { } path)
        {
            throw new AssetManifestException("A file in the asset manifest has no valid path.");
        }

        if (!element.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out var size) || size < 0)
        {
            throw new AssetManifestException("A file in the asset manifest has no valid size.");
        }

        if (!element.TryGetProperty("sha256", out var hashElement) || hashElement.ValueKind != JsonValueKind.String
            || hashElement.GetString() is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
        {
            throw new AssetManifestException("A file in the asset manifest has no valid SHA-256.");
        }

        return new AssetFile(path, size, hash.ToLowerInvariant());
    }
}
