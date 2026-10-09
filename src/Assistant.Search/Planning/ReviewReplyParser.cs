using System.Text.Json;

namespace Assistant.Search.Planning;

/// <summary>
/// Reads the model's pick of files into the numbers of the candidates, under a strict schema, and trusts nothing in it. The reply is
/// one JSON object with one field, <c>matches</c>, an array of whole numbers from 1 to the number of candidates, each once, at most
/// <see cref="ReviewPrompt.MaxMatches"/>. A field that is not <c>matches</c>, a field given twice, a value of another type, a number
/// that is not a candidate, a number given twice or more numbers than were asked for make the whole reply invalid, and an invalid
/// reply picks nothing. So a reply can only ever point at a file that was in the list; it cannot name one.
/// </summary>
internal static class ReviewReplyParser
{
    /// <summary>
    /// The picked numbers in ascending order (none is an empty list), or <see langword="null"/> when the reply is not valid.
    /// </summary>
    public static IReadOnlyList<int>? Parse(string json, int candidateCount)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement? matches = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name != "matches" || matches is not null)
                {
                    return null;
                }

                matches = property.Value;
            }

            return matches is { } value ? ReadNumbers(value, candidateCount) : null;
        }
    }

    private static IReadOnlyList<int>? ReadNumbers(JsonElement value, int candidateCount)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > ReviewPrompt.MaxMatches)
        {
            return null;
        }

        var numbers = new SortedSet<int>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetInt32(out var number)
                || number < 1 || number > candidateCount || !numbers.Add(number))
            {
                return null;
            }
        }

        return [.. numbers];
    }
}
