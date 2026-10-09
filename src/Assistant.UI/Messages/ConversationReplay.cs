using System.IO;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Messages;

/// <summary>
/// Remakes what the model remembers of a conversation from the messages the user sees, for one that was opened from the saved history:
/// the app's memory of a conversation (what the model was asked and said, the files it was shown) lives only while the app runs, so a
/// conversation continued after a restart would otherwise begin again as if nothing had been said. Each message is told as it was; a list
/// of files an answer showed becomes the model's own call of the tool that finds files and what it returned (the files get their ids in
/// the conversation again), so that "the first one" means the first of that list. Nothing is read from a file.
/// </summary>
internal static class ConversationReplay
{
    // The latest messages only: an old conversation is not told to the model whole.
    private const int MaxMessages = 24;

    /// <summary>Makes the model's messages of the conversation <paramref name="conversationId"/> from <paramref name="earlier"/>, oldest first.</summary>
    /// <param name="earlier">What the conversation held, oldest first.</param>
    /// <param name="conversationId">The conversation, whose files <paramref name="files"/> are made known to.</param>
    /// <param name="files">Where the files that were found or attached are made known to the conversation, or <see langword="null"/> for none.</param>
    public static IReadOnlyList<Message> Rebuild(IReadOnlyList<MessageViewModel> earlier, Guid conversationId, IConversationFiles? files)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        var messages = new List<Message>();
        var question = string.Empty;
        var exchanges = 0;
        for (var index = Math.Max(0, earlier.Count - MaxMessages); index < earlier.Count; index++)
        {
            var message = earlier[index];
            var text = message.Text;
            if (message.Role == MessageRole.User)
            {
                if (message.Documents.Count > 0)
                {
                    files?.Offer(
                        conversationId,
                        message.Documents.Select(document => new SearchResultItem(SearchResultItemType.File, document.Name, document.Path)));
                }

                if (text.Length > 0)
                {
                    messages.Add(new Message(Guid.NewGuid(), MessageRole.User, text, message.CreatedAt));
                    question = text;
                }

                continue;
            }

            if (message.Role != MessageRole.Assistant)
            {
                continue;
            }

            var shown = FilesShownIn(message, out var pictures);
            if (shown.Count > 0 && files is not null && question.Length > 0)
            {
                var known = files.Offer(conversationId, shown);
                var call = new ToolCall(
                    "call_replay" + ++exchanges, FileToolResults.SearchFiles, JsonSerializer.Serialize(new { query = question }));
                var result = new ToolResult(
                    call.Id, call.ToolName, ToolResultStatus.Succeeded, FileToolResults.Found(known, note: null, images: pictures));
                messages.Add(new Message(Guid.NewGuid(), MessageRole.Assistant, string.Empty, message.CreatedAt) { ToolCalls = [call] });
                messages.Add(new Message(Guid.NewGuid(), MessageRole.Tool, result.OutputJson, message.CreatedAt) { ToolResult = result });
            }
            else if (shown.Count > 0)
            {
                files?.Offer(conversationId, shown);
            }

            if (text.Length > 0)
            {
                messages.Add(new Message(Guid.NewGuid(), MessageRole.Assistant, text, message.CreatedAt));
            }
        }

        return messages;
    }

    // The files and pictures an answer lists, as the search found them.
    private static List<SearchResultItem> FilesShownIn(MessageViewModel message, out bool pictures)
    {
        var shown = new List<SearchResultItem>();
        foreach (var file in message.Content.OfType<FileCollection>().SelectMany(collection => collection.Files))
        {
            shown.Add(new SearchResultItem(file.Kind, file.Name, file.Path) { Extension = Path.GetExtension(file.Path).ToLowerInvariant() });
        }

        var images = message.Content.OfType<ImageCollection>().SelectMany(collection => collection.Images)
            .Where(image => image.Path is { Length: > 0 }).ToList();
        foreach (var image in images)
        {
            shown.Add(new SearchResultItem(SearchResultItemType.File, image.Name, image.Path!) { Extension = Path.GetExtension(image.Path!).ToLowerInvariant() });
        }

        pictures = images.Count > 0;
        return shown;
    }
}
