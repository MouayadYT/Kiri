using Assistant.Core.Domain;
using Assistant.UI.Messages;

namespace Assistant.UI.ViewModels;

/// <summary>
/// Where the History window's conversations come from (PROJECT_SPEC §4.3): the local history, listed without their
/// messages, one conversation's messages when it is opened, and the conversations a search finds. None of it needs a
/// model, and each call does its work away from the UI thread. A call the history cannot answer throws
/// <see cref="HistoryUnavailableException"/>.
/// </summary>
public interface IHistorySource
{
    /// <summary>
    /// The saved conversations, in any order, without their messages: what a card or a row shows of each, with the messages
    /// themselves loaded when the conversation is opened (<see cref="LoadMessagesAsync"/>).
    /// </summary>
    Task<IReadOnlyList<HistoryConversation>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The messages of the conversation <paramref name="id"/>, oldest first, or <see langword="null"/> when it is not saved.
    /// </summary>
    Task<IReadOnlyList<MessageViewModel>?> LoadMessagesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The conversations whose title or messages match <paramref name="query"/>, most recently changed first, each with the
    /// words around what matched. The search reads the local history only.
    /// </summary>
    Task<IReadOnlyList<HistorySearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The saved history cannot be read right now, such as a database that cannot be opened. The History window shows what it
/// has and tries again the next time it is asked; the message carries no content.
/// </summary>
public sealed class HistoryUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HistoryUnavailableException()
        : base("The saved history cannot be read.")
    {
    }

    /// <summary>Creates the exception with its cause.</summary>
    public HistoryUnavailableException(Exception innerException)
        : base("The saved history cannot be read.", innerException)
    {
    }
}

/// <summary>
/// A conversation as the History window lists it. Its card shows the image most recently attached to one of its messages
/// (<see cref="MessageViewModel.Attachments"/>), if any.
/// </summary>
/// <param name="Id">Which conversation it is.</param>
/// <param name="Messages">
/// Its messages, oldest first, or <see langword="null"/> when they are not loaded: a saved conversation is listed without
/// them, and they load when it is opened.
/// </param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Title">Its title, or <see langword="null"/> to use what the user first asked.</param>
public sealed record HistoryConversation(
    Guid Id,
    IReadOnlyList<MessageViewModel>? Messages,
    DateTimeOffset UpdatedAt,
    string? Title = null)
{
    /// <summary>
    /// For a conversation listed without its messages: the text of its latest answer, as it was written, from which its
    /// card takes its preview.
    /// </summary>
    public string? LatestAnswerText { get; init; }

    /// <summary>
    /// For a conversation listed without its messages: the image most recently attached to it, which its card shows in
    /// place of the preview.
    /// </summary>
    public ImageItem? Image { get; init; }
}

/// <summary>A conversation a search found, and why.</summary>
/// <param name="Conversation">The conversation, listed without its messages.</param>
/// <param name="Snippet">
/// The words around what matched in its latest message that has a match, on one line, or <see langword="null"/> when only
/// its title matched.
/// </param>
/// <param name="Matches">Where the searched words are in <paramref name="Snippet"/>.</param>
/// <param name="MessageId">The message the snippet comes from.</param>
public sealed record HistorySearchHit(
    HistoryConversation Conversation,
    string? Snippet = null,
    IReadOnlyList<TextMatch>? Matches = null,
    Guid? MessageId = null);
