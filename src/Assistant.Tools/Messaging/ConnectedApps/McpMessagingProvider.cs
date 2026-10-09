using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Memory;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging;
using OutgoingMessage = Assistant.Core.Messaging.OutgoingMessage;

namespace Assistant.Tools.Messaging.ConnectedApps;

/// <summary>
/// The Assistant's messaging through whichever messaging app the user has connected as an MCP integration (PROJECT_SPEC §4.8, step 116): the generic MCP route that step 113 left for
/// the messaging apps. It is an <see cref="IMessagingProvider"/>, so <c>draft_message</c> and <c>send_message</c> work on it as on any provider, and it never decides who "my brother"
/// is: that was decided from the user's own list before it is asked, and it is handed a saved person and their saved numbers, addresses and usernames. It finds the connected app that
/// can send a text (an enabled integration with a tool for it, <see cref="MessagingBinder"/>), looks the person's one-to-one chat up with the app's own search by each saved
/// identifier, and never guesses: no chat is "not found", several are a question for the user, and a group is never a chat with a person. A draft sends nothing (it only searches); a
/// send calls the app's send tool, and only with a draft this provider made, to the chat that draft names, with the text that draft holds, after the user was shown exactly that and
/// said yes. The app's own send tool is not offered to the model beside it (<see cref="IMcpToolVeto"/>): it would let a chat the user never saved be messaged. Nothing here is logged but
/// counts and outcomes, and a person's name, number, chat or message never is.
/// </summary>
internal sealed partial class McpMessagingProvider : IMessagingProvider, IMcpToolVeto
{
    private static readonly TimeSpan CatalogBudget = TimeSpan.FromSeconds(8);

    // What is sent to an app reads as it is written, not with a plus sign or an accent written as an escape.
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IInstalledIntegrationRegistry _registry;
    private readonly McpConnectionManager _connections;
    private readonly IPermissionPolicy? _permissions;
    private readonly ILogger<McpMessagingProvider> _logger;
    private readonly IMemoryStore? _memory;

    // The chats drafts were made for, a few of them: what each is and what it was found by, for remembering the one a message is then sent through.
    private const int MaxDraftedChats = 64;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DraftedChat> _drafted = new(StringComparer.Ordinal);

    /// <summary>Creates the provider.</summary>
    /// <param name="registry">The installed integrations.</param>
    /// <param name="connections">What connects to them and calls their tools.</param>
    /// <param name="permissions">The permissions: the Messaging permission is what makes the app's own sending tool the Assistant's to use; without it the tool is not kept from the model.</param>
    /// <param name="logger">Where outcomes are logged.</param>
    /// <param name="memory">
    /// Where the chat a person's messages go to is remembered once the user has sent one (Settings, under Memory), so that a person with several chats is asked
    /// about once. Without it nothing is remembered.
    /// </param>
    public McpMessagingProvider(
        IInstalledIntegrationRegistry registry, McpConnectionManager connections, IPermissionPolicy? permissions, ILogger<McpMessagingProvider> logger,
        IMemoryStore? memory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _connections = connections;
        _permissions = permissions;
        _logger = logger;
        _memory = memory;
    }

    /// <inheritdoc/>
    public string Name => "Your messaging app";

    /// <inheritdoc/>
    public bool IsSample => false;

    /// <inheritdoc/>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        (await CandidatesAsync(cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <inheritdoc/>
    public async Task<MessageDraft> CreateDraftAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var recipient = message.Recipient;

        // Looking for the person's chat reads the messaging app, so the Messaging permission is checked here, in the service, whoever asks (step 119).
        await RequireMessagingAsync(cancellationToken).ConfigureAwait(false);

        // A person with no number, address or username is looked for by their name, and only a one-to-one chat named like them counts (below).
        var candidates = await CandidatesAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            LogOutcome(_logger, "no_app");
            throw new MessagingProviderException(MessagingFailure.NotConnected);
        }

        var failure = MessagingFailure.NotConnected;
        foreach (var integration in candidates)
        {
            McpToolCatalog? catalog;
            try
            {
                catalog = await _connections.GetCatalogAsync(integration.Id, CatalogBudget, cancellationToken).ConfigureAwait(false);
            }
            catch (McpException exception)
            {
                failure = Translate(exception);
                continue;
            }

            if (catalog is null)
            {
                failure = MessagingFailure.Unavailable;
                continue;
            }

            if (MessagingBinder.Bind([.. catalog.Tools.Select(tool => tool.Descriptor)]) is not { } binding)
            {
                continue;
            }

            return await DraftAsync(integration, binding, message, cancellationToken).ConfigureAwait(false);
        }

        LogOutcome(_logger, "no_usable_app");
        throw new MessagingProviderException(failure);
    }

    /// <inheritdoc/>
    public async Task<MessageSendResult> SendAsync(MessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await RequireMessagingAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(draft.Message.Text) || SendReference.TryRead(draft.Reference) is not { } reference)
        {
            throw new MessagingProviderException(MessagingFailure.Rejected);
        }

        // The app and its tool are looked at again: what was drafted is sent only through the same tool of an app that is still there and still does that.
        InstalledIntegration? integration;
        McpToolCatalog? catalog;
        try
        {
            integration = await _registry.GetAsync(reference.IntegrationId, cancellationToken).ConfigureAwait(false);
            catalog = integration is { Enabled: true }
                ? await _connections.GetCatalogAsync(reference.IntegrationId, CatalogBudget, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (IntegrationException)
        {
            throw new MessagingProviderException(MessagingFailure.Unavailable);
        }
        catch (McpException exception)
        {
            throw new MessagingProviderException(Translate(exception), inner: exception);
        }

        if (catalog is null)
        {
            throw new MessagingProviderException(MessagingFailure.Unavailable);
        }

        var binding = MessagingBinder.Bind([.. catalog.Tools.Select(tool => tool.Descriptor)]);
        if (binding is null || binding.Send.Tool.Name != reference.Tool || binding.Send.TargetArgument != reference.TargetArgument || binding.Send.TextArgument != reference.TextArgument)
        {
            throw new MessagingProviderException(MessagingFailure.Rejected);
        }

        using var arguments = JsonDocument.Parse(new JsonObject
        {
            [binding.Send.TargetArgument] = reference.Target,
            [binding.Send.TextArgument] = draft.Message.Text,
        }.ToJsonString(Relaxed));

        McpToolResult result;
        try
        {
            // A call that may have sent the message is never made twice.
            result = await _connections.CallAsync(reference.IntegrationId, binding.Send.Tool, arguments.RootElement.Clone(), safeToRepeat: false, cancellationToken).ConfigureAwait(false);
        }
        catch (McpException exception) when (exception.Failure is McpFailure.TimedOut or McpFailure.Closed)
        {
            // The app may have taken it before it went quiet: it is not said to have been sent, and not said to have failed.
            LogOutcome(_logger, "send_unconfirmed");
            return new MessageSendResult(MessageDeliveryStatus.Pending, draft.Route) { Service = draft.Service, App = draft.App };
        }
        catch (McpException exception)
        {
            LogOutcome(_logger, "send_failed");
            throw new MessagingProviderException(Translate(exception), inner: exception);
        }

        if (result.IsError)
        {
            LogOutcome(_logger, "send_rejected");
            throw new MessagingProviderException(MessagingFailure.Rejected);
        }

        LogOutcome(_logger, "sent");

        // Where a message went is not kept by sending it: a chat the user named for this one message ("on iMessage") is not the one they prefer.
        // What is kept is what they say they prefer, when they are asked (PreferAsync), or what they wrote in Settings, under Memory. One thing is
        // kept without asking: a chat that only a handle the user gave could find. It is not a choice among the person's chats but the only way
        // found to reach them, and without it they would have to give the handle every time.
        if (_drafted.TryGetValue(Key(reference.IntegrationId, reference.Target), out var sentTo) && sentTo.OnlyWay)
        {
            await PreferAsync(draft, CancellationToken.None).ConfigureAwait(false);
        }

        // Which chat is this person's on the service the message went through is kept too (their iMessage chat, their WhatsApp chat): it says nothing
        // about which they prefer, only where "on iMessage" goes the next time it is asked for, without looking for it again.
        if (sentTo is not null)
        {
            await RememberChatAsync(draft, reference, sentTo).ConfigureAwait(false);
        }

        return new MessageSendResult(ChatResults.SaysPending(result) ? MessageDeliveryStatus.Pending : MessageDeliveryStatus.Sent, draft.Route)
        {
            Service = draft.Service,
            App = draft.App,
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<string>> ReservedToolsAsync(InstalledIntegration integration, IReadOnlyList<McpTool> tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integration);
        ArgumentNullException.ThrowIfNull(tools);
        var reserved = new HashSet<string>(StringComparer.Ordinal);

        // Only while the Assistant sends messages itself: the permission is on, and this is an app it can send through.
        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false) || !await MessagingAllowedAsync(cancellationToken).ConfigureAwait(false)
            || MessagingBinder.Bind([.. tools.Select(tool => tool.Descriptor)]) is not { } binding)
        {
            return reserved;
        }

        reserved.Add(binding.Send.Tool.Name);
        if (binding.Find is { } find)
        {
            reserved.Add(find.Tool.Name);
        }

        // A tool that writes words into a chat's draft ("focus_app" with a draft text) is a way to compose to a chat the user never saved, and a small model
        // reaches for it in place of the Assistant's own draft and send: it is kept from the model too.
        foreach (var tool in tools.Where(tool => MessagingBinder.WritesDraft(tool.Descriptor)))
        {
            reserved.Add(tool.Descriptor.Name);
        }

        return reserved;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// While a message is being sent for the user (<see cref="MessagingFlows"/>), none of the app's tools is offered: send_message finds the person in the user's
    /// own list and their chat in the app by itself, and a small model that is also handed the app's search looks "my brother" up there instead, as a name, and
    /// finds nobody. For anything else the user asks of the app (to find a message, to read a chat) its tools are offered as before, without the ones that send.
    /// </remarks>
    public async Task<IReadOnlySet<string>> ReservedToolsAsync(
        InstalledIntegration integration, IReadOnlyList<McpTool> tools, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reserved = await ReservedToolsAsync(integration, tools, cancellationToken).ConfigureAwait(false);

        // Only an app the Assistant sends through has anything reserved, and only while it does.
        if (reserved.Count == 0 || !MessagingFlows.IsSending(context))
        {
            return reserved;
        }

        LogOutcome(_logger, "app_tools_withheld");
        return tools.Select(tool => tool.Descriptor.Name).ToHashSet(StringComparer.Ordinal);
    }

    // The enabled integrations that have a tool that could send a text, in the order they were installed. Only the names the registry kept are looked at: nothing is started.
    private async Task<IReadOnlyList<InstalledIntegration>> CandidatesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<InstalledIntegration> installed;
        try
        {
            installed = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IntegrationException)
        {
            return [];
        }

        return [.. installed.Where(integration => integration.Enabled && MessagingBinder.NamesSuggestSending(integration.Capabilities.ToolNames))];
    }

    // Whether the Assistant sends messages itself, so that the app's own send tool is kept from the model: the permission is on, or set to ask each time (the user is asked when it is used).
    private async Task<bool> MessagingAllowedAsync(CancellationToken cancellationToken) =>
        _permissions is null || (await _permissions.CheckAsync(PermissionCapability.Messaging, cancellationToken).ConfigureAwait(false)).CouldBeAllowed;

    // The service's own check: nothing in the app is looked up or sent while Messaging is off, or set to ask and this use was not approved.
    private async Task RequireMessagingAsync(CancellationToken cancellationToken)
    {
        if (_permissions is not null && !(await _permissions.CheckAsync(PermissionCapability.Messaging, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            LogOutcome(_logger, "not_allowed");
            throw new MessagingProviderException(MessagingFailure.NotAllowed);
        }
    }

    // Finds the chat the message goes to, and says where it would go. Nothing is sent: the app is only asked to search.
    private async Task<MessageDraft> DraftAsync(InstalledIntegration integration, MessagingBinding binding, OutgoingMessage message, CancellationToken cancellationToken)
    {
        var recipient = message.Recipient;
        var app = Plain(integration.Name);

        // An app whose send tool is addressed by a number or a username needs no search: the message is addressed to what the user saved.
        if (binding.Send.Target == SendTarget.Address)
        {
            var addresses = recipient.Identifiers.Where(entry => entry.Kind != PersonIdentifierKind.ChatName).ToList();
            if (addresses.Count == 0)
            {
                LogOutcome(_logger, "no_address");
                throw new MessagingProviderException(MessagingFailure.RecipientNotFound);
            }

            var identifier = addresses.OrderBy(entry => entry.Kind == PersonIdentifierKind.Phone ? 0 : 1).First();
            var route = $"{(identifier.Service.Length > 0 ? identifier.Service + " " : string.Empty)}message to {Plain(recipient.DisplayName)} in {app}";
            return new MessageDraft(message, route, SendReference.Write(integration.Id, binding.Send, identifier.Value), integration.IsSample)
            {
                Service = MessagingServices.Name(identifier.Service),
                App = app,
            };
        }

        var via = message.Via.Trim();

        // The service the user chose for this person in Settings, under Memory ("go through iMessage"), chooses among their chats as if they had said it.
        var preferred = false;
        if (via.Length == 0 && SavedRoute.ServiceOf(_memory, recipient.PersonId) is { Length: > 0 } service)
        {
            (via, preferred) = (service, true);
        }

        // What each chat was found by, so that a chat that is chosen can be found the same way again.
        var foundBy = new Dictionary<string, string>(StringComparer.Ordinal);

        // The chat the user said they prefer for this person is used, as long as the app still finds it the way it did: nobody is asked twice. When the user
        // names another now ("through iMessage this time"), that is looked for instead, for this message alone.
        if (via.Length == 0 && SavedRoute.Read(_memory, recipient.PersonId) is { } saved && saved.IntegrationId == integration.Id)
        {
            var again = await FindAsync(integration, binding.Find!, saved.Query, cancellationToken).ConfigureAwait(false);
            if (again.FirstOrDefault(chat => chat.Id == saved.ChatId && chat.IsGroup != true) is { } known)
            {
                LogOutcome(_logger, "drafted_remembered");
                return Draft(integration, binding, message, known, saved.Query);
            }
        }

        // The user names a service ("on iMessage") and a message has gone to this person's chat on it before: that chat is looked up the way it was found then,
        // and used as long as the app still has it. Nothing else is searched for, so the same words reach the same chat every time. A chat that is gone, or
        // that the user asked to be forgotten (Settings, under Memory), is simply looked for again below.
        if (via.Length > 0 && MessagingServices.Find(via) is { Length: > 0 } asked && SavedRoute.ReadChat(_memory, recipient.PersonId, asked) is { } onService
            && onService.IntegrationId == integration.Id)
        {
            var again = await FindAsync(integration, binding.Find!, onService.Query, cancellationToken).ConfigureAwait(false);
            if (again.FirstOrDefault(chat => chat.Id == onService.ChatId && chat.IsGroup != true) is { } known)
            {
                LogOutcome(_logger, "drafted_remembered_service");
                return Draft(integration, binding, message, known, onService.Query);
            }
        }

        var found = new List<ChatInfo>();
        var seen = new List<string>();

        // Every chat with one person that is named like them, once they were looked for by name: what a service the user names is chosen among.
        List<ChatInfo>? named = null;
        foreach (var identifier in recipient.Identifiers.Where(entry => entry.Kind != PersonIdentifierKind.ChatName).Take(3))
        {
            // A number is asked for as it was saved, and by its digits alone only when that found nothing.
            foreach (var query in Queries(identifier))
            {
                var answer = await FindAsync(integration, binding.Find!, query, cancellationToken).ConfigureAwait(false);
                Note(foundBy, answer, query);
                found.AddRange(answer);
                if (answer.Count > 0)
                {
                    break;
                }
            }
        }

        // One chat is one however often it was found, and a group is never a chat with a person.
        var chats = found.GroupBy(chat => chat.Id, StringComparer.Ordinal).Select(group => group.First()).Where(chat => chat.IsGroup != true).ToList();

        // A chat that holds one of the saved numbers or usernames is the person's; when none does, what the app found for them is all there is, and the user is shown the chat's name.
        var verified = chats.Where(chat => Holds(chat, recipient.Identifiers)).ToList();
        if (verified.Count > 0)
        {
            chats = verified;
        }
        else if (recipient.Identifiers.All(identifier => identifier.Kind == PersonIdentifierKind.ChatName) || HandlesOf(recipient).Count > 0)
        {
            // No number, address or username was saved (only chat names, or nothing): the person is looked for by name, as the app's own search takes it. Only a chat
            // with one person that is named like them counts: a name alone is never taken for the right person, and a group is never a chat with one. A person whose
            // number was saved and not found is not looked for by name: that is a different chat, or none.
            // A handle ("@marcus:beeper.com") is something else again: an app's search looks at what chats are called and does not find it as it is
            // written, so the person is looked for by the name in it too, and the service it is for chooses among their chats.
            (chats, named) = await FindByNameAsync(integration, binding.Find!, recipient, seen, foundBy, cancellationToken).ConfigureAwait(false);
            var services = HandlesOf(recipient).Select(PersonHandles.Service).Where(service => service.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (chats.Count > 1 && services.Count > 0 && chats.Where(chat => services.Contains(MessagingServices.Name(chat.Network), StringComparer.OrdinalIgnoreCase)).ToList() is { Count: > 0 } onIt)
            {
                chats = onIt;
            }
        }

        chats = OnTheSavedServices(chats, recipient, integration.Name);

        // Whether the chat is one that nothing saved for the person found, and only a handle the user gave did.
        var onlyWay = false;
        if (via.Length > 0)
        {
            // The user said which: the service the chat is on ("Beeper", "iMessage"), or something of the chat itself.
            var chosen = Narrow(chats, via);
            if (chosen.Count == 0 && MessagingServices.Find(via).Length > 0)
            {
                // A service ("on iMessage") is looked for among every chat with the person, and not only the likeliest: the chat on it may be the one set aside
                // above, for having more than their name in its title, for being archived, or for being on another service than the handle saved for them. A
                // person who was found by a number is looked for by name for this too.
                named ??= (await FindByNameAsync(integration, binding.Find!, recipient, seen, foundBy, cancellationToken).ConfigureAwait(false)).Named;
                chosen = Narrow(named, via);
            }

            if (chosen.Count == 0 && message.ViaIsUsersOwn && LooksLikeHandle(via))
            {
                // What they gave is in none of the chats found by the person's name: a handle is the chat itself, and is looked for as they gave it. Only words the
                // user wrote themselves are, only when they read as a handle (never a plain word, which finds any chat that mentions it), and only a chat with one
                // person that holds the handle counts. The user is shown the chat's own name before anything is sent.
                var answer = await FindAsync(integration, binding.Find!, via, cancellationToken).ConfigureAwait(false);
                Note(foundBy, answer, via);
                var handle = Compact(via);
                chosen = [.. answer.GroupBy(chat => chat.Id, StringComparer.Ordinal).Select(group => group.First())
                    .Where(chat => chat.IsGroup != true && Compact(chat.Facts).Contains(handle, StringComparison.Ordinal))];
                onlyWay = chosen.Count > 0 && chats.Count == 0;
            }

            if (chosen.Count > 0)
            {
                chats = chosen;
            }
            else if (!preferred && chats.Count > 0 && MessagingServices.Find(via) is { Length: > 0 } wanted
                && !string.Equals(wanted, MessagingServices.Find(integration.Name), StringComparison.OrdinalIgnoreCase))
            {
                // The user named a service ("on iMessage") and none of the person's chats is on it: that is said, with the services their chats are on. A
                // message is never sent through another chat than the one asked for. (The app's own name, "on Beeper", names the app and not a service.)
                LogOutcome(_logger, "no_chat_on_service");
                throw new MessagingProviderException(
                    MessagingFailure.ServiceNotFound,
                    [.. chats.Select(chat => chat.Network.Length > 0 ? MessagingServices.Name(chat.Network) : chat.Title).Where(text => text.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)])
                {
                    Service = wanted,
                };
            }
        }

        switch (chats.Count)
        {
            case 0:
                // What the app did find for the name (the names of chats with one person) is told, so that the user can say which of them is meant: the name they gave may be
                // spelt differently in the app, or be only part of what it shows.
                LogOutcome(_logger, seen.Count > 0 ? "no_chat_named_like_it" : "no_chat_found");
                throw new MessagingProviderException(MessagingFailure.RecipientNotFound, [.. seen.Distinct(StringComparer.OrdinalIgnoreCase).Take(5)]);
            case > 1:
                LogOutcome(_logger, "several_chats");
                // The chats are told apart by the service each is on, and by their names as well when two are on the same one.
                var services = chats.Select(chat => chat.Network.Length > 0 ? MessagingServices.Name(chat.Network) : chat.Title).ToList();
                var options = services.Distinct(StringComparer.OrdinalIgnoreCase).Count() == chats.Count
                    ? services
                    : [.. chats.Select((chat, index) => chat.Title.Length > 0 && chat.Network.Length > 0 ? $"{services[index]} chat \"{chat.Title}\"" : services[index])];
                throw new MessagingProviderException(
                    MessagingFailure.Ambiguous, [.. options.Where(text => text.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)]);
        }

        LogOutcome(_logger, via.Length > 0 ? "drafted_chosen" : "drafted");
        return Draft(integration, binding, message, chats[0], foundBy.GetValueOrDefault(chats[0].Id, string.Empty), onlyWay && chats.Count == 1);
    }

    // The draft for a chat that was found: where it goes in words, and what the send needs to go there and nowhere else.
    private MessageDraft Draft(InstalledIntegration integration, MessagingBinding binding, OutgoingMessage message, ChatInfo chat, string query, bool onlyWay = false)
    {
        // What the chat is and what it was found by stay here, in memory, for the send that follows: the draft's own reference holds only where the message goes,
        // and never a number, a handle or a name.
        if (_drafted.Count >= MaxDraftedChats)
        {
            _drafted.Clear();
        }

        _drafted[Key(integration.Id, chat.Id)] = new DraftedChat(chat.Network, chat.Title, query, onlyWay);
        var recipient = message.Recipient;
        var app = Plain(integration.Name);
        var service = chat.Network.Length > 0 ? chat.Network + " chat" : "Chat";
        var named = chat.Title.Length > 0 && !string.Equals(chat.Title, recipient.DisplayName, StringComparison.OrdinalIgnoreCase) ? $" \"{chat.Title}\"" : string.Empty;
        return new MessageDraft(
            message, $"{service} with {Plain(recipient.DisplayName)}{named} in {app}",
            SendReference.Write(integration.Id, binding.Send, chat.Id), integration.IsSample)
        {
            Service = MessagingServices.Name(chat.Network),
            App = app,
        };
    }

    private static void Note(Dictionary<string, string> foundBy, IReadOnlyList<ChatInfo> chats, string query)
    {
        foreach (var chat in chats)
        {
            foundBy.TryAdd(chat.Id, query);
        }
    }

    // Words that say nothing about which chat: "through", "the", "app".
    private static readonly HashSet<string> ViaNoise = new(StringComparer.Ordinal)
    {
        "through", "via", "the", "app", "chat", "use", "using", "send", "with", "please", "one", "account", "message", "messages", "text", "that", "this", "his", "her", "their",
    };

    // The chats among the person's that the user's words choose: the ones on the service they named, else the ones that hold what they gave (a handle), else the
    // one called exactly that. None when the words fit none of them.
    internal static List<ChatInfo> Narrow(List<ChatInfo> chats, string via)
    {
        var words = PersonText.Fold(via).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length >= 3 && !ViaNoise.Contains(word)).ToList();
        var onService = chats
            .Where(chat => chat.Network.Length > 0 && PersonText.Fold(chat.Network).Replace(" ", string.Empty, StringComparison.Ordinal) is var network
                && (words.Any(word => network.Contains(word, StringComparison.Ordinal))
                    || words.Any(word => PersonText.Fold(MessagingServices.Name(chat.Network)).Replace(" ", string.Empty, StringComparison.Ordinal) == word)))
            .ToList();
        if (onService.Count > 0)
        {
            return onService;
        }

        var handle = Compact(via);
        if (handle.Length >= 4 && chats.Where(chat => Compact(chat.Facts).Contains(handle, StringComparison.Ordinal)).ToList() is { Count: > 0 } holding)
        {
            return holding;
        }

        var title = PersonText.Fold(via);
        return [.. chats.Where(chat => title.Length > 0 && PersonText.Fold(chat.Title) == title)];
    }

    // Whether what the user gave reads as a handle, a username or a number and not as a plain word: it has a mark of one in it and no spaces, or is mostly digits.
    internal static bool LooksLikeHandle(string via)
    {
        var text = via.Trim();
        if (text.Length < 5 || text.Length > 100)
        {
            return false;
        }

        var digits = text.Count(char.IsAsciiDigit);
        return (!text.Contains(' ', StringComparison.Ordinal) && (text.Contains('@', StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal) || text.Contains('.', StringComparison.Ordinal) || text.Contains('_', StringComparison.Ordinal)))
            || (digits >= 7 && text.All(character => char.IsAsciiDigit(character) || character is '+' or '-' or ' ' or '(' or ')'));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// What is kept is the chat the draft was made for: the app, the chat's id there and what it was found by, under the person, with the words
    /// Settings shows for it ("Messages to Sami go through iMessage in Beeper."). It is the user's own answer to "is this the one you prefer?".
    /// </remarks>
    public async Task<bool> PreferAsync(MessageDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (_memory is null || SendReference.TryRead(draft.Reference) is not { } reference
            || !_drafted.TryGetValue(Key(reference.IntegrationId, reference.Target), out var drafted) || drafted.Query.Length == 0)
        {
            return false;
        }

        try
        {
            var recipient = draft.Message.Recipient;
            var app = draft.App.Length > 0 ? draft.App : "the messaging app";
            var service = MessagingServices.Name(drafted.Network);
            var through = service.Length == 0 || string.Equals(service, app, StringComparison.OrdinalIgnoreCase) ? app : $"{service} in {app}";
            var chat = drafted.Title.Length > 0 && !string.Equals(drafted.Title, recipient.DisplayName, StringComparison.OrdinalIgnoreCase) ? $" (the chat \"{drafted.Title}\")" : string.Empty;
            var kept = await _memory.SaveAsync(
                new MemoryEntry(Guid.NewGuid(), MemoryKind.MessageRoute, $"Messages to {recipient.DisplayName} go through {through}{chat}.", DateTimeOffset.UtcNow)
                {
                    Key = recipient.PersonId.ToString("N"),
                    Value = SavedRoute.Write(reference.IntegrationId, reference.Target, drafted.Query),
                },
                CancellationToken.None).ConfigureAwait(false);
            LogOutcome(_logger, kept is null ? "route_not_kept" : "route_preferred");
            return kept is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogOutcome(_logger, "route_not_kept");
            return false;
        }
    }

    // Keeps the chat a message has just gone to as the person's chat on that service. A chat whose service the app did not say, or that nothing found, is not
    // kept; failing to keep it changes nothing about the message, which has gone.
    private async Task RememberChatAsync(MessageDraft draft, SendReference reference, DraftedChat sentTo)
    {
        var service = MessagingServices.Name(sentTo.Network);
        if (_memory is null || service.Length == 0 || sentTo.Query.Length == 0)
        {
            return;
        }

        try
        {
            var recipient = draft.Message.Recipient;
            var app = draft.App.Length > 0 ? draft.App : "the messaging app";
            var chat = sentTo.Title.Length > 0 && !string.Equals(sentTo.Title, recipient.DisplayName, StringComparison.OrdinalIgnoreCase) ? $"the chat \"{sentTo.Title}\"" : "their chat";
            var where = string.Equals(service, app, StringComparison.OrdinalIgnoreCase) ? app : $"{service} in {app}";
            var kept = await _memory.SaveAsync(
                new MemoryEntry(Guid.NewGuid(), MemoryKind.MessageChat, $"On {where}, messages to {recipient.DisplayName} go to {chat}.", DateTimeOffset.UtcNow)
                {
                    Key = SavedRoute.ChatKey(recipient.PersonId, service),
                    Value = SavedRoute.Write(reference.IntegrationId, reference.Target, sentTo.Query),
                },
                CancellationToken.None).ConfigureAwait(false);
            LogOutcome(_logger, kept is null ? "chat_not_kept" : "chat_kept");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogOutcome(_logger, "chat_not_kept");
        }
    }

    // The one-to-one chats the app finds for the person's name, other names and chat names, that are named like them: the likeliest of them, and all of them.
    private async Task<(List<ChatInfo> Best, List<ChatInfo> Named)> FindByNameAsync(
        InstalledIntegration integration, FindBinding find, MessageRecipient recipient, List<string> seen, Dictionary<string, string> foundBy, CancellationToken cancellationToken)
    {
        // A handle is looked for as it is written and by the name in it: "@marcus:beeper.com" and "marcus".
        var names = recipient.Identifiers.Where(entry => entry.Kind == PersonIdentifierKind.ChatName).Select(entry => entry.Value)
            .Append(recipient.DisplayName).Concat(recipient.Aliases)
            .Concat(HandlesOf(recipient).Select(PersonHandles.LocalPart).Where(local => local.Length >= 3))
            .Where(name => name.Trim().Length > 0)
            .DistinctBy(PersonText.Fold)
            .Take(5)
            .ToList();
        var found = new List<ChatInfo>();
        foreach (var name in names)
        {
            var answer = await FindAsync(integration, find, name, cancellationToken).ConfigureAwait(false);
            Note(foundBy, answer, name);
            found.AddRange(answer);
        }

        var folded = names.Select(PersonText.Fold).Where(name => name.Length > 0).ToList();

        // An app that does not say whether a chat is a group (null) is not taken to say it is one: only a chat that is known to be a group is left out.
        var people = found.GroupBy(chat => chat.Id, StringComparer.Ordinal).Select(group => group.First()).Where(chat => chat.IsGroup != true).ToList();
        seen.AddRange(people.Select(chat => chat.Title).Where(title => title.Length > 0));
        List<ChatInfo> named = [.. people.Where(chat => NamedLike(chat, folded))];
        return (BestNamed(named, folded), named);
    }

    // Everything saved for the person that reads as a handle: a username or an address, or a name that was given as one.
    private static List<string> HandlesOf(MessageRecipient recipient) =>
        [.. recipient.Identifiers.Select(identifier => identifier.Value).Append(recipient.DisplayName).Concat(recipient.Aliases)
            .Where(PersonHandles.IsHandle).Distinct(StringComparer.OrdinalIgnoreCase)];

    // The chats a person may be, narrowed the way a person would: a chat named exactly as they are is more likely theirs than one that only has their name in it ("Savannah" and
    // "Savannah Brooks"), and a chat the user has not put away is more likely theirs than one they archived. What is still more than one is left to the caller, who asks.
    private static List<ChatInfo> BestNamed(List<ChatInfo> named, IReadOnlyList<string> foldedNames)
    {
        var exact = named.Where(chat => foldedNames.Contains(PersonText.Fold(chat.Title), StringComparer.Ordinal)).ToList();
        var best = exact.Count > 0 ? exact : named;
        var live = best.Where(chat => !chat.IsArchived).ToList();
        return live.Count > 0 ? live : best;
    }

    // Whether the chat is named like one of the names: the same words, or all the words of the shorter name among the words of the other ("Omar" and "Omar Hassan").
    private static bool NamedLike(ChatInfo chat, IReadOnlyList<string> foldedNames)
    {
        var title = PersonText.Fold(chat.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (title.Length == 0)
        {
            return false;
        }

        foreach (var name in foldedNames)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var (shorter, longer) = words.Length <= title.Length ? (words, title) : (title, words);

            // A slip of the keyboard in a word ("Savanah" for "Savannah") is still that word.
            if (shorter.Length > 0 && shorter.All(word => longer.Any(other => other == word || PersonText.IsClose(other, word))))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<ChatInfo>> FindAsync(InstalledIntegration integration, FindBinding find, string query, CancellationToken cancellationToken)
    {
        using var arguments = JsonDocument.Parse(new JsonObject { [find.QueryArgument] = query }.ToJsonString(Relaxed));
        try
        {
            // Looking chats up changes nothing, so a call that was cut off is made again.
            var result = await _connections.CallAsync(integration.Id, find.Tool, arguments.RootElement.Clone(), safeToRepeat: true, cancellationToken).ConfigureAwait(false);
            var chats = ChatResults.Parse(result);
            if (chats.All(chat => chat.Title.Length == 0))
            {
                // An answer that gave no chat, or none with a name, is drawn in the log (its layout and field names, never what it says), so that the way this app answers can be read.
                LogShape(_logger, ChatResultShape.Describe(result));
            }

            return chats;
        }
        catch (McpException exception)
        {
            LogOutcome(_logger, "search_failed");
            throw new MessagingProviderException(Translate(exception), inner: exception);
        }
    }

    // What a saved number, address or username is looked for as: as it was saved, and for a number also its digits alone, which is how many apps keep it.
    private static IEnumerable<string> Queries(PersonIdentifier identifier)
    {
        yield return identifier.Value;
        if (identifier.Kind == PersonIdentifierKind.Phone)
        {
            var digits = new string(identifier.Value.Where(char.IsDigit).ToArray());
            if (digits.Length >= 5 && digits != identifier.Value)
            {
                yield return digits;
            }
        }
    }

    // Whether the chat's own facts hold one of the identifiers saved for the person.
    private static bool Holds(ChatInfo chat, IReadOnlyList<PersonIdentifier> identifiers)
    {
        var facts = Compact(chat.Facts);
        return identifiers.Any(identifier => Compact(identifier.Value) is { Length: >= 4 } wanted && facts.Contains(wanted, StringComparison.Ordinal));
    }

    private static string Compact(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // When every address the user saved for the person is for a service (a WhatsApp number, a Signal username), only the chats on those services count: the user chose which to
    // keep. An address that is for no service in particular means any service will do, and so does a chat whose service the app did not say.
    private static List<ChatInfo> OnTheSavedServices(List<ChatInfo> chats, MessageRecipient recipient, string app)
    {
        // A service that is the messaging app itself ("Beeper") says which app to use and not which network, so it keeps no chat out.
        var appName = PersonText.Fold(app);
        var services = recipient.Identifiers
            .Select(identifier => PersonText.Fold(identifier.Service))
            .Select(service => service.Length > 0 && (service == appName || appName.Contains(service, StringComparison.Ordinal) && service.Length >= 4) ? string.Empty : service)
            .ToList();
        if (services.Count == 0 || services.Contains(string.Empty))
        {
            return chats;
        }

        // An app may name a service inside a longer word (an account called "local-whatsapp-1"): the saved name is found in it, or the other way round.
        var allowed = services.ToHashSet(StringComparer.Ordinal);
        return [.. chats.Where(chat => chat.Network.Length == 0 || Matches(PersonText.Fold(chat.Network), allowed))];
    }

    private static bool Matches(string network, HashSet<string> allowed) =>
        allowed.Contains(network) || allowed.Any(service => network.Contains(service, StringComparison.Ordinal) || service.Contains(network, StringComparison.Ordinal));

    private static MessagingFailure Translate(McpException exception) => exception.Failure switch
    {
        McpFailure.AuthRequired or McpFailure.Forbidden => MessagingFailure.SignInNeeded,
        McpFailure.Server => MessagingFailure.Rejected,
        _ => MessagingFailure.Unavailable,
    };

    // The app's name for the running words of a route: no mark that Markdown would read.
    private static string Plain(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text.Trim())
        {
            builder.Append(char.IsControl(character) || character is '*' or '_' or '~' or '`' or '[' or ']' or '<' or '>' or '\\' or '|' ? ' ' : character);
        }

        var line = System.Text.RegularExpressions.Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        return line.Length > 60 ? line[..60] : line;
    }

    [LoggerMessage(EventId = 3260, Level = LogLevel.Information, Message = "Messaging through a connected app: {Outcome}")]
    private static partial void LogOutcome(ILogger logger, string outcome);

    [LoggerMessage(EventId = 3261, Level = LogLevel.Information, Message = "A search for chats gave no chat with a name; its answer looks like: {Shape}")]
    private static partial void LogShape(ILogger logger, string shape);

    private static string Key(string integrationId, string chatId) => integrationId + "\n" + chatId;

    private sealed record DraftedChat(string Network, string Title, string Query, bool OnlyWay = false);

    /// <summary>
    /// The chat a person's messages go to, as it is remembered (Settings, under Memory): the app, the chat's id there, and what the chat was found by. It is used
    /// only while the app still finds that chat by those words; a chat that is gone is simply looked for again.
    /// </summary>
    internal sealed record SavedRoute(string IntegrationId, string ChatId, string Query)
    {
        public static string Write(string integrationId, string chatId, string query) =>
            new JsonObject { ["i"] = integrationId, ["c"] = chatId, ["q"] = query }.ToJsonString(Relaxed);

        /// <summary>What is kept when the user chose only the service (Settings, under Memory): no chat, so the chat is found by the service each time.</summary>
        public static string WriteService(string service) => new JsonObject { ["s"] = service }.ToJsonString(Relaxed);

        /// <summary>The service the user chose for the person, when that is all that is kept; null otherwise.</summary>
        public static string? ServiceOf(IMemoryStore? memory, Guid person)
        {
            if (memory?.Find(MemoryKind.MessageRoute, person.ToString("N")) is not { } entry)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(entry.Value) is JsonObject node && node["c"] is null && node["s"]?.GetValue<string>() is { Length: > 0 } service ? service : null;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        /// <summary>What a person's chat on one service is kept under: the person and the service's name, lower case and without spaces.</summary>
        public static string ChatKey(Guid person, string service) =>
            person.ToString("N") + ":" + new string(service.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        /// <summary>The chat that is the person's on <paramref name="service"/>, when a message has gone there before; null otherwise.</summary>
        public static SavedRoute? ReadChat(IMemoryStore? memory, Guid person, string service) =>
            memory?.Find(MemoryKind.MessageChat, ChatKey(person, service)) is { } entry ? Parse(entry) : null;

        public static SavedRoute? Read(IMemoryStore? memory, Guid person) =>
            memory?.Find(MemoryKind.MessageRoute, person.ToString("N")) is { } entry ? Parse(entry) : null;

        private static SavedRoute? Parse(MemoryEntry entry)
        {
            try
            {
                if (JsonNode.Parse(entry.Value) is not JsonObject node)
                {
                    return null;
                }

                var parts = new[] { "i", "c", "q" }.Select(key => node[key]?.GetValue<string>()).ToArray();
                return parts.Any(string.IsNullOrEmpty) ? null : new SavedRoute(parts[0]!, parts[1]!, parts[2]!);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>What a draft keeps so that only that draft can be sent: the app, its tool, the names of its two arguments and where the message goes. It is the draft's own, never shown.</summary>
    private sealed record SendReference(string IntegrationId, string Tool, string TargetArgument, string TextArgument, string Target)
    {
        public static string Write(string integrationId, SendBinding binding, string target) =>
            new JsonObject
            {
                ["i"] = integrationId,
                ["t"] = binding.Tool.Name,
                ["a"] = binding.TargetArgument,
                ["x"] = binding.TextArgument,
                ["v"] = target,
            }.ToJsonString();

        public static SendReference? TryRead(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            try
            {
                if (JsonNode.Parse(reference) is not JsonObject node)
                {
                    return null;
                }

                var parts = new[] { "i", "t", "a", "x", "v" }.Select(key => node[key]?.GetValue<string>()).ToArray();
                return parts.Any(string.IsNullOrEmpty) ? null : new SendReference(parts[0]!, parts[1]!, parts[2]!, parts[3]!, parts[4]!);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }
    }
}
