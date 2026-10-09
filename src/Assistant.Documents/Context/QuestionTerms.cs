using System.Globalization;
using System.Text.RegularExpressions;
using Assistant.Core.Documents;

namespace Assistant.Documents.Context;

/// <summary>A place in a document that a question names: <c>page 4</c>, <c>slides 3-5</c>, <c>section 2</c>.</summary>
/// <param name="Kind">Whether it is a page, a slide or a section.</param>
/// <param name="From">The first number, counted from 1.</param>
/// <param name="To">The last number; the same as <paramref name="From"/> for one place.</param>
internal sealed record PlaceReference(DocumentLocationKind Kind, int From, int To)
{
    /// <summary>
    /// Whether a passage that starts at <paramref name="first"/> and ends at <paramref name="last"/> is at this place. The notes
    /// of a slide are part of the slide.
    /// </summary>
    public bool Covers(DocumentLocation first, DocumentLocation last)
    {
        if (!IsKind(first.Kind) && !IsKind(last.Kind))
        {
            return false;
        }

        var start = IsKind(first.Kind) ? first.Number : last.Number;
        var end = IsKind(last.Kind) ? last.Number : first.Number;
        return From <= end && To >= start;
    }

    private bool IsKind(DocumentLocationKind kind) =>
        kind == Kind || (Kind == DocumentLocationKind.Slide && kind == DocumentLocationKind.SlideNotes);
}

/// <summary>
/// What a question asks for, read for ranking passages (PROJECT_SPEC §4.7): the terms worth looking for in the text (its words
/// without those that mean nothing alone, each once, in the order they stand, at most <see cref="MaxTerms"/>), and the places in
/// the document it names ("what does slide 5 say"). Neither the question nor its terms are ever logged.
/// </summary>
internal sealed partial class QuestionTerms
{
    /// <summary>The most terms looked for; a longer question is read as far as that.</summary>
    public const int MaxTerms = 32;

    private QuestionTerms(IReadOnlyList<string> terms, IReadOnlyList<PlaceReference> places)
    {
        Terms = terms;
        Places = places;
    }

    /// <summary>The terms, each once, in the order the question has them.</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>The places the question names.</summary>
    public IReadOnlyList<PlaceReference> Places { get; }

    /// <summary>Whether the question gives nothing to look for: no term and no place.</summary>
    public bool IsEmpty => Terms.Count == 0 && Places.Count == 0;

    /// <summary>Reads <paramref name="question"/>.</summary>
    public static QuestionTerms Parse(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new QuestionTerms([], []);
        }

        // A named place is taken out of the words, so "slide 5" looks for the slide and not for the digit.
        var places = new List<PlaceReference>();
        foreach (Match match in PlaceText().Matches(question))
        {
            var kind = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "slide" or "slides" => DocumentLocationKind.Slide,
                "section" or "sections" => DocumentLocationKind.Section,
                _ => DocumentLocationKind.Page,
            };
            var from = Number(match.Groups[2].Value);
            var to = match.Groups[3].Success ? Number(match.Groups[3].Value) : from;
            places.Add(new PlaceReference(kind, Math.Min(from, to), Math.Max(from, to)));
        }

        var words = places.Count == 0 ? question : PlaceText().Replace(question, " ");
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in TermExtractor.Extract(words, skipStopWords: true))
        {
            if (seen.Add(term))
            {
                terms.Add(term);
                if (terms.Count == MaxTerms)
                {
                    break;
                }
            }
        }

        return new QuestionTerms(terms, places);
    }

    private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    // "page 4", "pages 3-5", "slide no. 2", "section #7", "slides 2 to 4". The dash is any dash punctuation.
    [GeneratedRegex(
        @"\b(pages?|slides?|sections?)\s*(?:no\.?|number|#)?\s*(\d{1,5})(?:\s*(?:\p{Pd}|to)\s*(\d{1,5}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceText();
}
