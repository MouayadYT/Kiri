using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Ipc;
using Assistant.Core.Settings;
using Assistant.ModelHost.FakeEngine;
using Assistant.SmokeTests.Support;
using Assistant.UI.Browser;
using Assistant.UI.Explorer;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// Checklists 8 and 10 (the handoffs from outside the app): the files File Explorer's Ask Assistant sends and the text a browser's extension sends arrive
/// at the running app on its pipe and open the floating conversation with them attached. The sending side is the real thing as far as it can run here:
/// File Explorer starts one process per selected file (sent here as one connection each, with the wire format those processes use), and the browser's
/// extension talks to the real <c>Assistant.BrowserBridge.exe</c> over native messaging.
/// </summary>
public sealed class HandoffSmokeTests
{
    [Fact]
    public Task SeveralFilesFromFileExplorerOpenOneConversationWithAllOfThemAttached_AndAQuestionIsAnsweredFromThem() => Smoke.RunInFolderAsync(async scratch =>
    {
        var report = MakeWordFile(scratch, "report.docx", "The harvest festival raised 3,210 dollars for the library.");
        var notes = scratch.File("docs", "notes.txt");
        await File.WriteAllTextAsync(notes, "The library will buy new shelves with the festival money, about 900 dollars of it.");
        var picture = MakePicture(scratch, "cat.png");

        await using var model = FakeLocalModel.Create(
            scratch, new FakeEngineScenario { Chat = new FakeChatReply { Pieces = ["Both files are about the festival."] } }, readsPictures: true);
        await using var app = await SmokeApp.StartAsync(scratch, model.Replace);
        await app.ChangeSettingsAsync(model.Use);
        var window = app.Get<AssistantWindow>();
        var controller = app.Get<AssistantWindowStateController>();
        var conversation = app.Get<ConversationViewModel>();
        window.ShowActivated = false;

        // As the bootstrapper connects it: files that arrive open the floating conversation, on the UI thread.
        app.Get<ExplorerFileRequests>().Connect(controller.OpenWithFiles, action => UiThread.Dispatcher.BeginInvoke(action));

        // File Explorer's one process per selected file, at the same moment.
        var replies = await Task.WhenAll(
            Send(app.PipeName, new InvocationRequest(InvocationAction.AskAboutFiles, [report])),
            Send(app.PipeName, new InvocationRequest(InvocationAction.AskAboutFiles, [notes])),
            Send(app.PipeName, new InvocationRequest(InvocationAction.AskAboutFiles, [picture])));
        Assert.All(replies, reply => Assert.True(reply.IsAccepted));

        // One conversation, not three: every file attached as the kind of file it is, and the panel open.
        await Wait.UntilAsync(
            () => conversation.Documents.Count == 2 && conversation.Attachments.Count == 1,
            () => $"The files did not all arrive: {conversation.Documents.Count} documents, {conversation.Attachments.Count} pictures.");
        await Wait.UntilAsync(() => window.IsVisible && window.State == AssistantWindowState.FloatingConversation, "The conversation did not open.");
        Assert.Equal(["notes.txt", "report.docx"], conversation.Documents.Select(document => document.Name).Order(StringComparer.Ordinal));
        Assert.Equal("cat.png", Path.GetFileName(conversation.Attachments[0].Path));
        Assert.Empty(conversation.Messages);

        // The question is about all of them: what each says reaches the model.
        Assert.True(conversation.Ask("What do these files have in common?"));
        await Wait.UntilAsync(
            () => conversation.Messages.Count == 2 && conversation.Messages[1].Status == MessageStatus.Complete,
            () => "The question was not answered: " + string.Join(" | ", conversation.Messages.Select(message => message.Status + ": " + message.Text)));
        Assert.Equal("Both files are about the festival.", conversation.Messages[1].Text);

        var asked = Enumerable.Range(1, 4).Where(number => File.Exists(FakeChatReply.RequestPath(model.ModelPath, number)))
            .Select(model.RequestBody).ToArray();
        Assert.Contains(asked, request => request.Contains("3,210", StringComparison.Ordinal));
        Assert.Contains(asked, request => request.Contains("new shelves", StringComparison.Ordinal));

        await AppWindows.CloseAsync(window);
    });

    [Fact]
    public Task TextSelectedInABrowserReachesTheAppThroughTheRealBridge_AndOpensTheConversationWithItAndItsPage() => Smoke.RunInFolderAsync(async scratch =>
    {
        await using var model = FakeLocalModel.Create(scratch, new FakeEngineScenario { Chat = new FakeChatReply { Pieces = ["It means the fox is quick."] } });
        await using var app = await SmokeApp.StartAsync(scratch, model.Replace);
        await app.ChangeSettingsAsync(model.Use);
        var window = app.Get<AssistantWindow>();
        var conversation = app.Get<ConversationViewModel>();
        window.ShowActivated = false;

        // As the bootstrapper connects it: a selection from the browser opens the conversation with the text and where it is from attached.
        var selections = app.Get<AskBrowserSelectionController>();
        app.Get<BrowserSelectionRequests>().Connect(
            (selection, browser) => _ = selections.OpenAsync(selection, browser), action => UiThread.Dispatcher.BeginInvoke(action));

        // The browser's extension talks to the bridge program over native messaging: a 4-byte length, then the JSON. The bridge is the shipped program,
        // copied where it has no app beside it (so it could never start one), and told which pipe to forward to.
        var bridge = ShippedProgram.CopyOf(scratch, "Assistant.BrowserBridge");
        using var process = bridge.Start("--pipe", app.PipeName, "chrome-extension://smoke-test/");
        var message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1,
            type = "selection",
            body = new
            {
                selectionText = "The quick brown fox",
                selectionTruncated = false,
                pageTitle = "A page about foxes",
                pageUrl = "https://example.test/foxes",
                browserName = "Edge",
            },
        });
        await process.StandardInput.BaseStream.WriteAsync(BitConverter.GetBytes(message.Length));
        await process.StandardInput.BaseStream.WriteAsync(message);
        await process.StandardInput.BaseStream.FlushAsync();

        var reply = await ReadMessageAsync(process.StandardOutput.BaseStream);
        Assert.Equal("accepted", reply.GetProperty("type").GetString());

        await Wait.UntilAsync(() => conversation.Texts.Count == 1 && window.IsVisible, "The selection did not open the conversation.");
        Assert.Equal("The quick brown fox", conversation.Texts[0].Text);
        Assert.Equal("A page about foxes", conversation.Texts[0].WebPage?.Title);
        Assert.Equal("Edge", conversation.Texts[0].WebPage?.BrowserName);
        Assert.Equal(AssistantWindowState.FloatingConversation, window.State);

        // The question about it is answered from it.
        Assert.True(conversation.Ask("What does this say?"));
        await Wait.UntilAsync(
            () => conversation.Messages.Count == 2 && conversation.Messages[1].Status == MessageStatus.Complete, "The question was not answered.");
        Assert.Equal("It means the fox is quick.", conversation.Messages[1].Text);
        Assert.Contains("The quick brown fox", model.RequestBody(1), StringComparison.Ordinal);

        // What the user selected is not in the app's logs.
        Assert.DoesNotContain(app.Logs.Lines, line => line.Contains("quick brown fox", StringComparison.OrdinalIgnoreCase));

        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10_000), "The bridge did not end when the browser closed its input.");
        await AppWindows.CloseAsync(window);
    });

    [Fact]
    public async Task TheEntryPointsTheAppShipsAreThere_AndAnswerWithUsageWhenStartedWithNothing()
    {
        using var scratch = new ScratchFolder();

        // Started with no command they do nothing at all, which also shows each one runs on this PC.
        foreach (var name in new[] { "Assistant.ExplorerExtension", "Assistant.BrowserBridge" })
        {
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".exe")), name + " is not shipped beside the app.");
        }

        using var explorer = ShippedProgram.CopyOf(scratch, "Assistant.ExplorerExtension").Start();
        Assert.True(explorer.WaitForExit(15_000), "The Explorer entry point did not end.");
        Assert.Equal(2, explorer.ExitCode);
        await Task.CompletedTask;
    }

    private static async Task<InvocationReply> Send(string pipeName, InvocationRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, LocalPipe.Options);
        await pipe.ConnectAsync(10_000);
        return await InvocationClient.SendAsync(pipe, request);
    }

    private static async Task<JsonElement> ReadMessageAsync(Stream output)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var length = new byte[4];
        await output.ReadExactlyAsync(length, limit.Token);
        var body = new byte[BitConverter.ToInt32(length)];
        await output.ReadExactlyAsync(body, limit.Token);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string MakeWordFile(ScratchFolder scratch, string name, string text)
    {
        var path = scratch.File("docs", name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        document.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));
        return path;
    }

    private static string MakePicture(ScratchFolder scratch, string name)
    {
        var path = scratch.File("docs", name);
        var pixels = new byte[4 * 4 * 4];
        Array.Fill(pixels, (byte)200);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16)));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
