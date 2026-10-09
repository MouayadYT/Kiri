namespace Assistant.Core.People;

/// <summary>
/// The few words for the same relative that people use interchangeably ("mom", "mum", "mother"; "bro", "brother"), by a fixed list in code and
/// nothing else (PROJECT_SPEC §4.8, step 112): no model, no connected app, no network. A relationship the user wrote that is not on the list stands for itself, so
/// "Colleague" and "Boss" work with no list at all. Only terms that really mean the same person are joined: "partner" is not "wife".
/// </summary>
public static class RelationshipTerms
{
    private static readonly Dictionary<string, string> Canonical = Build(
        ["mother", "mom", "mum", "mommy", "mummy", "mama", "mam"],
        ["father", "dad", "daddy", "papa", "pops"],
        ["brother", "bro"],
        ["sister", "sis"],
        ["grandmother", "grandma", "granny", "gran", "nana"],
        ["grandfather", "grandpa", "grandad", "granddad", "gramps"],
        ["husband", "hubby"],
        ["wife", "wifey"]);

    /// <summary>
    /// The one word that stands for every way of saying the same relative: <paramref name="folded"/> (already folded by
    /// <see cref="PersonText.Fold"/>) itself when it is not on the list.
    /// </summary>
    public static string Of(string folded) => Canonical.TryGetValue(folded, out var term) ? term : folded;

    /// <summary>
    /// The one word for the relative that <paramref name="folded"/> is a slip of the keyboard away from ("borther" is <c>brother</c>), or <see langword="null"/> when it is not that close to
    /// any word on the list, or is close to the words of two different relatives. A word on the list, or one of fewer than four letters, is never a slip.
    /// </summary>
    public static string? Closest(string folded)
    {
        if (Canonical.ContainsKey(folded))
        {
            return null;
        }

        var terms = Canonical.Where(pair => PersonText.IsClose(folded, pair.Key)).Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToList();
        return terms.Count == 1 ? terms[0] : null;
    }

    /// <summary>Whether <paramref name="folded"/> (already folded by <see cref="PersonText.Fold"/>) is one of the words on the list: a way of saying mother, father, brother and the like.</summary>
    public static bool IsKnown(string folded) => Canonical.ContainsKey(folded);

    private static Dictionary<string, string> Build(params string[][] groups)
    {
        var terms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            foreach (var word in group)
            {
                terms[word] = group[0];
            }
        }

        return terms;
    }
}
