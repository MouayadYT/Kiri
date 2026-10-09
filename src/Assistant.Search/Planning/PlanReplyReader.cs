using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Search.Planning;

/// <summary>
/// Collects the model's reply as it streams in and finds the JSON object in it: the first object whose braces close, outside
/// of any reasoning block (<c>&lt;think&gt;...&lt;/think&gt;</c>), a code fence or the words the model puts around it. The
/// framing is tolerated; the object it frames is not: it still has to pass the caller's validation, field by field. The
/// caller stops reading as soon as the object is complete, so a model that goes on talking costs no time.
/// </summary>
internal sealed partial class PlanReplyReader
{
    /// <summary>The most characters of a reply that are kept; a reply that goes past it has no plan in it.</summary>
    public const int MaxLength = 8000;

    private readonly StringBuilder _text = new();

    /// <summary>The JSON object, once its braces have closed; <see langword="null"/> until then.</summary>
    public string? Object { get; private set; }

    /// <summary>Whether the object is complete, or the reply has grown too long to hold one.</summary>
    public bool IsDone => Object is not null || _text.Length > MaxLength;

    /// <summary>Adds the next piece of the reply and looks for the object again.</summary>
    public void Append(string piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        if (IsDone)
        {
            return;
        }

        _text.Append(piece);
        Object = Find(_text.ToString());
    }

    /// <summary>Finds the JSON object in a whole reply, or returns <see langword="null"/> when it holds none that closes.</summary>
    public static string? Find(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var visible = Reasoning().Replace(reply, "");

        // A reasoning block that is still open holds nothing to read yet.
        if (visible.Contains("<think>", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var start = visible.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < visible.Length; i++)
        {
            var character = visible[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return visible[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reasoning();
}
