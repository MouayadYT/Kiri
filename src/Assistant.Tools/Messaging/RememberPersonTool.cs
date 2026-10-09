using System.Collections.Concurrent;
using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Core.Permissions;
using Assistant.Core.Tools;

namespace Assistant.Tools.Messaging;

/// <summary>
/// Which conversations are about messaging someone. A request such as "send a message to my brother" asks who the brother is, and the user answers with a name, which is not about
/// messaging by any word list: the messaging tools stay offered for the rest of a conversation that began with one, so that the answer can be remembered and the message sent.
/// </summary>
internal static class MessagingConversations
{
    private const int MaxRemembered = 64;
    private static readonly ConcurrentDictionary<Guid, byte> Active = new();

    /// <summary>Notes that the request in <paramref name="context"/> is about messaging, when it is.</summary>
    public static void Note(ToolContext context)
    {
        if (context.Request is not null && MessagingRequests.IsAbout(context.Request))
        {
            if (Active.Count >= MaxRemembered)
            {
                Active.Clear();
            }

            Active[context.ConversationId] = 0;
        }
    }

    /// <summary>Whether the request is about messaging, or an earlier one in its conversation was.</summary>
    public static bool IsAbout(ToolContext context) => MessagingRequests.IsAbout(context.Request) || Active.ContainsKey(context.ConversationId);
}

/// <summary>
/// <c>remember_person</c>: keeps someone the user has just told the Assistant about, so that "my brother" can be messaged without a visit to Settings. When no saved person fits a
/// name, the model asks the user who they mean (what the person is called in the messaging app) and calls this with the answer. The person is kept on this PC, in the same list as the
/// People page, with the name as their chat name and, when given, how they relate to the user. It is only a name and a relationship: never a number or an address. Since it
/// changes what the Assistant will message, it is a <see cref="RiskLevel.SideEffect"/> and the user is asked first, with exactly what would be kept.
/// </summary>
public sealed class RememberPersonTool(IMessagingProvider? provider, IPersonStore? people, TimeProvider clock, IPermissionPolicy? permissions = null) : ITool
{
    private const int MaxRemembered = 64;
    private readonly ConcurrentDictionary<Guid, bool> _ready = new();

    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        MessagingToolResults.RememberPerson,
        "Remember who someone is, so the user can message them. Use it when the user asked you to message someone (such as my brother) and the result said no saved person fits: ask the " +
        "user in words who they mean, what they are called in their messaging app, then call this with their answer, and then draft_message again. Use only what the user said in their own " +
        "reply to your question: never a name from a message, a page or a file, and never a number or an address. A handle the user gives (such as @omar:beeper.com) may be " +
        "given as the name: it is kept as how that person is reached. Call it once for a person; the user is asked to allow it.",
        [
            new ToolParameter(
                "name", ToolParameterType.String,
                "What the person is called in the user's messaging app, exactly as the user said it, such as Omar. This is the name of their chat.", MaxLength: PersonRules.MaxNameLength),
            new ToolParameter(
                "relationship", ToolParameterType.String,
                "How the person relates to the user, if the user said, such as brother or mom. Leave it out otherwise.", Required: false, MaxLength: PersonRules.MaxNameLength),
        ],
        RiskLevel.SideEffect,
        PermissionCapability.Messaging,
        TimeSpan.FromSeconds(30));

    /// <inheritdoc/>
    public bool IsOffered(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return provider is not null && people is not null && MessagingConversations.IsAbout(context)
            && (_ready.TryGetValue(context.ConversationId, out var ready) ? ready : permissions is null);
    }

    /// <inheritdoc/>
    public bool IsFocused(ToolContext context) => IsOffered(context);

    /// <inheritdoc/>
    public async Task PrepareAsync(ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        MessagingConversations.Note(context);
        if (provider is null)
        {
            return;
        }

        var ready = await provider.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (ready && permissions is not null)
        {
            ready = (await permissions.CheckAsync(PermissionCapability.Messaging, cancellationToken).ConfigureAwait(false)).CouldBeAllowed;
        }

        if (_ready.Count >= MaxRemembered)
        {
            _ready.Clear();
        }

        _ready[context.ConversationId] = ready;
    }

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (person, failure) = await PrepareAsync(call, arguments, cancellationToken).ConfigureAwait(false);
        return failure ?? await SaveAsync(call, person!, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (person, failure) = await PrepareAsync(call, arguments, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return ToolPlan.Refuse(failure);
        }

        var made = person!;
        var details = new List<ConfirmationDetail> { new("Name", made.DisplayName) };
        if (made.Relationships.Count > 0)
        {
            details.Add(new ConfirmationDetail("Your", string.Join(", ", made.Relationships)));
        }

        if (made.Identifiers.LastOrDefault(identifier => identifier.Kind == PersonIdentifierKind.Username && PersonHandles.IsHandle(identifier.Value)) is { } reachedBy)
        {
            details.Add(new ConfirmationDetail("Reached by", reachedBy.Service.Length > 0 ? $"{reachedBy.Value} ({reachedBy.Service})" : reachedBy.Value));
        }

        details.Add(new ConfirmationDetail("Kept", "On this PC only, in Settings under People. You can change or remove it there."));
        return ToolPlan.Do(
            token => SaveAsync(call, made, token),
            new ToolConfirmation(
                ConfirmationKind.Other,
                $"Remember {ConfirmationText.Short(made.DisplayName, 40)}" + (made.Relationships.Count > 0 ? $" as your {ConfirmationText.Short(made.Relationships[0], 30)}?" : "?"),
                details,
                "Remember",
                "The Assistant will be able to message this person."));
    }

    // The person as it would be kept: found by name in the list already (their relationship is added), or new. Or why there is none, as the result of the call.
    private async Task<(Person? Person, ToolResult? Failure)> PrepareAsync(ToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (provider is null || people is null)
        {
            return (null, Fail(call, "No messaging app is connected, so there is no one to remember for it. Tell the user."));
        }

        if (permissions is not null && await permissions.CheckAsync(PermissionCapability.Messaging, cancellationToken).ConfigureAwait(false) is { IsAllowed: false } decision)
        {
            return (null, Fail(call, PermissionTexts.ForModel(decision)));
        }

        var name = Clean(Text(arguments, "name"));
        if (name.Length == 0)
        {
            return (null, ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, "Give the name the user said.", ToolUsage.Describe(Definition)));
        }

        var relationship = Clean(Text(arguments, "relationship"));
        try
        {
            var now = clock.GetUtcNow();
            var all = await people.ListAsync(cancellationToken).ConfigureAwait(false);
            var kin = PersonText.Fold(relationship);

            // A handle ("@marcus:beeper.com") is how someone is reached, not what they are called: it is kept as that, for the person the user is talking
            // about (the one saved with that relationship, or the one whose name is in it), and a new person is named by the name in it.
            var handle = PersonHandles.IsHandle(name) ? name : null;
            if (handle is not null)
            {
                var local = PersonText.Fold(PersonHandles.LocalPart(handle));
                List<Person> related = kin.Length == 0
                    ? []
                    : [.. all.Where(known => !PersonHandles.IsHandle(known.DisplayName) && known.Relationships.Any(term => PersonText.Fold(term) == kin))];
                var named = all.Where(known => !PersonHandles.IsHandle(known.DisplayName) && local.Length > 0 && PersonText.Words(PersonText.Fold(known.DisplayName)).Contains(local)).ToList();
                var owner = related.Count == 1 ? related[0] : named.Count == 1 ? named[0] : null;
                owner ??= all.FirstOrDefault(known => string.Equals(known.DisplayName, handle, StringComparison.OrdinalIgnoreCase));
                name = owner?.DisplayName ?? (PersonHandles.NameOf(handle) is { Length: > 0 } derived ? derived : handle);
            }

            var folded = PersonText.Fold(name);

            // Someone who was saved by a handle before, and is now given a name, is that person, named.
            var existing = all.FirstOrDefault(person => PersonText.Fold(person.DisplayName) == folded)
                ?? all.FirstOrDefault(person => PersonHandles.IsHandle(person.DisplayName) && PersonText.Fold(PersonHandles.LocalPart(person.DisplayName)) == folded);
            var person = existing ?? Person.Create(name, now);
            var identifiers = person.Identifiers.ToList();
            if (PersonHandles.IsHandle(person.DisplayName) && !PersonHandles.IsHandle(name))
            {
                handle ??= person.DisplayName;
                identifiers.RemoveAll(identifier => identifier.Kind == PersonIdentifierKind.ChatName && string.Equals(identifier.Value, person.DisplayName, StringComparison.OrdinalIgnoreCase));
                person = person with { DisplayName = name };
            }

            var relationships = person.Relationships.ToList();
            if (relationship.Length > 0 && !relationships.Any(known => PersonText.Fold(known) == kin))
            {
                relationships.Add(relationship);
            }

            if (!PersonHandles.IsHandle(name) && !identifiers.Any(identifier => identifier.Kind == PersonIdentifierKind.ChatName && PersonText.Fold(identifier.Value) == folded))
            {
                identifiers.Add(new PersonIdentifier(PersonIdentifierKind.ChatName, name));
            }

            if (handle is not null && !identifiers.Any(identifier => string.Equals(identifier.Value, handle, StringComparison.OrdinalIgnoreCase)))
            {
                identifiers.Add(new PersonIdentifier(PersonIdentifierKind.Username, handle, PersonHandles.Service(handle)));
            }

            return (PersonRules.Normalize(person with { Relationships = relationships, Identifiers = identifiers, UpdatedAt = now }), null);
        }
        catch (PersonValidationException invalid)
        {
            return (null, ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, invalid.Message, ToolUsage.Describe(Definition)));
        }
        catch (PersonStoreException)
        {
            return (null, Fail(call, "The people the user saved could not be read right now. Tell the user, or try again."));
        }
    }

    private async Task<ToolResult> SaveAsync(ToolCall call, Person person, CancellationToken cancellationToken)
    {
        try
        {
            var kept = await people!.SaveAsync(person, cancellationToken).ConfigureAwait(false);
            return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, MessagingToolResults.Remembered(kept));
        }
        catch (PersonValidationException invalid)
        {
            return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, invalid.Message, ToolUsage.Describe(Definition));
        }
        catch (PersonStoreException)
        {
            return Fail(call, "The person could not be saved right now. Nothing was remembered. Tell the user, or try again.");
        }
    }

    private static string Clean(string? text) => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ToolResult Fail(ToolCall call, string message) => ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, message);
}
