using System.IO;
using System.Text.Json;
using Assistant.Core.Storage;
using Assistant.UI.ViewModels;

namespace Assistant.UI.History;

/// <summary>Where a chat was had: in the Search or Ask bar's floating conversation, or in the full window.</summary>
public enum ConversationSurface
{
    /// <summary>The floating conversation the Search or Ask bar grows into.</summary>
    Bar = 1,

    /// <summary>The full window, with the history beside it.</summary>
    Window = 2,
}

/// <summary>
/// Which chats were had in the bar and which in the full window, for Cleanup (Settings > Privacy), which deletes the two after different times. A
/// chat is the bar's when it was only ever added to there; one that was opened or added to in the full window is the window's from then on, and
/// one that nothing is known of (it is older than this record) counts as the window's, which is the one kept for longer. Only the chats' ids are
/// kept, in one small file beside the history, written when a chat is first seen and never on the thread that asked: nothing of what was said.
/// </summary>
public sealed class ConversationSurfaces
{
    private readonly string? _file;
    private readonly object _gate = new();
    private readonly object _writing = new();
    private Dictionary<Guid, ConversationSurface>? _known;

    /// <summary>Creates the record beside the history of <paramref name="paths"/>.</summary>
    public ConversationSurfaces(AppPaths paths)
        : this(Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).DatabaseDirectory, "chat-surfaces.json"))
    {
    }

    /// <summary>Creates the record in <paramref name="file"/>, or in memory alone when it is <see langword="null"/>.</summary>
    public ConversationSurfaces(string? file) => _file = file;

    /// <summary>
    /// Notes that the chat <paramref name="conversation"/> was added to, or opened, on <paramref name="surface"/>. The full window is kept once it
    /// is known: a chat that was moved there does not become the bar's again.
    /// </summary>
    public void Note(Guid conversation, ConversationSurface surface)
    {
        lock (_gate)
        {
            var known = Load();
            if (known.TryGetValue(conversation, out var was) && (was == surface || was == ConversationSurface.Window))
            {
                return;
            }

            known[conversation] = surface;
        }

        // Off the caller's thread, which is the one the conversation is shown on.
        Written = Task.Run(Write);
    }

    /// <summary>Completes when what was last noted is on disk.</summary>
    internal Task Written { get; private set; } = Task.CompletedTask;

    /// <summary>Where the chat was had, or <see langword="null"/> when nothing is known of it.</summary>
    public ConversationSurface? Of(Guid conversation)
    {
        lock (_gate)
        {
            return Load().TryGetValue(conversation, out var surface) ? surface : null;
        }
    }

    /// <summary>Forgets every chat that is not among <paramref name="existing"/>: the ones that were deleted.</summary>
    public void Keep(IReadOnlyCollection<Guid> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var kept = existing as IReadOnlySet<Guid> ?? existing.ToHashSet();
        lock (_gate)
        {
            var known = Load();
            var gone = known.Keys.Where(id => !kept.Contains(id)).ToList();
            if (gone.Count == 0)
            {
                return;
            }

            foreach (var id in gone)
            {
                known.Remove(id);
            }
        }

        Write();
    }

    /// <summary>Writes what is known now, and returns when it is on disk. What cannot be written is known for this run only.</summary>
    internal void Write()
    {
        if (_file is null)
        {
            return;
        }

        lock (_writing)
        {
            Saved saved;
            lock (_gate)
            {
                var known = Load();
                saved = new Saved(
                    [.. known.Where(entry => entry.Value == ConversationSurface.Bar).Select(entry => entry.Key)],
                    [.. known.Where(entry => entry.Value == ConversationSurface.Window).Select(entry => entry.Key)]);
            }

            try
            {
                var partial = _file + ".tmp";
                File.WriteAllText(partial, JsonSerializer.Serialize(saved));
                File.Move(partial, _file, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A chat whose surface could not be kept counts as the full window's the next time: the one kept for longer.
            }
        }
    }

    // Called with the gate held.
    private Dictionary<Guid, ConversationSurface> Load()
    {
        if (_known is not null)
        {
            return _known;
        }

        var known = new Dictionary<Guid, ConversationSurface>();
        if (_file is not null)
        {
            try
            {
                if (File.Exists(_file) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file)) is { } saved)
                {
                    foreach (var id in saved.Bar ?? [])
                    {
                        known[id] = ConversationSurface.Bar;
                    }

                    foreach (var id in saved.Window ?? [])
                    {
                        known[id] = ConversationSurface.Window;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A record that cannot be read is begun again: every chat then counts as the full window's.
                known.Clear();
            }
        }

        return _known = known;
    }

    private sealed record Saved(IReadOnlyList<Guid>? Bar, IReadOnlyList<Guid>? Window);
}

/// <summary>Saves a conversation's messages as <paramref name="inner"/> does, and notes which surface the chat was added to on.</summary>
internal sealed class SurfaceRecorder(IConversationRecorder inner, ConversationSurfaces surfaces, ConversationSurface surface) : IConversationRecorder
{
    /// <inheritdoc/>
    public void Record(Guid conversationId, MessageViewModel message)
    {
        surfaces.Note(conversationId, surface);
        inner.Record(conversationId, message);
    }
}