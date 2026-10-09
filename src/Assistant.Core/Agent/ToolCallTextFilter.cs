using System.Text;

namespace Assistant.Core.Agent;

/// <summary>
/// Keeps a tool call that a model wrote out as words out of what the user reads. A model that has no tools left to call (the run's last step) sometimes
/// writes the call anyway, as the markup its template uses (<c>&lt;tool_call&gt;…</c>): that is not an answer, and the user is not shown it. What the model
/// said before the markup is kept; the markup and everything after it is dropped. The words arrive in pieces, so a piece that ends in what could be the
/// start of the markup is held back until the next one says which it is.
/// </summary>
internal sealed class ToolCallTextFilter
{
    private static readonly string[] Markers = ["<tool_call", "<|tool_call", "<function=", "<|python_tag|>", "[TOOL_CALLS]"];

    private readonly StringBuilder _held = new();

    /// <summary>Whether markup was met, and the rest of the answer dropped.</summary>
    public bool Cut { get; private set; }

    /// <summary>What of <paramref name="delta"/>, and of what was held back before it, the user is shown.</summary>
    public string Take(string delta)
    {
        if (Cut)
        {
            return string.Empty;
        }

        _held.Append(delta);
        var text = _held.ToString();
        var at = Markers.Select(marker => text.IndexOf(marker, StringComparison.OrdinalIgnoreCase)).Where(index => index >= 0).DefaultIfEmpty(-1).Min();
        if (at >= 0)
        {
            Cut = true;
            _held.Clear();
            return text[..at].TrimEnd();
        }

        // The longest end of the text that is the beginning of a marker waits for what follows it.
        var keep = 0;
        foreach (var marker in Markers)
        {
            for (var length = Math.Min(marker.Length - 1, text.Length); length > keep; length--)
            {
                if (text.EndsWith(marker[..length], StringComparison.OrdinalIgnoreCase))
                {
                    keep = length;
                    break;
                }
            }
        }

        _held.Clear();
        _held.Append(text, text.Length - keep, keep);
        return text[..^keep];
    }

    /// <summary>What was held back at the end of the answer and turned out to be words.</summary>
    public string Flush()
    {
        if (Cut)
        {
            return string.Empty;
        }

        var rest = _held.ToString();
        _held.Clear();
        return rest;
    }
}
