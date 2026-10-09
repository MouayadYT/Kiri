namespace Assistant.Core.People;

/// <summary>How a reference to a person such as "my brother" was resolved.</summary>
public enum PersonResolutionOutcome
{
    /// <summary>Exactly one person fits.</summary>
    Found = 0,

    /// <summary>More than one person fits, so the user has to say which.</summary>
    Ambiguous = 1,

    /// <summary>No saved person fits.</summary>
    NotFound = 2,

    /// <summary>There was nothing to look for: no words.</summary>
    NothingToResolve = 3,
}

/// <summary>
/// The answer to "who is this?" (PROJECT_SPEC §4.8, step 112): the one person who fits, or the several that do with a question to put to the user, or none
/// with what to do about it. <see cref="Message"/> is written for the user to read and is empty when a person was found. It repeats what was asked
/// (private content) and the names of the people saved, so it is shown and passed on and never logged.
/// </summary>
/// <param name="Outcome">What was found.</param>
/// <param name="Candidates">The person who was found, or the people who fit, ordered by name; empty when none does.</param>
/// <param name="Message">What to tell the user when no single person was found.</param>
public sealed record PersonResolution(PersonResolutionOutcome Outcome, IReadOnlyList<Person> Candidates, string Message)
{
    /// <summary>The person who was found, or <see langword="null"/> when the outcome is not <see cref="PersonResolutionOutcome.Found"/>.</summary>
    public Person? Person => Outcome == PersonResolutionOutcome.Found ? Candidates[0] : null;

    // Keeps the names and the question (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Outcome = {Outcome}, Candidates = {Candidates.Count}");
        return true;
    }
}

/// <summary>Works out who the user means by "my brother", "Omar" or "my brother Omar" from the people they saved (PROJECT_SPEC §4.8, step 112).</summary>
public interface IPersonResolver
{
    /// <summary>
    /// Finds the saved person <paramref name="reference"/> names, or says why no single one can be taken: several fit, none does, or there was
    /// nothing to look for. It never guesses: more than one match is a question for the user, not a choice.
    /// </summary>
    /// <exception cref="PersonStoreException">The people could not be read.</exception>
    Task<PersonResolution> ResolveAsync(string? reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// The app's <see cref="IPersonResolver"/>: it reads only the Assistant's own list of people, by fixed rules and nothing else (no model, no connected app,
/// no network), so that what "my brother" means is the user's own and is the same however the request is phrased. In order, the first of these that fits
/// anyone decides, and several people in it make the answer ambiguous:
/// <list type="number">
/// <item>For "my X": everyone whose relationship is X ("my bro" and "my brother" are the same relationship, <see cref="RelationshipTerms"/>) or whose name or alias is X.</item>
/// <item>For a bare "X": everyone whose name or alias is X, then everyone whose relationship is X.</item>
/// <item>A relationship and a name together, "my brother Omar": everyone with that relationship whose name fits the rest.</item>
/// <item>The words of X among the words of a name or of one alias: "Sara" for "Sara Ahmed".</item>
/// <item>Last, X with one slip of the keyboard in it ("my borther"): the relationship, name or alias it is a slip of.</item>
/// </list>
/// Case, accents and punctuation never matter (<see cref="PersonText"/>).
/// </summary>
/// <param name="store">Where the people are.</param>
public sealed class PersonResolver(IPersonStore store) : IPersonResolver
{
    private const int MaxQuotedLength = 60;

    /// <inheritdoc/>
    public async Task<PersonResolution> ResolveAsync(string? reference, CancellationToken cancellationToken = default)
    {
        var words = PersonText.Words(PersonText.Fold(reference));
        if (words.Length == 0)
        {
            return new PersonResolution(
                PersonResolutionOutcome.NothingToResolve, [], "Say who you mean: a name, or someone you know by how they relate to you, such as “my brother”.");
        }

        var possessive = words.Length > 1 && words[0] == "my";
        if (possessive)
        {
            words = words[1..];
        }

        var text = string.Join(' ', words);
        var people = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var found = Tiers(people, words, text, possessive).Select(tier => tier.ToList()).FirstOrDefault(tier => tier.Count > 0) ?? [];
        var candidates = OnePersonEach(found).OrderBy(person => person.DisplayName, PersonRules.NameOrder).ThenBy(person => person.Id).ToArray();
        var quoted = Quote(reference);
        return candidates.Length switch
        {
            1 => new PersonResolution(PersonResolutionOutcome.Found, candidates, string.Empty),
            0 => new PersonResolution(
                PersonResolutionOutcome.NotFound,
                [],
                people.Count == 0
                    ? "No one is saved yet. You can add the people you message in Settings, under People."
                    : $"I don't have anyone saved as “{quoted}”. You can add them in Settings, under People."),
            _ => new PersonResolution(
                PersonResolutionOutcome.Ambiguous,
                candidates,
                $"More than one person fits “{quoted}”: {Join(candidates.Select(person => person.DisplayName))}. Which one do you mean?"),
        };
    }

    private static IEnumerable<IEnumerable<Person>> Tiers(IReadOnlyList<Person> people, string[] words, string text, bool possessive)
    {
        var relationship = RelationshipTerms.Of(text);
        IEnumerable<Person> Exact() => people.Where(person => NameKeys(person).Contains(text));
        IEnumerable<Person> ByRelationship() => people.Where(person => RelationshipKeys(person).Contains(relationship));
        IEnumerable<Person> RelationshipAndName() => people.Where(person => RelationshipWithName(person, words));
        IEnumerable<Person> ByWords() => people.Where(person => WordsFit(person, words));

        // The last resort: what was typed is a slip of the keyboard away from a relationship word ("borther") or from a name or alias, and only that one thing.
        IEnumerable<Person> BySlip()
        {
            if (RelationshipTerms.Closest(text) is { } term)
            {
                var related = people.Where(person => RelationshipKeys(person).Contains(term)).ToList();
                if (related.Count > 0)
                {
                    return related;
                }
            }

            return people.Where(person => NameKeys(person).Any(key => PersonText.IsClose(key, text))
                || RelationshipKeys(person).Any(key => PersonText.IsClose(key, text)));
        }

        if (possessive)
        {
            yield return ByRelationship().Union(Exact());
        }
        else
        {
            yield return Exact();
            yield return ByRelationship();
        }

        yield return RelationshipAndName();
        yield return ByWords();
        yield return BySlip();
    }

    // Someone who was saved twice, once by a handle and once by the name in it ("@marcus:beeper.com" and "Marcus"), is one person: the one with the
    // name, who is reached by the handle too. Nothing kept is changed by this; the two stay as they are in Settings, under People.
    private static List<Person> OnePersonEach(List<Person> found)
    {
        var byHandle = found.Where(person => PersonHandles.IsHandle(person.DisplayName)).ToList();
        if (found.Count < 2 || byHandle.Count == 0 || byHandle.Count == found.Count)
        {
            return found;
        }

        var people = new List<Person>();
        var taken = new HashSet<Guid>();
        foreach (var person in found.Where(person => !PersonHandles.IsHandle(person.DisplayName)))
        {
            var words = NameSets(person).ToList();
            var same = byHandle
                .Where(other => !taken.Contains(other.Id) && PersonText.Fold(PersonHandles.LocalPart(other.DisplayName)) is { Length: > 0 } local
                    && words.Any(set => set.Contains(local)))
                .ToList();
            if (same.Count == 0)
            {
                people.Add(person);
                continue;
            }

            var identifiers = person.Identifiers.ToList();
            foreach (var other in same)
            {
                taken.Add(other.Id);
                identifiers.Add(new PersonIdentifier(PersonIdentifierKind.Username, other.DisplayName, PersonHandles.Service(other.DisplayName)));
                identifiers.AddRange(other.Identifiers.Where(identifier => identifier.Kind != PersonIdentifierKind.ChatName));
            }

            people.Add(person with { Identifiers = identifiers });
        }

        people.AddRange(byHandle.Where(other => !taken.Contains(other.Id)));
        return people;
    }

    // "brother omar": a relationship the person has, then a name that fits the rest.
    private static bool RelationshipWithName(Person person, string[] words)
    {
        var relationships = RelationshipKeys(person);
        for (var split = 1; split < words.Length; split++)
        {
            var term = RelationshipTerms.Of(string.Join(' ', words[..split]));
            var rest = words[split..];
            if (relationships.Contains(term) && (NameKeys(person).Contains(string.Join(' ', rest)) || WordsFit(person, rest)))
            {
                return true;
            }
        }

        return false;
    }

    // Every word asked for is a word of the person's name, or every one is a word of one alias.
    private static bool WordsFit(Person person, string[] words) =>
        NameSets(person).Any(set => words.All(set.Contains));

    private static IEnumerable<HashSet<string>> NameSets(Person person) =>
        NameKeys(person).Select(key => PersonText.Words(key).ToHashSet(StringComparer.Ordinal));

    private static HashSet<string> NameKeys(Person person) =>
        new([PersonText.Fold(person.DisplayName), .. person.Aliases.Select(PersonText.Fold)], StringComparer.Ordinal);

    private static HashSet<string> RelationshipKeys(Person person) =>
        new(person.Relationships.Select(relationship => RelationshipTerms.Of(PersonText.Fold(relationship))), StringComparer.Ordinal);

    // What was asked, on one line and not too long, for a sentence that repeats it.
    private static string Quote(string? reference)
    {
        var line = string.Join(' ', (reference ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= MaxQuotedLength ? line : line[..(MaxQuotedLength - 1)] + "…";
    }

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToArray();
        return list.Length <= 2 ? string.Join(" and ", list) : string.Join(", ", list[..^1]) + " and " + list[^1];
    }
}
