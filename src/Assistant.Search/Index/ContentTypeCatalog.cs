using System.Security;
using Microsoft.Win32;

namespace Assistant.Search.Index;

/// <summary>What the index can hold of the text inside files of one type.</summary>
internal enum ContentTypeSupport
{
    /// <summary>It cannot be told, so nothing is claimed either way.</summary>
    Unknown = 0,

    /// <summary>Windows has a filter that reads the text of this type, so the index can hold it.</summary>
    HasContentFilter = 1,

    /// <summary>Windows has no filter for this type, so the index keeps its properties (name, dates, size) and no text.</summary>
    NoContentFilter = 2,

    /// <summary>A picture, a video or a sound: the index holds its properties, and it has no text to hold.</summary>
    NoTextInMedia = 3,
}

/// <summary>
/// Tells whether the index can hold the text inside files of a type. It is the one place the module asks Windows about file
/// types, so the rest is tested with a fake.
/// </summary>
internal interface IContentTypeCatalog
{
    /// <summary>What the index can hold of the text of files with <paramref name="extension"/> (lower case, with its dot).</summary>
    ContentTypeSupport Lookup(string extension);
}

/// <summary>
/// Reads Windows' own registration of file types: the persistent handler of the extension (on the extension itself or on its
/// ProgID) and the filter that handler names, which is what Windows Search reads a file's text with. A type with none is one
/// Windows indexes for its properties only, and this PC's index confirms it (<c>.md</c>, <c>.log</c>, <c>.json</c> and files
/// of a type nobody has registered have no text in it). A read that fails is <see cref="ContentTypeSupport.Unknown"/>, never
/// a guess. A type the user has turned to "properties only" in the Indexing Options is not seen here: Windows keeps that
/// somewhere this does not read, so the answer is what can be indexed, not a promise of what is.
/// </summary>
internal sealed class RegistryContentTypeCatalog : IContentTypeCatalog
{
    // The registry key under a persistent handler that names the filter (IFilter) which reads the text of its files.
    private const string FilterAddin = "{89BCB740-6119-101A-BCB7-00DD010655AF}";

    /// <inheritdoc/>
    public ContentTypeSupport Lookup(string extension)
    {
        if (string.IsNullOrEmpty(extension) || extension[0] != '.' || extension.Contains('\\') || extension.Contains('/')
            || extension.Any(char.IsControl))
        {
            return ContentTypeSupport.Unknown;
        }

        try
        {
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Default);
            using var key = classes.OpenSubKey(extension);
            if (key is not null && IsMedia(key))
            {
                return ContentTypeSupport.NoTextInMedia;
            }

            var handler = HandlerOf(classes, key, extension);
            if (handler is null)
            {
                return ContentTypeSupport.NoContentFilter;
            }

            using var filter = classes.OpenSubKey($@"CLSID\{handler}\PersistentAddinsRegistered\{FilterAddin}");
            return filter?.GetValue(string.Empty) is string { Length: > 0 }
                ? ContentTypeSupport.HasContentFilter
                : ContentTypeSupport.NoContentFilter;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException
            or ObjectDisposedException or ArgumentException)
        {
            return ContentTypeSupport.Unknown;
        }
    }

    private static bool IsMedia(RegistryKey extension)
    {
        if (extension.GetValue("PerceivedType") is string perceived
            && perceived.Trim().ToLowerInvariant() is "image" or "audio" or "video")
        {
            return true;
        }

        return extension.GetValue("Content Type") is string mime
            && (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The persistent handler's class id: registered on the extension, or else on its ProgID.</summary>
    private static string? HandlerOf(RegistryKey classes, RegistryKey? extension, string extensionName)
    {
        if (ReadDefault(classes, extensionName + @"\PersistentHandler") is { } direct)
        {
            return direct;
        }

        return extension?.GetValue(string.Empty) is string { Length: > 0 } progId && !progId.Contains('\\')
            ? ReadDefault(classes, progId + @"\PersistentHandler")
            : null;
    }

    private static string? ReadDefault(RegistryKey classes, string path)
    {
        using var key = classes.OpenSubKey(path);
        return key?.GetValue(string.Empty) is string { Length: > 0 } value ? value.Trim() : null;
    }
}
