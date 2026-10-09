using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Files;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Tools.Apps;
using Assistant.Tools.Audio;
using Assistant.Tools.Files;
using Assistant.Tools.Screen;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// What the user is asked before the tools that act on Windows do anything (PROJECT_SPEC §4.8, step 115): the exact application, file, folder, volume or capture, as the PC has it, and
/// not what the model called it; and that a call that cannot be done (an application name that fits two, a file the conversation does not know, a kind of file that runs code) is
/// refused before anyone is asked, and that it is the thing the user was shown that is done.
/// </summary>
public sealed class SystemToolConfirmationTests
{
    private static readonly Guid Chat = Guid.NewGuid();
    private static readonly ToolContext Context = new(Chat);

    private static ToolCall Call(string name, string arguments = "{}") => new("c1", name, arguments);

    private static ToolExecutor Executor(FakeConfirmation confirmation, params ITool[] tools) =>
        new(tools, confirmation, new FakePermissions(true));

    private static string Detail(ToolConfirmation question, string label) => question.Details.Single(line => line.Label == label).Value;

    // ---- open_application ----

    [Fact]
    public async Task TheUserIsShownTheOneApplicationThatMatched_AndThatOneIsStarted()
    {
        var launcher = new RecordingApplicationLauncher();
        var notepad = new InstalledApplication(@"C:\Windows\notepad.exe", "Notepad") { ExecutablePath = @"C:\Windows\notepad.exe" };
        var asked = new FakeConfirmation(true);

        await Executor(asked, new OpenApplicationTool(new FakeApplications(notepad, new InstalledApplication("calc", "Calculator")), launcher))
            .ExecuteAsync(Call("open_application", """{"application":"pad"}"""), Context);

        var question = Assert.Single(asked.Shown);
        Assert.Equal(ConfirmationKind.Launch, question.Kind);
        Assert.Equal("Open this application?", question.Title);
        Assert.Equal("Notepad", Detail(question, "Application"));
        Assert.Equal(@"C:\Windows\notepad.exe", Detail(question, "Program"));
        Assert.Equal("Open", question.ApproveLabel);
        Assert.Equal([@"C:\Windows\notepad.exe"], launcher.Launched);
    }

    [Fact]
    public async Task AnApplicationThatIsDeclinedIsNotStarted_AndOneThatFitsTwoOrNoneIsNotAskedAbout()
    {
        var launcher = new RecordingApplicationLauncher();
        var tool = new OpenApplicationTool(new FakeApplications(new InstalledApplication("a", "Brave"), new InstalledApplication("b", "Brave Nightly")), launcher);
        var declined = new FakeConfirmation(false);
        var executor = Executor(declined, tool);

        var no = await executor.ExecuteAsync(Call("open_application", """{"application":"brave"}"""), Context);
        var two = await executor.ExecuteAsync(Call("open_application", """{"application":"bra"}"""), Context);
        var none = await executor.ExecuteAsync(Call("open_application", """{"application":"photoshop"}"""), Context);

        Assert.Equal(ToolResultStatus.Declined, no.Status);
        Assert.Equal(ToolResultStatus.Failed, two.Status);
        Assert.Equal(ToolResultStatus.Failed, none.Status);
        Assert.Contains("More than one application fits that name", two.OutputJson, StringComparison.Ordinal);

        // Only the first was worth asking about.
        Assert.Equal(1, declined.Asked);
        Assert.Empty(launcher.Launched);
    }

    // ---- open_file, reveal_file, open_folder ----

    private static (ConversationFiles Known, RecordingFileLauncher Launcher) Files() => (new ConversationFiles(), new RecordingFileLauncher());

    private static KnownFile Offer(ConversationFiles known, string path, SearchResultItemType type = SearchResultItemType.File) =>
        known.Offer(Chat, [new SearchResultItem(type, Path.GetFileName(path), path) { Extension = Path.GetExtension(path) }])[0];

    [Fact]
    public async Task AFileIsAskedAboutByItsNameAndWhereItIs_NotByTheIdTheModelUsed()
    {
        var (known, launcher) = Files();
        var file = Offer(known, @"C:\Users\me\Documents\Budget 2026.xlsx");
        var asked = new FakeConfirmation(true);

        await Executor(asked, new OpenFileTool(known, launcher)).ExecuteAsync(Call("open_file", JsonSerializer.Serialize(new { file = file.Id })), Context);

        var question = Assert.Single(asked.Shown);
        Assert.Equal(ConfirmationKind.Launch, question.Kind);
        Assert.Equal("Open this file?", question.Title);
        Assert.Equal("Budget 2026.xlsx", Detail(question, "File"));
        Assert.Equal(@"C:\Users\me\Documents", Detail(question, "Location"));
        Assert.Equal("The program set for .xlsx files", Detail(question, "Opens with"));
        Assert.DoesNotContain(question.Details, line => line.Value == file.Id);
        Assert.Equal([@"C:\Users\me\Documents\Budget 2026.xlsx"], launcher.Opened);
    }

    [Fact]
    public async Task ShowingAFileIsAskedAboutToo_WithTheSameNameAndPlace()
    {
        var (known, launcher) = Files();
        Offer(known, @"C:\Users\me\Documents\Budget 2026.xlsx");
        var asked = new FakeConfirmation(false);

        var result = await Executor(asked, new RevealFileTool(known, launcher)).ExecuteAsync(Call("reveal_file", """{"file":"f1"}"""), Context);

        var question = Assert.Single(asked.Shown);
        Assert.Equal("Show this file in File Explorer?", question.Title);
        Assert.Equal("Show", question.ApproveLabel);
        Assert.Equal("Budget 2026.xlsx", Detail(question, "File"));
        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Empty(launcher.Revealed);
    }

    [Fact]
    public async Task AFileThatRunsCode_OrThatTheConversationDoesNotKnow_IsRefusedBeforeAnyoneIsAsked()
    {
        var (known, launcher) = Files();
        Offer(known, @"C:\Users\me\Downloads\setup.exe");
        var asked = new FakeConfirmation(true);
        var executor = Executor(asked, new OpenFileTool(known, launcher));

        var program = await executor.ExecuteAsync(Call("open_file", """{"file":"f1"}"""), Context);
        var unknown = await executor.ExecuteAsync(Call("open_file", """{"file":"f9"}"""), Context);

        Assert.Equal(ToolResultStatus.Failed, program.Status);
        Assert.Equal(ToolResultStatus.Failed, unknown.Status);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(launcher.Opened);
    }

    [Fact]
    public async Task AFolderOfTheUsersOwnIsAskedAboutByWhereItIs_WholePath()
    {
        var (known, launcher) = Files();
        var asked = new FakeConfirmation(true);

        await Executor(asked, new OpenFolderTool(new FakeSystem(), known, launcher)).ExecuteAsync(Call("open_folder", """{"folder":"Downloads"}"""), Context);

        var question = Assert.Single(asked.Shown);
        Assert.Equal("Open this folder?", question.Title);
        Assert.Equal(@"C:\Users\someone\Downloads", Detail(question, "Folder"));
        Assert.DoesNotContain(question.Details, line => line.Label == "Location");
        Assert.Equal([@"C:\Users\someone\Downloads"], launcher.Opened);
    }

    [Fact]
    public async Task AFolderThatWasFoundIsAskedAboutByNameAndPlace_AndAFileIsNotOpenedAsAFolder()
    {
        var (known, launcher) = Files();
        Offer(known, @"D:\Work\Budget", SearchResultItemType.Folder);
        Offer(known, @"D:\Work\notes.txt");
        var asked = new FakeConfirmation(true);
        var executor = Executor(asked, new OpenFolderTool(new FakeSystem(), known, launcher));

        var folder = await executor.ExecuteAsync(Call("open_folder", """{"folder":"f1"}"""), Context);
        var file = await executor.ExecuteAsync(Call("open_folder", """{"folder":"f2"}"""), Context);

        Assert.Equal(ToolResultStatus.Succeeded, folder.Status);
        Assert.Equal(ToolResultStatus.Failed, file.Status);
        var question = Assert.Single(asked.Shown);
        Assert.Equal("Budget", Detail(question, "Folder"));
        Assert.Equal(@"D:\Work", Detail(question, "Location"));
        Assert.Equal([@"D:\Work\Budget"], launcher.Opened);
    }

    // ---- the volume and the screen ----

    [Fact]
    public async Task TheVolumeIsAskedAboutWithWhatItIsNow_AndOnlyChangedOnAYes()
    {
        var system = new FakeSystem { State = new VolumeState(28, false) };
        var asked = new FakeConfirmation(false);
        var executor = Executor(asked, VolumeTools.SetVolume(system), VolumeTools.Mute(system), VolumeTools.Unmute(system));

        await executor.ExecuteAsync(Call("set_volume", """{"percent":30}"""), Context);

        var question = Assert.Single(asked.Shown);
        Assert.Equal("Set the volume to 30%?", question.Title);
        Assert.Equal(ConfirmationKind.ChangeSystem, question.Kind);
        Assert.Equal("28%", Detail(question, "Now"));
        Assert.Equal("Set volume", question.ApproveLabel);
        Assert.Empty(system.Calls);
    }

    [Fact]
    public async Task TheSoundIsTurnedOffAndOnWithoutAQuestion_SinceTheUserCanFlipItBackAtOnce()
    {
        var system = new FakeSystem { State = new VolumeState(28, false) };
        var asked = new FakeConfirmation(false);
        var executor = Executor(asked, VolumeTools.SetVolume(system), VolumeTools.Mute(system), VolumeTools.Unmute(system));

        var muted = await executor.ExecuteAsync(Call("mute"), Context);
        var unmuted = await executor.ExecuteAsync(Call("unmute"), Context);

        Assert.Empty(asked.Shown);
        Assert.Equal((ToolResultStatus.Succeeded, ToolResultStatus.Succeeded), (muted.Status, unmuted.Status));
        Assert.Equal(["mute", "unmute"], system.Calls);
    }

    [Fact]
    public async Task ACaptureOfTheScreenIsAskedAboutWithWhereItGoes_AndNotTakenOnANo()
    {
        var screenshots = new FakeScreenshots();
        var asked = new FakeConfirmation(false);

        var result = await Executor(asked, new TakeScreenshotTool(screenshots)).ExecuteAsync(Call("take_screenshot"), Context);

        var question = Assert.Single(asked.Shown);
        Assert.Equal(ConfirmationKind.Capture, question.Kind);
        Assert.Contains("in memory only", Detail(question, "Where it goes"), StringComparison.Ordinal);
        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Empty(screenshots.Asked);
    }
}
