using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Core.Context;

/// <summary>
/// The app's <see cref="IContextService"/>: a thread-safe, in-memory store of each conversation's context (PROJECT_SPEC
/// §5.5). It holds what it is given in memory only, logs counts and never content, and is bounded: it remembers the
/// <see cref="MaxConversations"/> conversations touched last, and of one conversation at most
/// <see cref="MaxPendingItems"/> waiting items and <see cref="MaxEntries"/> in all, forgetting the oldest sent ones first.
/// </summary>
/// <param name="budgeter">Fits the context into the model's window.</param>
/// <param name="clock">Stamps provenance; the system clock when <see langword="null"/>.</param>
/// <param name="logger">Receives counts; nothing is logged when <see langword="null"/>.</param>
public sealed partial class ContextService(
    ContextBudgeter budgeter,
    TimeProvider? clock = null,
    ILogger<ContextService>? logger = null) : IContextService
{
    /// <summary>How many conversations' context is remembered; the one touched longest ago is forgotten first.</summary>
    public const int MaxConversations = 32;

    /// <summary>How many items may wait for one question.</summary>
    public const int MaxPendingItems = 32;

    /// <summary>How many items, waiting and sent, a conversation remembers.</summary>
    public const int MaxEntries = 128;

    // The longest origin kept.
    private const int MaxOriginLength = 40;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Scope> _scopes = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger<ContextService>.Instance;
    private long _touch;

    /// <inheritdoc/>
    public ContextAddResult Add(Guid conversationId, ContextItem item, string origin)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        var source = ContextPriorityRules.SourceOf(item);
        var canonical = item.Source == source ? item : item with { Source = source };
        var key = ContextKey.Of(canonical);
        origin = origin.Trim();
        var provenance = new ContextProvenance(
            source, origin.Length > MaxOriginLength ? origin[..MaxOriginLength] : origin, _clock.GetUtcNow());

        ContextAddResult result;
        lock (_gate)
        {
            var scope = ScopeFor(conversationId, create: true)!;

            // A sent item the last prompt left out is not in the model's view any more, so the same content is worth adding again. A
            // retained item is for every question, so what was sent once with an earlier one does not stand in for it.
            var existing = scope.Entries.Find(entry =>
                (entry.Key == key || entry.Item.Id == item.Id)
                && (canonical.Retained ? !entry.IsSent : !(entry.IsSent && entry.LastFit == ContextFate.LeftOut)));
            if (existing is not null)
            {
                existing.Provenance.Add(provenance);
                if (canonical.Retained && !existing.Item.Retained)
                {
                    existing.Item = existing.Item with { Retained = true };
                }

                if (existing.IsSent)
                {
                    result = new ContextAddResult(ContextAddOutcome.AlreadyInConversation, existing.Item);
                }
                else
                {
                    // One item with two ways in ranks by the better of them.
                    if (ContextPriorityRules.Of(source, true) < ContextPriorityRules.Of(existing.Item, true))
                    {
                        existing.Item = existing.Item with { Source = source };
                    }

                    result = new ContextAddResult(ContextAddOutcome.Merged, existing.Item);
                }
            }
            else if (scope.Entries.Count(entry => !entry.IsSent) >= MaxPendingItems)
            {
                result = new ContextAddResult(ContextAddOutcome.Rejected, item);
            }
            else
            {
                scope.Entries.Add(new Entry(canonical, key, provenance, ++scope.Arrivals));
                TrimSent(scope);
                result = new ContextAddResult(ContextAddOutcome.Added, canonical);
            }
        }

        LogAdded(_logger, result.Outcome, source, provenance.Origin, item.Type);
        return result;
    }

    /// <inheritdoc/>
    public bool Remove(Guid conversationId, Guid itemId)
    {
        lock (_gate)
        {
            var scope = ScopeFor(conversationId, create: false);
            return scope is not null && scope.Entries.RemoveAll(entry => !entry.IsSent && entry.Item.Id == itemId) > 0;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ContextItem> PendingItems(Guid conversationId)
    {
        lock (_gate)
        {
            var scope = ScopeFor(conversationId, create: false);
            return scope is null ? [] : [.. PendingOf(scope).Select(entry => entry.Item)];
        }
    }

    /// <inheritdoc/>
    public void Commit(Guid conversationId, IReadOnlyList<ContextItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_gate)
        {
            var scope = ScopeFor(conversationId, create: false);
            if (scope is null)
            {
                return;
            }

            var ids = items.Select(item => item.Id).ToHashSet();
            var turn = ++scope.Turns;
            var position = 0;

            // In the order the prompt laid them out, which is the order they keep in the question they were sent with.
            foreach (var entry in PendingOf(scope).Where(entry => ids.Contains(entry.Item.Id) && !entry.Item.Retained).ToList())
            {
                // The conversation's message carries the content from here on. A retained item is not here: it goes with the next
                // question as well, so it keeps its content and goes on waiting.
                entry.Item = entry.Item with { Text = null, ImageData = default, WebPage = null };
                entry.Turn = turn;
                entry.Position = ++position;
            }
        }
    }

    /// <inheritdoc/>
    public ConversationContext GetContext(Guid conversationId)
    {
        lock (_gate)
        {
            var scope = ScopeFor(conversationId, create: false);
            if (scope is null)
            {
                return ConversationContext.Empty;
            }

            return new ConversationContext(
                [.. PendingOf(scope).Select(entry => entry.ToPublic())],
                [.. scope.Entries.Where(entry => entry.IsSent)
                    .OrderBy(entry => ContextPriorityRules.Of(entry.Item, false))
                    .ThenByDescending(entry => entry.Turn)
                    .ThenBy(entry => entry.Position)
                    .Select(entry => entry.ToPublic())]);
        }
    }

    /// <inheritdoc/>
    public BudgetedConversation Prepare(
        Guid conversationId,
        string instructions,
        IReadOnlyList<Message> conversation,
        ModelInfo model,
        ContextLimitSettings limits)
    {
        var fit = budgeter.Fit(instructions, conversation, model, limits);
        lock (_gate)
        {
            var scope = ScopeFor(conversationId, create: false);
            if (scope is not null)
            {
                var fates = fit.Report.Items.ToDictionary(item => item.ItemId, item => item.Fate);
                foreach (var entry in scope.Entries)
                {
                    entry.LastFit = fates.TryGetValue(entry.Item.Id, out var fate) ? fate : null;
                }
            }
        }

        LogPrepared(_logger, fit.Report.Items.Count, fit.Report.ContextItemsShortened, fit.Report.ContextItemsLeftOut);
        return fit;
    }

    /// <inheritdoc/>
    public void Forget(Guid conversationId)
    {
        lock (_gate)
        {
            _scopes.Remove(conversationId);
        }
    }

    // The waiting items in the order the prompt lays them out: by rank, then as they came.
    private static IEnumerable<Entry> PendingOf(Scope scope) =>
        scope.Entries.Where(entry => !entry.IsSent)
            .OrderBy(entry => ContextPriorityRules.Of(entry.Item, true))
            .ThenBy(entry => entry.Arrival);

    // The conversation's store; a new one, forgetting the one touched longest ago of many, when asked to create it.
    private Scope? ScopeFor(Guid conversationId, bool create)
    {
        if (_scopes.TryGetValue(conversationId, out var scope))
        {
            scope.Touched = ++_touch;
            return scope;
        }

        if (!create)
        {
            return null;
        }

        if (_scopes.Count >= MaxConversations)
        {
            _scopes.Remove(_scopes.MinBy(pair => pair.Value.Touched).Key);
        }

        scope = new Scope { Touched = ++_touch };
        _scopes.Add(conversationId, scope);
        return scope;
    }

    // Sent items are only descriptors, kept so that a repeat is recognized; the oldest go first when there are too many.
    private static void TrimSent(Scope scope)
    {
        var excess = scope.Entries.Count - MaxEntries;
        if (excess <= 0)
        {
            return;
        }

        var oldest = scope.Entries.Where(entry => entry.IsSent)
            .OrderBy(entry => entry.Turn).ThenBy(entry => entry.Position).Take(excess).ToHashSet();
        scope.Entries.RemoveAll(oldest.Contains);
    }

    [LoggerMessage(
        EventId = 2305,
        Level = LogLevel.Debug,
        Message = "Context item ({ItemType}) from {Origin} ({Source}): {Outcome}")]
    private static partial void LogAdded(
        ILogger logger, ContextAddOutcome outcome, ContextSource source, string origin, ContextItemType itemType);

    [LoggerMessage(
        EventId = 2306,
        Level = LogLevel.Debug,
        Message = "Context prepared: {Items} items considered, {Shortened} shortened, {LeftOut} left out")]
    private static partial void LogPrepared(ILogger logger, int items, int shortened, int leftOut);

    private sealed class Scope
    {
        public List<Entry> Entries { get; } = [];

        public long Touched { get; set; }

        public long Arrivals { get; set; }

        public int Turns { get; set; }
    }

    private sealed class Entry(ContextItem item, ContextKey key, ContextProvenance first, long arrival)
    {
        public ContextItem Item { get; set; } = item;

        public ContextKey Key { get; } = key;

        public List<ContextProvenance> Provenance { get; } = [first];

        public long Arrival { get; } = arrival;

        // 0 while it waits; the number of the question it was sent with after.
        public int Turn { get; set; }

        // Its place among the items sent with that question.
        public int Position { get; set; }

        public ContextFate? LastFit { get; set; }

        public bool IsSent => Turn > 0;

        public ContextEntry ToPublic() =>
            new(Item, ContextPriorityRules.Of(Item, !IsSent), [.. Provenance], IsSent, LastFit);
    }
}
