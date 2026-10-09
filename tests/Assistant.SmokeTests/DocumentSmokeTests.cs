using System.IO;
using Assistant.Core.Settings;
using Assistant.ModelHost.FakeEngine;
using Assistant.SmokeTests.Support;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// Checklist 7: a question about one attached document is answered from it. A Word file is made for the check, attached to the conversation as the Explorer
/// handoff and the paperclip attach it, read by the app's own document reader (Files permission first), cut into passages, and the passage the question points
/// to goes to the model with the question; nothing of the file is logged.
/// </summary>
public sealed class DocumentSmokeTests
{
    private const string Passage = "The Falcon launch is on 14 March 2027 and its budget is 48,500 euros.";

    private static readonly FakeEngineScenario Answer = new()
    {
        Chat = new FakeChatReply { Pieces = ["The budget is ", "48,500 euros."] },
    };

    [Fact]
    public Task AQuestionAboutAnAttachedWordFileIsAnsweredFromIt_AndTheFilesTextIsNotLogged() => Smoke.RunInFolderAsync(async scratch =>
    {
        var path = MakeDocument(scratch);
        await using var model = FakeLocalModel.Create(scratch, Answer);
        await using var app = await SmokeApp.StartAsync(scratch, model.Replace);
        await app.ChangeSettingsAsync(model.Use);
        var conversation = app.Get<ConversationViewModel>();

        conversation.StartWithDocument(new DocumentAttachment("falcon.docx", path));
        Assert.Equal("falcon.docx", conversation.Document?.Name);
        Assert.True(conversation.Ask("What is the budget for the launch?"));
        await Wait.UntilAsync(
            () => conversation.Messages.Count == 2 && conversation.Messages[1].Status == MessageStatus.Complete,
            () => "The question was not answered: " + string.Join(" | ", conversation.Messages.Select(message => message.Status + ": " + message.Text)));

        // The answer is the model's, and the message that asked it shows the file it was about.
        Assert.Equal("The budget is 48,500 euros.", conversation.Messages[1].Text);
        Assert.Equal("falcon.docx", conversation.Messages[0].Document?.Name);

        // What the model was given: the question and the passage of the document that answers it, read from the real file.
        var prompt = model.RequestBody(1);
        Assert.Contains("What is the budget for the launch?", prompt, StringComparison.Ordinal);
        Assert.Contains("48,500 euros", prompt, StringComparison.Ordinal);
        Assert.Contains("falcon.docx", prompt, StringComparison.Ordinal);

        Assert.DoesNotContain(app.Logs.Lines, line => line.Contains("48,500", StringComparison.Ordinal) || line.Contains("Falcon", StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    public Task WithTheFilesPermissionOff_TheFileIsNotRead_AndTheModelIsNotGivenItsText() => Smoke.RunInFolderAsync(async scratch =>
    {
        var path = MakeDocument(scratch);
        await using var model = FakeLocalModel.Create(scratch, Answer);
        await using var app = await SmokeApp.StartAsync(scratch, model.Replace);
        await app.ChangeSettingsAsync(settings => model.Use(settings) with { Permissions = settings.Permissions with { Files = false } });
        var conversation = app.Get<ConversationViewModel>();

        conversation.StartWithDocument(new DocumentAttachment("falcon.docx", path));
        Assert.True(conversation.Ask("What is the budget for the launch?"));
        await Wait.UntilAsync(
            () => conversation.Messages.Count == 2 && conversation.Messages[1].Status != MessageStatus.Answering,
            "The question was not answered.");

        // The user is told why, and nothing was asked of the model: the permission is checked before the file is opened.
        Assert.Contains("Files are turned off", conversation.Messages[1].Text, StringComparison.Ordinal);
        Assert.Equal(0, model.EngineLaunches);
    });

    // A document with the passage that answers the question among others that do not, as a person's file has.
    private static string MakeDocument(ScratchFolder scratch)
    {
        var path = scratch.File("docs", "falcon.docx");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body(
            Paragraph("Project Falcon status"),
            Paragraph("The team meets every Tuesday in the small room to go over open tasks and who needs help."),
            Paragraph(Passage),
            Paragraph("Office plants are watered on Fridays and the kitchen is cleaned at the end of the month."),
            Paragraph("Parking passes are renewed each autumn through the front desk.")));
        return path;
    }

    private static Paragraph Paragraph(string text) => new(new Run(new Text(text)));
}
