using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;

namespace Assistant.Tools.Files;

/// <summary>
/// <c>search_files</c> (PROJECT_SPEC §4.8): finds the user's own files through Windows Search, from words as the user would say them
/// ("milestone three guidelines", "pdfs about biology from last week"). It runs the same request the Search or Ask bar runs
/// (<see cref="IFileRequestService"/>: the Files permission first, then planning and the index), and makes what it found known to the
/// conversation, so that the model can name a file by its id when it reads it later. What the model gets back holds names, folders and
/// dates, never a path.
/// </summary>
public sealed class SearchFilesTool(IFileRequestService files, IConversationFiles known, ISettingsService? settings = null) : ITool
{
    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = new(
        FileToolResults.SearchFiles,
        "Find the user's own files on this PC by name, or by what they are about. Use it when the user asks to find, show or look for a " +
        "file, or names a file that has not been found in this conversation yet. It returns the files with ids (f1, f2, ...), names and " +
        "folders; the user sees the list too.",
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "What to look for, in plain words, as the user said it, even if a word is misspelled: for example milestone three guidelines, or pdfs about biology from last week."
            }
          },
          "required": ["query"]
        }
        """,
        RiskLevel.ReadOnly)
    {
        RequiredPermission = PermissionCapability.Files,
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var query = arguments.GetProperty("query").GetString()?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return Failed(call, "The query is empty. Say what to look for.");
        }

        // The planner reads a request ("find ..."): the words the model gives are put as one.
        var request = StartsAsARequest(query) ? query : "find " + query;
        var result = await files.FindAsync(request, cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case FileRequestStatus.FilesTurnedOff:
                return Failed(call, "Files are turned off in Settings, under Permissions, so no file can be looked for. Tell the user.");
            case FileRequestStatus.NothingToSearch:
                return Failed(call, "There was nothing to search for in that query. Use the name or topic the user gave.");
            case FileRequestStatus.SearchUnavailable:
                return Failed(call, "Windows Search is turned off or has no index on this PC, so no file can be looked for. Tell the user.");
            case FileRequestStatus.SearchTimedOut:
                return Failed(call, "Windows Search took too long to answer. Tell the user, or try again with something narrower.");
            case FileRequestStatus.SearchFailed:
                return Failed(call, "Windows Search could not run that search. Tell the user.");
            case FileRequestStatus.NothingFound:
                return Succeeded(call, FileToolResults.Found([], "Nothing was found. Windows Search only finds files in the places it indexes.", false));
        }

        // No more than the user's "Files found to ground an answer" go to the model and into the conversation.
        var most = settings is null ? 0 : (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ContextLimits.MaxRetrievedFiles;
        var found = known.Offer(context.ConversationId, most > 0 && result.Items.Count > most ? [.. result.Items.Take(most)] : result.Items);
        var note = result.OtherKind
            ? "No file of that kind has these words in its name, but these files of another kind do."
            : result.Reviewed
                ? "No name has these words exactly; these are the closest."
                : null;
        return Succeeded(call, FileToolResults.Found(found, note, result.AsksForImages));
    }

    // A request that already says to find, show or look for something is left as it is.
    private static bool StartsAsARequest(string query)
    {
        var first = query.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim(',', '.', ':').ToLowerInvariant();
        return first is "find" or "show" or "search" or "locate" or "look" or "get" or "fetch" or "where";
    }

    private static ToolResult Succeeded(ToolCall call, string output) =>
        new(call.Id, call.ToolName, ToolResultStatus.Succeeded, output);

    private static ToolResult Failed(ToolCall call, string message) =>
        new(call.Id, call.ToolName, ToolResultStatus.Failed, FileToolResults.Error(message));
}
