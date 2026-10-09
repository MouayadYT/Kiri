using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Files;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.Tools;

namespace Assistant.Tools.Files;

/// <summary>
/// <c>open_folder</c> (PROJECT_SPEC §4.8): opens a folder in File Explorer: one of the user's own (<see cref="Names"/>: where Windows says
/// their Downloads or Documents are, which Windows gives and the model never writes), or a folder that was found in the conversation, by
/// its id. A path is never taken from the model. It has a side effect and needs the Files permission, and the user confirms each call.
/// </summary>
public sealed class OpenFolderTool(ISystemActions system, IConversationFiles known, IFileLauncher launcher) : ITool
{
    /// <summary>The user's own folders the model may name, and which folder each is.</summary>
    public static IReadOnlyDictionary<string, SystemFolder> Names { get; } = new Dictionary<string, SystemFolder>(StringComparer.Ordinal)
    {
        ["home"] = SystemFolder.Home,
        ["desktop"] = SystemFolder.Desktop,
        ["documents"] = SystemFolder.Documents,
        ["downloads"] = SystemFolder.Downloads,
        ["pictures"] = SystemFolder.Pictures,
        ["music"] = SystemFolder.Music,
        ["videos"] = SystemFolder.Videos,
    };

    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        SystemToolResults.OpenFolder,
        "Open a folder in File Explorer: one of the user's own folders (home, desktop, documents, downloads, pictures, music or videos), or " +
        "a folder that was found in this conversation, by its id such as f2.",
        [
            new ToolParameter(
                "folder",
                ToolParameterType.String,
                "Which folder: home, desktop, documents, downloads, pictures, music or videos, or the id of a folder found in this conversation.",
                MaxLength: FileOpening.MaxReferenceLength),
        ],
        RiskLevel.SideEffect,
        PermissionCapability.Files,
        TimeSpan.FromSeconds(20));

    /// <inheritdoc/>
    public Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var target = Resolve(call, arguments, context);
        return Task.FromResult(target.Refusal ?? Open(call, target));
    }

    /// <inheritdoc/>
    /// <remarks>The folder is worked out once: the user is shown its name and where it is, and that folder, and no other, is opened.</remarks>
    public Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var target = Resolve(call, arguments, context);
        if (target.Refusal is { } refusal)
        {
            return Task.FromResult(ToolPlan.Refuse(refusal));
        }

        return Task.FromResult(ToolPlan.Do(
            _ => Task.FromResult(Open(call, target)),
            FileOpening.Ask("Open this folder?", "Open", ConfirmationKind.Launch, "Folder", target.Name, target.Path!, "File Explorer", target.IsUsersFolder)));
    }

    // Which folder the call means: one of the user's own, as Windows has it, or one that was found in the conversation; or why it cannot be opened.
    private Target Resolve(ToolCall call, JsonElement arguments, ToolContext context)
    {
        var reference = (arguments.GetProperty("folder").GetString() ?? string.Empty).Trim();
        var spoken = QuickSearchText.Normalize(reference);
        if (Names.TryGetValue(spoken, out var folder))
        {
            return system.GetFolder(folder) is { } path
                ? new Target(null, spoken, path, $"Opened the {spoken} folder.", $"The {spoken} folder could not be opened. Tell the user.", IsUsersFolder: true)
                : Refused(FileOpening.Failed(call, $"The {spoken} folder could not be opened. Tell the user."));
        }

        if (FileOpening.Resolve(known, context, arguments, "folder") is not { } found)
        {
            return Refused(FileOpening.Failed(
                call,
                "That is not one of the user's folders (home, desktop, documents, downloads, pictures, music, videos) or a folder known in " +
                "this conversation. Use search_files to find it first."));
        }

        return found.Item.Type != SearchResultItemType.Folder
            ? Refused(FileOpening.Failed(call, $"{found.Id} is a file, not a folder. Use open_file or reveal_file."))
            : new Target(
                null, found.Name, found.Path, $"Opened {found.Name}.", $"{found.Id} could not be opened: it may have been moved or deleted. Tell the user.");
    }

    private ToolResult Open(ToolCall call, Target target) =>
        launcher.Open(target.Path!)
            ? FileOpening.Done(call, target.DoneText)
            : FileOpening.Failed(call, target.FailedText);

    private static Target Refused(ToolResult refusal) => new(refusal, string.Empty, null, string.Empty, string.Empty);

    // A folder the call means, or the refusal that says why not.
    private sealed record Target(ToolResult? Refusal, string Name, string? Path, string DoneText, string FailedText, bool IsUsersFolder = false);
}
