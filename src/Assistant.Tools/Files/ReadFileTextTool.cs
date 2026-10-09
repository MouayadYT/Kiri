using System.Text.Json;
using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Assistant.Core.Tools;

namespace Assistant.Tools.Files;

/// <summary>
/// <c>read_file_text</c> (PROJECT_SPEC §4.8): reads one file that was found or attached in the conversation, and returns the passages of
/// it that matter for what the user wants to know, each marked with where it is, or passages spread over the whole file when no question
/// is given. It reaches only files the conversation has made known (<see cref="IConversationFiles"/>), so the model cannot read a file
/// it was not shown, whatever it asks for; the Files permission is asked first, and the text is returned as untrusted context, data and
/// never instructions (P9). How much is read is sized for the model's window and the user's limits, as for an attached file, and the
/// user is told when the file was not read whole.
/// </summary>
public sealed class ReadFileTextTool(
    IConversationFiles known,
    IPermissionPolicy permissions,
    IDocumentContextService documents,
    IModelService models,
    ISettingsService settings) : ITool
{
    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = new(
        FileToolResults.ReadFileText,
        "Read one file that was found or attached in this conversation, to answer what the user wants to know about what is in it: what " +
        "it is about, a summary, or a detail. It returns the parts of the file that matter for the question, or parts spread over the " +
        "whole file when the question is a general one.",
        """
        {
          "type": "object",
          "properties": {
            "file": {
              "type": "string",
              "description": "The id of the file, such as f1, from the list of files found or attached."
            },
            "question": {
              "type": "string",
              "description": "What the user wants to know about the file, in plain words: for example summary, due dates, or what the rubric says about evidence."
            }
          },
          "required": ["file"]
        }
        """,
        RiskLevel.ReadOnly)
    {
        RequiredPermission = PermissionCapability.Files,
        Timeout = TimeSpan.FromSeconds(90),
    };

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var reference = arguments.GetProperty("file").GetString() ?? string.Empty;
        var question = arguments.TryGetProperty("question", out var asked) && asked.ValueKind == JsonValueKind.String
            ? asked.GetString() ?? string.Empty
            : string.Empty;

        if (known.Find(context.ConversationId, reference) is not { } file)
        {
            return Failed(call, "No file with that id or name is known in this conversation. Use search_files to find it first.");
        }

        if (file.Item.Type != SearchResultItemType.File)
        {
            return Failed(call, $"{file.Id} is a folder, not a file to read.");
        }

        if (!(await permissions.CheckAsync(PermissionCapability.Files, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return Failed(call, "Files are turned off in Settings, under Permissions, so no file can be read. Tell the user.");
        }

        var limits = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ContextLimits;
        var result = await documents.GetContextAsync(
            file.Path, question, DocumentContextBudget.For(await ModelAsync(cancellationToken).ConfigureAwait(false), limits), cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != DocumentReadStatus.Success)
        {
            return Failed(call, ProblemText(file.Id, result.Status));
        }

        var text = UntrustedContext.Wrap(1, "file", file.Name, result.Text);
        var notice = DocumentContextNotices.For(file.Name, result);
        return new ToolResult(
            call.Id,
            call.ToolName,
            ToolResultStatus.Succeeded,
            FileToolResults.Read(file.Id, file.Name, text, Coverage(result.Selection), notice.Count == 0 ? null : string.Join(' ', notice)));
    }

    // What was read of the file, for the model: all of it, or which parts of how many.
    private static string? Coverage(PassageSelection selection)
    {
        if (selection.IsComplete)
        {
            return null;
        }

        var how = selection.Reason switch
        {
            PassageSelectionReason.Matched => "the parts that best match the question",
            PassageSelectionReason.NoMatch => "the first parts, since nothing matched the question",
            PassageSelectionReason.NoQueryTerms => "parts spread across the whole file",
            _ => "the first parts",
        };
        return $"{selection.Passages.Count} of {selection.TotalPassages} parts: {how}";
    }

    // The model the passages are sized for, when one is set up; its window only guides the sizing.
    private async Task<ModelInfo?> ModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await models.GetActiveModelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string ProblemText(string id, DocumentReadStatus status) => status switch
    {
        DocumentReadStatus.Unsupported =>
            $"{id} is not a kind of file that can be read. These can: {DocumentFileTypes.Described}. Tell the user.",
        DocumentReadStatus.NoText => $"{id} has no text that can be read (a scan, say). Tell the user.",
        DocumentReadStatus.TooLarge => $"{id} is too large to read. Tell the user.",
        DocumentReadStatus.Encrypted => $"{id} is protected by a password, which cannot be used. Tell the user.",
        DocumentReadStatus.Corrupt => $"{id} looks damaged, or is not the kind of file its name says. Tell the user.",
        DocumentReadStatus.NotFound => $"{id} can not be found any more: it may have been moved or deleted. Tell the user.",
        DocumentReadStatus.NotAllowed => $"Files is turned off in Settings, under Permissions, so {id} was not opened. Tell the user.",
        _ => $"{id} could not be opened: another program may be using it, or access may be denied. Tell the user.",
    };

    private static ToolResult Failed(ToolCall call, string message) =>
        new(call.Id, call.ToolName, ToolResultStatus.Failed, FileToolResults.Error(message));
}
