using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Domain;

namespace Assistant.Core.Context;

/// <summary>
/// What makes two pieces of context the same one: the same kind, the same file (when it is one), and the same content,
/// compared by a hash so the content itself is never held twice. Two items with neither a file nor any content are never
/// the same, unless they are one item.
/// </summary>
internal readonly record struct ContextKey(ContextItemType Type, string? Path, string? ContentHash, Guid IdWhenEmpty)
{
    public static ContextKey Of(ContextItem item)
    {
        var path = NormalizePath(item.FilePath);
        var hash = HashOf(item);
        return new ContextKey(item.Type, path, hash, path is null && hash is null ? item.Id : Guid.Empty);
    }

    // Windows file names ignore case and accept either slash, and a path may end in a slash.
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Trim().Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    }

    // The text compared by its words' spelling alone: the line ends of the system it came from, and the space around it,
    // do not make it another text. A picture is compared by its bytes.
    private static string? HashOf(ContextItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Text))
        {
            var text = item.Text.ReplaceLineEndings("\n").Trim();
            return "t:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        return item.ImageData.IsEmpty ? null : "i:" + Convert.ToHexString(SHA256.HashData(item.ImageData.Span));
    }
}
