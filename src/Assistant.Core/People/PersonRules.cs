namespace Assistant.Core.People;

/// <summary>A person cannot be saved as written. The words say what to change and never repeat what the user typed.</summary>
public sealed class PersonValidationException(string message) : Exception(message);

/// <summary>The people could not be read or saved (the database could not be opened, or the disk is full).</summary>
public sealed class PersonStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// What a person may be and how it is tidied before it is kept (PROJECT_SPEC §4.8, step 112): the limits, and the one place that trims,
/// removes repeats and checks a name, an alias, a relationship or an address. A store applies it to everything it saves, so what is kept is
/// always tidy, whoever asked for it.
/// </summary>
public static class PersonRules
{
    /// <summary>The longest display name, in characters.</summary>
    public const int MaxNameLength = 80;

    /// <summary>The most aliases one person has.</summary>
    public const int MaxAliases = 12;

    /// <summary>The longest alias, in characters.</summary>
    public const int MaxAliasLength = 40;

    /// <summary>The most relationships one person has.</summary>
    public const int MaxRelationships = 8;

    /// <summary>The longest relationship, in characters.</summary>
    public const int MaxRelationshipLength = 30;

    /// <summary>The most identifiers one person has.</summary>
    public const int MaxIdentifiers = 12;

    /// <summary>The longest identifier value, in characters.</summary>
    public const int MaxIdentifierLength = 100;

    /// <summary>The longest service name, in characters.</summary>
    public const int MaxServiceLength = 40;

    /// <summary>The most people the Assistant keeps; it is a list of the people the user talks to, not an address book.</summary>
    public const int MaxPeople = 500;

    private const int MinPhoneDigits = 5;
    private const int MaxPhoneDigits = 20;

    /// <summary>
    /// <paramref name="person"/> as it is kept: every text trimmed with its spaces made single, aliases, relationships and identifiers
    /// without repeats (by <see cref="PersonText.Fold"/>, so "Bro" and "bro" are one), a relationship without a leading "my", and every
    /// limit and format checked.
    /// </summary>
    /// <exception cref="PersonValidationException">Something cannot be kept; the message says what to change.</exception>
    public static Person Normalize(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);
        var name = Tidy(person.DisplayName);
        if (PersonText.Fold(name).Length == 0)
        {
            throw new PersonValidationException("A person needs a name.");
        }

        if (name.Length > MaxNameLength)
        {
            throw new PersonValidationException($"A name can be at most {MaxNameLength} characters.");
        }

        return Arrange(person with
        {
            DisplayName = name,
            Aliases = Distinct(person.Aliases, MaxAliases, MaxAliasLength, "alias", "aliases", alias => alias),
            Relationships = Distinct(person.Relationships, MaxRelationships, MaxRelationshipLength, "relationship", "relationships", WithoutMy),
            Identifiers = Identifiers(person.Identifiers),
        });
    }

    /// <summary>How people are ordered by name wherever they are listed: without regard to case, in the user's language.</summary>
    public static StringComparer NameOrder => StringComparer.CurrentCultureIgnoreCase;

    /// <summary>
    /// <paramref name="person"/> with their aliases and relationships in alphabetical order (without regard to case) and their addresses by kind (phone, email, username),
    /// then service, then value, so that every store gives the same order for the same person, whatever order the user typed them in.
    /// </summary>
    public static Person Arrange(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return person with
        {
            Aliases = [.. person.Aliases.Order(StringComparer.OrdinalIgnoreCase)],
            Relationships = [.. person.Relationships.Order(StringComparer.OrdinalIgnoreCase)],
            Identifiers =
            [
                .. person.Identifiers
                    .OrderBy(identifier => identifier.Kind)
                    .ThenBy(identifier => identifier.Service, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(identifier => identifier.Value, StringComparer.OrdinalIgnoreCase),
            ],
        };
    }

    /// <summary>The text of a phone number reduced to what identifies it: its digits, and a plus when it had one in front.</summary>
    public static string PhoneKey(string value)
    {
        var digits = new string([.. value.Where(char.IsAsciiDigit)]);
        return value.TrimStart().StartsWith('+') ? "+" + digits : digits;
    }

    /// <summary>Whether two identifiers name the same address: the same kind and service, and the same number, address or name.</summary>
    public static bool SameAddress(PersonIdentifier left, PersonIdentifier right) => Key(left) == Key(right);

    private static string Key(PersonIdentifier identifier) =>
        identifier.Kind + "|" + PersonText.Fold(identifier.Service) + "|"
        + (identifier.Kind == PersonIdentifierKind.Phone ? PhoneKey(identifier.Value) : identifier.Value.Trim().ToLowerInvariant());

    private static PersonIdentifier[] Identifiers(IReadOnlyList<PersonIdentifier>? identifiers)
    {
        var kept = new List<PersonIdentifier>();
        foreach (var identifier in identifiers ?? [])
        {
            var service = Tidy(identifier.Service);
            if (service.Length > MaxServiceLength)
            {
                throw new PersonValidationException($"A service name can be at most {MaxServiceLength} characters.");
            }

            var value = Tidy(identifier.Value);
            if (value.Length == 0)
            {
                throw new PersonValidationException("An address, number or username is empty. Fill it in or remove it.");
            }

            if (value.Length > MaxIdentifierLength)
            {
                throw new PersonValidationException($"A number, address or username can be at most {MaxIdentifierLength} characters.");
            }

            CheckFormat(identifier.Kind, value);
            var tidy = new PersonIdentifier(identifier.Kind, value, service);
            if (!kept.Any(other => SameAddress(other, tidy)))
            {
                kept.Add(tidy);
            }
        }

        if (kept.Count > MaxIdentifiers)
        {
            throw new PersonValidationException($"A person can have at most {MaxIdentifiers} numbers, addresses and usernames.");
        }

        return [.. kept];
    }

    private static void CheckFormat(PersonIdentifierKind kind, string value)
    {
        switch (kind)
        {
            case PersonIdentifierKind.Phone:
                var digits = value.Count(char.IsAsciiDigit);
                var shape = value.All(character => char.IsAsciiDigit(character) || character is ' ' or '+' or '-' or '(' or ')' or '.')
                    && value.LastIndexOf('+') <= 0;
                if (!shape || digits < MinPhoneDigits || digits > MaxPhoneDigits)
                {
                    throw new PersonValidationException(
                        $"A phone number has {MinPhoneDigits} to {MaxPhoneDigits} digits, and may have spaces, brackets, dashes and a plus at the start.");
                }

                break;
            case PersonIdentifierKind.Email:
                var at = value.IndexOf('@');
                if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1 || value.Any(char.IsWhiteSpace)
                    || !value[(at + 1)..].Contains('.') || value.EndsWith('.') || value.Length > 254)
                {
                    throw new PersonValidationException("An email address looks like name@example.com.");
                }

                break;
            default:
                if (value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
                {
                    throw new PersonValidationException("A username has no spaces in it.");
                }

                break;
        }
    }

    private static string[] Distinct(
        IReadOnlyList<string>? values, int most, int longest, string one, string many, Func<string, string> clean)
    {
        var kept = new List<string>();
        var folded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            var text = Tidy(clean(Tidy(value)));
            var key = PersonText.Fold(text);
            if (key.Length == 0)
            {
                continue;
            }

            if (text.Length > longest)
            {
                throw new PersonValidationException($"An {one} can be at most {longest} characters.");
            }

            if (folded.Add(key))
            {
                kept.Add(text);
            }
        }

        if (kept.Count > most)
        {
            throw new PersonValidationException($"A person can have at most {most} {many}.");
        }

        return [.. kept];
    }

    // "my brother" is how the user says it; the relationship is "brother".
    private static string WithoutMy(string value) =>
        value.Equals("my", StringComparison.OrdinalIgnoreCase) ? string.Empty
        : value.StartsWith("my ", StringComparison.OrdinalIgnoreCase) ? value[3..]
        : value;

    // Trimmed, with every run of white space (and any control character) made one space.
    private static string Tidy(string? text) =>
        string.Join(
            ' ',
            new string([.. (text ?? string.Empty).Select(character => char.IsControl(character) || char.IsWhiteSpace(character) ? ' ' : character)])
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
