using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Core.Files;

/// <summary>
/// The app's <see cref="IConversationFiles"/>: a thread-safe, in-memory list of the files each conversation has made known, bounded
/// (the <see cref="MaxConversations"/> conversations touched last, and <see cref="MaxFiles"/> files each, the oldest forgotten
/// first). Ids are never reused within a conversation, so an id the model remembers never comes to mean another file.
/// </summary>
public sealed class ConversationFiles : IConversationFiles
{
    /// <summary>How many conversations are remembered; the one touched longest ago is forgotten first.</summary>
    public const int MaxConversations = 32;

    /// <summary>How many files one conversation remembers.</summary>
    public const int MaxFiles = 64;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Scope> _scopes = [];
    private long _touch;

    /// <inheritdoc/>
    public IReadOnlyList<KnownFile> Offer(Guid conversationId, IEnumerable<SearchResultItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_gate)
        {
            var scope = ScopeFor(conversationId, create: true)!;
            var known = new List<KnownFile>();
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Path))
                {
                    continue;
                }

                var existing = scope.Files.Find(file => string.Equals(file.Path, item.Path, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new KnownFile("f" + ++scope.Issued, item);
                    scope.Files.Add(existing);
                    if (scope.Files.Count > MaxFiles)
                    {
                        scope.Files.RemoveAt(0);
                    }
                }

                known.Add(existing);
            }

            return known;
        }
    }

    /// <inheritdoc/>
    public KnownFile? Find(Guid conversationId, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var wanted = reference.Trim().Trim('[', ']', '"', '\'', '`', ' ');
        lock (_gate)
        {
            var scope = ScopeFor(conversationId, create: false);
            if (scope is null)
            {
                return null;
            }

            var byId = scope.Files.Find(file => string.Equals(file.Id, wanted, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }

            var byPath = scope.Files.Find(file => string.Equals(file.Path, wanted, StringComparison.OrdinalIgnoreCase));
            if (byPath is not null)
            {
                return byPath;
            }

            // A name the model copied from the list, or a part of one that only one file has.
            var exact = scope.Files.Where(file => string.Equals(file.Name, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(System.IO.Path.GetFileNameWithoutExtension(file.Name), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1)
            {
                return exact[0];
            }

            var partial = scope.Files.Where(file => file.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            return partial.Count == 1 ? partial[0] : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<KnownFile> Get(Guid conversationId, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
        {
            var scope = ScopeFor(conversationId, create: false);
            if (scope is null)
            {
                return [];
            }

            return [.. ids.Select(id => scope.Files.Find(file => string.Equals(file.Id, id, StringComparison.OrdinalIgnoreCase)))
                .OfType<KnownFile>()];
        }
    }

    /// <inheritdoc/>
    public bool Has(Guid conversationId)
    {
        lock (_gate)
        {
            return ScopeFor(conversationId, create: false) is { Files.Count: > 0 };
        }
    }

    /// <inheritdoc/>
    public void Forget(Guid conversationId)
    {
        lock (_gate)
        {
            _scopes.Remove(conversationId);
        }
    }

    // The conversation's list; a new one, forgetting the one touched longest ago of many, when asked to create it.
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

    private sealed class Scope
    {
        public List<KnownFile> Files { get; } = [];

        public long Touched { get; set; }

        // How many ids were handed out, so that one is never handed out again.
        public int Issued { get; set; }
    }
}
