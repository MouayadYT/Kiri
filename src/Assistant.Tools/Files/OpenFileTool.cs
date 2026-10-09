using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Files;
using Assistant.Core.Tools;

namespace Assistant.Tools.Files;

/// <summary>
/// <c>open_file</c> (PROJECT_SPEC §4.8): opens a file that was found or attached in the conversation with the program the user has chosen
/// for that kind of file, as a double click would. It reaches only files the conversation has made known (<see cref="IConversationFiles"/>),
/// by their ids, so the model never gives a path, and it refuses a kind of file that runs code when it is opened (a program, a script, a
/// shortcut, an installer, <see cref="FileOpening.RunsCode"/>), so that opening can never be a way to run something. It has a side effect
/// and needs the Files permission, and the user confirms each call.
/// </summary>
public sealed class OpenFileTool(IConversationFiles known, IFileLauncher launcher) : ITool
{
    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        SystemToolResults.OpenFile,
        "Open a file that was found or attached in this conversation, with the program the user has chosen for that kind of file. Use it " +
        "when the user asks to open a file. It cannot open programs or scripts; to show a file in its folder use reveal_file.",
        [new ToolParameter("file", ToolParameterType.String, "The id of the file, such as f1, from the list of files found or attached.", MaxLength: FileOpening.MaxReferenceLength)],
        RiskLevel.SideEffect,
        PermissionCapability.Files,
        TimeSpan.FromSeconds(20));

    /// <inheritdoc/>
    public Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Resolve(call, arguments, context, out var file) ?? Open(call, file!));

    /// <inheritdoc/>
    /// <remarks>The file is found once: the user is shown its name and its folder, and that file, and no other, is what is opened.</remarks>
    public Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (Resolve(call, arguments, context, out var file) is { } refusal)
        {
            return Task.FromResult(ToolPlan.Refuse(refusal));
        }

        var found = file!;
        var title = found.Item.Type == SearchResultItemType.Folder ? "Open this folder?" : "Open this file?";
        return Task.FromResult(ToolPlan.Do(
            _ => Task.FromResult(Open(call, found)),
            FileOpening.Ask(
                title, "Open", ConfirmationKind.Launch, found.Item.Type == SearchResultItemType.Folder ? "Folder" : "File", found.Name, found.Path,
                found.Item.Type == SearchResultItemType.File ? FileOpening.OpensWith(found.Path) : "File Explorer")));
    }

    // The file the call names, or why it cannot be opened.
    private ToolResult? Resolve(ToolCall call, JsonElement arguments, ToolContext context, out KnownFile? file)
    {
        file = FileOpening.Resolve(known, context, arguments);
        if (file is null)
        {
            return FileOpening.NotKnown(call);
        }

        return file.Item.Type == SearchResultItemType.File && FileOpening.RunsCode(file.Path)
            ? FileOpening.Failed(
                call, $"{file.Id} is a kind of file that runs code when it is opened, which this tool does not do. Tell the user, or show it with reveal_file.")
            : null;
    }

    private ToolResult Open(ToolCall call, KnownFile file) =>
        launcher.Open(file.Path)
            ? FileOpening.Done(call, $"Opened {file.Name}.")
            : FileOpening.Failed(call, $"{file.Id} could not be opened: it may have been moved or deleted. Tell the user.");
}

/// <summary>
/// <c>reveal_file</c> (PROJECT_SPEC §4.8): shows a file that was found or attached in the conversation, selected in a File Explorer window.
/// It reaches only files the conversation has made known, by their ids. Showing a file runs nothing, so any kind of file may be shown. It
/// has a side effect and needs the Files permission, and the user confirms each call.
/// </summary>
public sealed class RevealFileTool(IConversationFiles known, IFileLauncher launcher) : ITool
{
    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        SystemToolResults.RevealFile,
        "Show a file that was found or attached in this conversation, selected in its folder in File Explorer. Use it when the user asks " +
        "where a file is or to show it in its folder.",
        [new ToolParameter("file", ToolParameterType.String, "The id of the file, such as f1, from the list of files found or attached.", MaxLength: FileOpening.MaxReferenceLength)],
        RiskLevel.SideEffect,
        PermissionCapability.Files,
        TimeSpan.FromSeconds(20));

    /// <inheritdoc/>
    public Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(FileOpening.Resolve(known, context, arguments) is { } file ? Reveal(call, file) : FileOpening.NotKnown(call));

    /// <inheritdoc/>
    /// <remarks>The file is found once: the user is shown its name and its folder, and that file, and no other, is shown.</remarks>
    public Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (FileOpening.Resolve(known, context, arguments) is not { } file)
        {
            return Task.FromResult(ToolPlan.Refuse(FileOpening.NotKnown(call)));
        }

        return Task.FromResult(ToolPlan.Do(
            _ => Task.FromResult(Reveal(call, file)),
            FileOpening.Ask(
                file.Item.Type == SearchResultItemType.Folder ? "Show this folder in File Explorer?" : "Show this file in File Explorer?",
                "Show", ConfirmationKind.Launch, file.Item.Type == SearchResultItemType.Folder ? "Folder" : "File", file.Name, file.Path)));
    }

    private ToolResult Reveal(ToolCall call, KnownFile file) =>
        launcher.Reveal(file.Path)
            ? FileOpening.Done(call, $"Showed {file.Name} in File Explorer.")
            : FileOpening.Failed(call, $"{file.Id} could not be shown: it may have been moved or deleted. Tell the user.");
}
