using System.Text;

namespace Assistant.Core.People;

/// <summary>What kind of address a <see cref="PersonIdentifier"/> is.</summary>
public enum PersonIdentifierKind
{
    /// <summary>A phone number, as the user writes it.</summary>
    Phone = 0,

    /// <summary>An email address.</summary>
    Email = 1,

    /// <summary>A name or id in a messaging app, such as a username or a handle.</summary>
    Username = 2,

    /// <summary>The name of their one-to-one chat in a messaging app, as the app shows it ("Omar"), for an app that finds people by chat name and not by number.</summary>
    ChatName = 3,
}

/// <summary>
/// An address a messaging provider can reach a person by (PROJECT_SPEC §3.5, §4.8, step 112): a phone number, an email address or a
/// username, and optionally the service it is for ("WhatsApp"), because the same number can be used in more than one app. It is private
/// content and is never logged.
/// </summary>
/// <param name="Kind">What the value is.</param>
/// <param name="Value">The number, address or name.</param>
/// <param name="Service">The messaging service it is for, or empty for any.</param>
public sealed record PersonIdentifier(PersonIdentifierKind Kind, string Value, string Service = "")
{
    // Keeps the value (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}");
        return true;
    }
}

/// <summary>
/// Someone the user has told the Assistant about (PROJECT_SPEC §3.5, §4.8, step 112): the name they are known by, other names they are
/// called (<see cref="Aliases"/>, "Bro", "Mama"), how they relate to the user (<see cref="Relationships"/>, "Brother"), and the addresses a
/// messaging provider can reach them by (<see cref="Identifiers"/>). It is kept on this PC, in the Assistant's own database, and what
/// "my brother" means is decided from it and never by a connected app. Names, aliases and addresses are private content: nothing logs them.
/// </summary>
/// <param name="Id">The person's id.</param>
/// <param name="DisplayName">The name the user knows them by, such as "Omar Hassan".</param>
/// <param name="CreatedAt">When the person was added.</param>
/// <param name="UpdatedAt">When the person was last changed.</param>
public sealed record Person(Guid Id, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>Other names the person is called, such as a nickname.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>How the person relates to the user: "Brother", "Colleague". A person may have several.</summary>
    public IReadOnlyList<string> Relationships { get; init; } = [];

    /// <summary>The addresses a messaging provider can reach the person by.</summary>
    public IReadOnlyList<PersonIdentifier> Identifiers { get; init; } = [];

    /// <summary>A new person with a new id, who is not saved until <see cref="IPersonStore.SaveAsync"/> is called.</summary>
    public static Person Create(string displayName, DateTimeOffset now) => new(Guid.NewGuid(), displayName, now, now);

    // Keeps the name, aliases and addresses (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Id = {Id}");
        return true;
    }
}
