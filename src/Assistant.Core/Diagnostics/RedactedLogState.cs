using System.Collections;
using System.Globalization;
using System.Text;

namespace Assistant.Core.Diagnostics;

/// <summary>
/// Log state whose values have passed <see cref="LogPrivacy.Sanitize"/>, and the message rendered from them.
/// </summary>
internal sealed class RedactedLogState : IReadOnlyList<KeyValuePair<string, object?>>
{
    private const string OriginalFormatKey = "{OriginalFormat}";
    private const string RedactedMessage = "[message redacted]";

    public static readonly Func<RedactedLogState, Exception?, string> Formatter = static (state, _) => state._message;

    private readonly KeyValuePair<string, object?>[] _values;
    private readonly string _message;

    private RedactedLogState(KeyValuePair<string, object?>[] values, string message)
    {
        _values = values;
        _message = message;
    }

    public int Count => _values.Length;

    public KeyValuePair<string, object?> this[int index] => _values[index];

    /// <summary>
    /// Sanitizes structured state and renders its template with the sanitized values. Unstructured state has no
    /// template to render safely, so its whole message is redacted.
    /// </summary>
    public static RedactedLogState From<TState>(TState state)
    {
        if (state is RedactedLogState redacted)
        {
            return redacted;
        }

        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            return new RedactedLogState([], RedactedMessage);
        }

        string? template = null;
        var sanitized = new KeyValuePair<string, object?>[values.Count];
        var holeValues = new List<object?>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var (key, value) = values[i];
            if (key == OriginalFormatKey)
            {
                template = value as string;
                sanitized[i] = values[i];
            }
            else
            {
                var safeValue = LogPrivacy.Sanitize(key, value);
                sanitized[i] = new KeyValuePair<string, object?>(key, safeValue);
                holeValues.Add(safeValue);
            }
        }

        return new RedactedLogState(sanitized, template is null ? RedactedMessage : Render(template, holeValues));
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => _message;

    // Fills "{Name}", "{Name,alignment}" and "{Name:format}" holes in order, as the built-in formatter does.
    private static string Render(string template, List<object?> values)
    {
        var builder = new StringBuilder(template.Length);
        var valueIndex = 0;
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                builder.Append(c);
                i += 2;
                continue;
            }

            var end = c == '{' ? template.IndexOf('}', i + 1) : -1;
            if (end < 0)
            {
                builder.Append(c);
                i++;
                continue;
            }

            var hole = template.AsSpan(i + 1, end - i - 1);
            var formatStart = hole.IndexOf(':');
            var format = formatStart < 0 ? null : hole[(formatStart + 1)..].ToString();
            var value = valueIndex < values.Count ? values[valueIndex] : null;
            valueIndex++;

            builder.Append(value switch
            {
                null => "(null)",
                IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            });
            i = end + 1;
        }

        return builder.ToString();
    }
}
