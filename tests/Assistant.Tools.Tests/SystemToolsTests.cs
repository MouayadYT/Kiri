using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Files;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.Tools;
using Assistant.Tools.Apps;
using Assistant.Tools.Audio;
using Assistant.Tools.Files;
using Assistant.Tools.Screen;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The tools that act on Windows (PROJECT_SPEC §4.8, step 103): each does one fixed thing, names what it acts on the way the user
/// would, is confirmed when it changes something, and starts nothing the model writes.
/// </summary>
public sealed class SystemToolsTests
{
    private static readonly Guid Chat = Guid.NewGuid();
    private static readonly ToolContext Context = new(Chat);

    private static ToolCall Call(string name, string arguments = "{}") => new("c1", name, arguments);

    // What the user allows: every permission, or only the ones named.
    private sealed class Allowing(params PermissionCapability[] on) : IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionDecision(
                capability, on.Length == 0 || on.Contains(capability) ? PermissionDecisionReason.Granted : PermissionDecisionReason.TurnedOff));
    }

    private static ToolExecutor Executor(IEnumerable<ITool> tools, bool confirm = true, IPermissionPolicy? policy = null) =>
        new(tools, new FakeConfirmation(confirm), policy ?? new Allowing());

    private static (string Code, string Message) Failure(ToolResult result)
    {
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out var message), result.OutputJson);
        return (code, message);
    }

    private static string Message(ToolResult result)
    {
        using var document = JsonDocument.Parse(result.OutputJson);
        return document.RootElement.GetProperty("message").GetString()!;
    }

    // ---- open_application ----

    private static readonly InstalledApplication Calculator = new("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Calculator");
    private static readonly InstalledApplication Notepad = new(@"C:\Windows\notepad.exe", "Notepad");
    private static readonly InstalledApplication Code = new("code-id", "Visual Studio Code");
    private static readonly InstalledApplication Brave = new("brave-id", "Brave");
    private static readonly InstalledApplication BraveNightly = new("brave-nightly-id", "Brave Nightly");
    private static readonly InstalledApplication WordA = new("word-a", "Word");
    private static readonly InstalledApplication WordB = new("word-b", "Microsoft Word");

    private static (ToolExecutor Executor, RecordingApplicationLauncher Launcher) Apps(bool confirm = true, params InstalledApplication[] installed)
    {
        var launcher = new RecordingApplicationLauncher();
        var catalog = new FakeApplications(installed.Length > 0 ? installed : [Calculator, Notepad, Code, Brave, BraveNightly, WordA, WordB]);
        return (Executor([new OpenApplicationTool(catalog, launcher)], confirm), launcher);
    }

    [Theory]
    [InlineData("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("  CALCULATOR  ", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("notepad", @"C:\Windows\notepad.exe")]
    [InlineData("calc", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("visual studio code", "code-id")]
    [InlineData("studio code", "code-id")]
    [InlineData("vscode", null)]
    [InlineData("Brave", "brave-id")]
    public async Task AnApplicationIsStartedByTheNameStartShows_ExactBeforeAnyOther(string asked, string? launched)
    {
        var (executor, launcher) = Apps();

        var result = await executor.ExecuteAsync(Call("open_application", JsonSerializer.Serialize(new { application = asked })), Context);

        if (launched is null)
        {
            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Empty(launcher.Launched);
            return;
        }

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([launched], launcher.Launched);
    }

    [Fact]
    public async Task AFoundApplicationIsNamedInTheResult_NeverItsIdentity()
    {
        var (executor, _) = Apps();

        var result = await executor.ExecuteAsync(Call("open_application", """{"application":"calculator"}"""), Context);

        Assert.Equal("Opened Calculator.", Message(result));
        Assert.DoesNotContain("8wekyb3d8bbwe", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameThatFitsSeveralApplications_IsNotGuessed_TheModelIsToldWhichAndAsksTheUser()
    {
        var (executor, launcher) = Apps();

        var result = await executor.ExecuteAsync(Call("open_application", """{"application":"word"}"""), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(["word-a"], launcher.Launched);

        // "bra" begins both Brave and Brave Nightly.
        var many = await executor.ExecuteAsync(Call("open_application", """{"application":"bra"}"""), Context);
        Assert.Equal(ToolResultStatus.Failed, many.Status);
        Assert.Contains("More than one application fits that name: Brave, Brave Nightly. Ask the user which one.", Failure(many).Message, StringComparison.Ordinal);
        Assert.Single(launcher.Launched);
    }

    [Fact]
    public async Task ApplicationsListedTwiceUnderOneNameAreOne()
    {
        var launcher = new RecordingApplicationLauncher();
        var catalog = new FakeApplications(new InstalledApplication("a1", "Paint"), new InstalledApplication("a2", "Paint"), new InstalledApplication("b", "Paint 3D"));

        var result = await Executor([new OpenApplicationTool(catalog, launcher)]).ExecuteAsync(Call("open_application", """{"application":"paint"}"""), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal(["a1"], launcher.Launched);
    }

    [Theory]
    [InlineData("""{"application":"Photoshop"}""", "No installed application has that name")]
    [InlineData("""{"application":"   "}""", "name is empty")]
    [InlineData("""{"application":"!!!"}""", "name is empty")]
    public async Task AnApplicationThatIsNotThere_OrNoName_IsAFailureTheModelCanPassOn(string arguments, string reason)
    {
        var (executor, launcher) = Apps();

        var result = await executor.ExecuteAsync(Call("open_application", arguments), Context);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains(reason, Failure(result).Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task AnApplicationThatCannotBeStarted_IsAFailure()
    {
        var (executor, launcher) = Apps();
        launcher.Works = false;

        var result = await executor.ExecuteAsync(Call("open_application", """{"application":"notepad"}"""), Context);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("Notepad could not be started", Failure(result).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"application":"notepad","arguments":"C:\\secret.txt"}""")]
    [InlineData("""{"application":"notepad","path":"C:\\Windows\\System32\\cmd.exe"}""")]
    [InlineData("""{"command":"notepad"}""")]
    [InlineData("""{"program":"cmd.exe"}""")]
    [InlineData("""{"application":["notepad"]}""")]
    [InlineData("""{"application":7}""")]
    public async Task NoProgramPathOrArgumentTheModelWritesIsEverRun(string arguments)
    {
        var (executor, launcher) = Apps();

        var result = await executor.ExecuteAsync(Call("open_application", arguments), Context);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(ToolErrors.InvalidArguments, Failure(result).Code);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task AnApplicationNameThatIsTooLongIsRefused()
    {
        var (executor, launcher) = Apps();

        var result = await executor.ExecuteAsync(
            Call("open_application", JsonSerializer.Serialize(new { application = new string('a', OpenApplicationTool.MaxNameLength + 1) })), Context);

        Assert.Equal(ToolErrors.InvalidArguments, Failure(result).Code);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task AnApplicationIsStartedOnlyWhenTheUserConfirms()
    {
        var launcher = new RecordingApplicationLauncher();
        var tool = new OpenApplicationTool(new FakeApplications(Notepad), launcher);
        var declined = await new ToolExecutor([tool], new FakeConfirmation(false)).ExecuteAsync(Call("open_application", """{"application":"notepad"}"""), Context);

        Assert.Equal(ToolResultStatus.Declined, declined.Status);
        Assert.Empty(launcher.Launched);
        Assert.Equal(RiskLevel.SideEffect, tool.Definition.RiskLevel);

        // With no way to ask, nothing runs either.
        var unasked = await new ToolExecutor([tool]).ExecuteAsync(Call("open_application", """{"application":"notepad"}"""), Context);
        Assert.Equal(ToolResultStatus.Declined, unasked.Status);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public void TheMatchingKeepsToTheBestKindOfMatch()
    {
        var all = new[] { Calculator, new InstalledApplication("x", "Calculator Plus"), new InstalledApplication("y", "My Calculator Tool") };

        Assert.Equal(["Calculator"], OpenApplicationTool.Match("calculator", all).Select(match => match.DisplayName));
        Assert.Equal(["Calculator Plus"], OpenApplicationTool.Match("calculator plus", all).Select(match => match.DisplayName));
        Assert.Equal(["Calculator", "Calculator Plus"], OpenApplicationTool.Match("calc", all).Select(match => match.DisplayName));
        Assert.Equal(["My Calculator Tool"], OpenApplicationTool.Match("tool", all).Select(match => match.DisplayName));
        Assert.Equal(["Calculator", "Calculator Plus", "My Calculator Tool"], OpenApplicationTool.Match("lator", all).Select(match => match.DisplayName));
        Assert.Empty(OpenApplicationTool.Match("photoshop", all));
    }

    // ---- open_file and reveal_file ----

    private static (ConversationFiles Known, RecordingFileLauncher Launcher, ToolExecutor Executor) Files(bool confirm = true, IPermissionPolicy? policy = null)
    {
        var known = new ConversationFiles();
        var launcher = new RecordingFileLauncher();
        var executor = Executor(
            [new OpenFileTool(known, launcher), new RevealFileTool(known, launcher), new OpenFolderTool(new FakeSystem(), known, launcher)], confirm, policy);
        return (known, launcher, executor);
    }

    private static KnownFile Offer(ConversationFiles known, string path, SearchResultItemType type = SearchResultItemType.File) =>
        known.Offer(Chat, [new SearchResultItem(type, Path.GetFileName(path), path) { Extension = Path.GetExtension(path) }])[0];

    [Fact]
    public async Task AFileTheConversationKnows_IsOpenedAndShownByItsId_AndTheModelNeverSeesItsPath()
    {
        var (known, launcher, executor) = Files();
        var file = Offer(known, @"C:\Users\me\Documents\Report.pdf");

        var opened = await executor.ExecuteAsync(Call("open_file", JsonSerializer.Serialize(new { file = file.Id })), Context);
        var shown = await executor.ExecuteAsync(Call("reveal_file", JsonSerializer.Serialize(new { file = file.Id })), Context);

        Assert.Equal(ToolResultStatus.Succeeded, opened.Status);
        Assert.Equal(ToolResultStatus.Succeeded, shown.Status);
        Assert.Equal([@"C:\Users\me\Documents\Report.pdf"], launcher.Opened);
        Assert.Equal([@"C:\Users\me\Documents\Report.pdf"], launcher.Revealed);
        Assert.Equal("Opened Report.pdf.", Message(opened));
        Assert.Equal("Showed Report.pdf in File Explorer.", Message(shown));
        Assert.DoesNotContain("Users", opened.OutputJson + shown.OutputJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("f1")]
    [InlineData("F1")]
    [InlineData("[f1]")]
    [InlineData("Report.pdf")]
    [InlineData("report")]
    public async Task AFileIsNamedByItsIdOrItsName(string reference)
    {
        var (known, launcher, executor) = Files();
        Offer(known, @"C:\Users\me\Documents\Report.pdf");

        var result = await executor.ExecuteAsync(Call("open_file", JsonSerializer.Serialize(new { file = reference })), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Single(launcher.Opened);
    }

    [Theory]
    [InlineData("""{"file":"f9"}""")]
    [InlineData("""{"file":"C:\\Users\\me\\secret.txt"}""")]
    [InlineData("""{"file":"..\\..\\Windows\\System32\\notepad.exe"}""")]
    [InlineData("""{"file":"nothing"}""")]
    public async Task AFileTheConversationDoesNotKnow_IsNeverOpened_WhateverTheModelAsksFor(string arguments)
    {
        var (known, launcher, executor) = Files();
        Offer(known, @"C:\Users\me\Documents\Report.pdf");

        var open = await executor.ExecuteAsync(Call("open_file", arguments), Context);
        var reveal = await executor.ExecuteAsync(Call("reveal_file", arguments), Context);

        Assert.Equal(ToolResultStatus.Failed, open.Status);
        Assert.Equal(ToolResultStatus.Failed, reveal.Status);
        Assert.Contains("No file with that id or name is known", Failure(open).Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Opened);
        Assert.Empty(launcher.Revealed);
    }

    [Fact]
    public async Task AnotherConversationsFilesAreNotReachable()
    {
        var (known, launcher, executor) = Files();
        Offer(known, @"C:\Users\me\Documents\Report.pdf");

        var result = await executor.ExecuteAsync(Call("open_file", """{"file":"f1"}"""), new ToolContext(Guid.NewGuid()));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Empty(launcher.Opened);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\setup.exe")]
    [InlineData(@"C:\Users\me\Downloads\SETUP.EXE")]
    [InlineData(@"C:\Users\me\Downloads\run.bat")]
    [InlineData(@"C:\Users\me\Downloads\run.cmd")]
    [InlineData(@"C:\Users\me\Downloads\script.ps1")]
    [InlineData(@"C:\Users\me\Downloads\script.vbs")]
    [InlineData(@"C:\Users\me\Downloads\script.js")]
    [InlineData(@"C:\Users\me\Downloads\app.msi")]
    [InlineData(@"C:\Users\me\Downloads\link.lnk")]
    [InlineData(@"C:\Users\me\Downloads\site.url")]
    [InlineData(@"C:\Users\me\Downloads\page.hta")]
    [InlineData(@"C:\Users\me\Downloads\tool.jar")]
    [InlineData(@"C:\Users\me\Downloads\tool.py")]
    [InlineData(@"C:\Users\me\Downloads\x.reg")]
    [InlineData(@"C:\Users\me\Downloads\x.scr")]
    [InlineData(@"C:\Users\me\Downloads\x.dll")]
    [InlineData(@"C:\Users\me\Downloads\x.docm")]
    [InlineData(@"C:\Users\me\Downloads\noextension")]
    [InlineData(@"C:\Users\me\Downloads\trailingdot.")]
    public async Task AFileThatRunsCodeWhenItIsOpened_IsNeverOpenedByTheModel_ButMayBeShown(string path)
    {
        var (known, launcher, executor) = Files();
        var file = Offer(known, path);

        var open = await executor.ExecuteAsync(Call("open_file", JsonSerializer.Serialize(new { file = file.Id })), Context);
        var reveal = await executor.ExecuteAsync(Call("reveal_file", JsonSerializer.Serialize(new { file = file.Id })), Context);

        Assert.Equal(ToolResultStatus.Failed, open.Status);
        Assert.Contains("runs code when it is opened", Failure(open).Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Opened);
        Assert.Equal(ToolResultStatus.Succeeded, reveal.Status);
        Assert.Equal([path], launcher.Revealed);
    }

    [Theory]
    [InlineData(@"C:\a\x.pdf")]
    [InlineData(@"C:\a\x.docx")]
    [InlineData(@"C:\a\x.xlsx")]
    [InlineData(@"C:\a\x.pptx")]
    [InlineData(@"C:\a\x.txt")]
    [InlineData(@"C:\a\x.md")]
    [InlineData(@"C:\a\x.png")]
    [InlineData(@"C:\a\x.JPG")]
    [InlineData(@"C:\a\x.mp3")]
    [InlineData(@"C:\a\x.mp4")]
    [InlineData(@"C:\a\x.zip")]
    [InlineData(@"C:\a\x.csv")]
    [InlineData(@"C:\a\x.mhtml")]
    [InlineData(@"C:\a\x.html")]
    public async Task OrdinaryDocumentsAndMediaAreOpened(string path)
    {
        var (known, launcher, executor) = Files();
        Offer(known, path);

        var result = await executor.ExecuteAsync(Call("open_file", """{"file":"f1"}"""), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([path], launcher.Opened);
    }

    [Fact]
    public async Task AFolderTheConversationFoundMayBeOpenedWithOpenFile()
    {
        var (known, launcher, executor) = Files();
        var folder = Offer(known, @"C:\Users\me\Projects\Milestone", SearchResultItemType.Folder);

        var result = await executor.ExecuteAsync(Call("open_file", JsonSerializer.Serialize(new { file = folder.Id })), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([@"C:\Users\me\Projects\Milestone"], launcher.Opened);
    }

    [Fact]
    public async Task AFileThatCannotBeOpenedOrShown_IsAFailure()
    {
        var (known, launcher, executor) = Files();
        Offer(known, @"C:\Users\me\Documents\Report.pdf");
        launcher.Works = false;

        var open = await executor.ExecuteAsync(Call("open_file", """{"file":"f1"}"""), Context);
        var reveal = await executor.ExecuteAsync(Call("reveal_file", """{"file":"f1"}"""), Context);

        Assert.Contains("could not be opened", Failure(open).Message, StringComparison.Ordinal);
        Assert.Contains("could not be shown", Failure(reveal).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesAreOpenedOnlyWhenTheFilesPermissionIsOn_AndWhenTheUserConfirms()
    {
        var (known, launcher, off) = Files(policy: new Allowing(PermissionCapability.ScreenCapture));
        Offer(known, @"C:\Users\me\Documents\Report.pdf");

        foreach (var name in new[] { "open_file", "reveal_file" })
        {
            var refused = await off.ExecuteAsync(Call(name, """{"file":"f1"}"""), Context);
            Assert.Equal(ToolErrors.PermissionOff, Failure(refused).Code);
            Assert.Contains("Files is turned off", Failure(refused).Message, StringComparison.Ordinal);
        }

        Assert.Empty(launcher.Opened);
        Assert.Empty(launcher.Revealed);

        var (known2, launcher2, declined) = Files(confirm: false);
        Offer(known2, @"C:\Users\me\Documents\Report.pdf");
        Assert.Equal(ToolResultStatus.Declined, (await declined.ExecuteAsync(Call("open_file", """{"file":"f1"}"""), Context)).Status);
        Assert.Equal(ToolResultStatus.Declined, (await declined.ExecuteAsync(Call("reveal_file", """{"file":"f1"}"""), Context)).Status);
        Assert.Empty(launcher2.Opened);
        Assert.Empty(launcher2.Revealed);
    }

    [Theory]
    [InlineData("open_file", """{"file":"f1","path":"C:\\x"}""")]
    [InlineData("reveal_file", """{"file":"f1","with":"cmd.exe"}""")]
    [InlineData("open_file", """{"path":"C:\\Windows\\notepad.exe"}""")]
    [InlineData("open_file", """{"file":["f1"]}""")]
    public async Task TheFileToolsTakeOnlyAnId(string tool, string arguments)
    {
        var (known, launcher, executor) = Files();
        Offer(known, @"C:\Users\me\Documents\Report.pdf");

        var result = await executor.ExecuteAsync(Call(tool, arguments), Context);

        Assert.Equal(ToolErrors.InvalidArguments, Failure(result).Code);
        Assert.Empty(launcher.Opened);
        Assert.Empty(launcher.Revealed);
    }

    // ---- open_folder ----

    [Theory]
    [InlineData("downloads", @"C:\Users\someone\Downloads")]
    [InlineData("Downloads", @"C:\Users\someone\Downloads")]
    [InlineData(" documents ", @"C:\Users\someone\Documents")]
    [InlineData("home", @"C:\Users\someone")]
    public async Task OneOfTheUsersOwnFoldersIsOpenedByName_FromWhereWindowsSaysItIs(string name, string path)
    {
        var (_, launcher, executor) = Files();

        var result = await executor.ExecuteAsync(Call("open_folder", JsonSerializer.Serialize(new { folder = name })), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([path], launcher.Opened);
        Assert.DoesNotContain("Users", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderWindowsDoesNotHave_OrCannotBeOpened_IsAFailure()
    {
        var (_, launcher, executor) = Files();

        var missing = await executor.ExecuteAsync(Call("open_folder", """{"folder":"music"}"""), Context);
        launcher.Works = false;
        var broken = await executor.ExecuteAsync(Call("open_folder", """{"folder":"downloads"}"""), Context);

        Assert.Contains("music folder could not be opened", Failure(missing).Message, StringComparison.Ordinal);
        Assert.Contains("downloads folder could not be opened", Failure(broken).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderTheConversationFoundIsOpenedById_AndAFileIsNotAFolder()
    {
        var (known, launcher, executor) = Files();
        var folder = Offer(known, @"C:\Users\me\Projects\Milestone", SearchResultItemType.Folder);
        var file = Offer(known, @"C:\Users\me\Projects\notes.txt");

        var opened = await executor.ExecuteAsync(Call("open_folder", JsonSerializer.Serialize(new { folder = folder.Id })), Context);
        var wrong = await executor.ExecuteAsync(Call("open_folder", JsonSerializer.Serialize(new { folder = file.Id })), Context);

        Assert.Equal(ToolResultStatus.Succeeded, opened.Status);
        Assert.Equal([@"C:\Users\me\Projects\Milestone"], launcher.Opened);
        Assert.Contains("is a file, not a folder", Failure(wrong).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"folder":"C:\\Windows\\System32"}""")]
    [InlineData("""{"folder":"\\\\server\\share"}""")]
    [InlineData("""{"folder":"f77"}""")]
    [InlineData("""{"folder":"c:"}""")]
    [InlineData("""{"folder":"..\\"}""")]
    [InlineData("""{"folder":"desktop\\..\\..\\Windows"}""")]
    public async Task APathTheModelWritesIsNeverOpened(string arguments)
    {
        var (_, launcher, executor) = Files();

        var result = await executor.ExecuteAsync(Call("open_folder", arguments), Context);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("not one of the user's folders", Failure(result).Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Opened);
    }

    [Fact]
    public async Task FoldersAreOpenedOnlyWithTheFilesPermission_AndTheUsersConfirmation()
    {
        var (_, launcher, off) = Files(policy: new Allowing(PermissionCapability.ScreenCapture));
        Assert.Equal(ToolErrors.PermissionOff, Failure(await off.ExecuteAsync(Call("open_folder", """{"folder":"downloads"}"""), Context)).Code);

        var (_, _, declined) = Files(confirm: false);
        Assert.Equal(ToolResultStatus.Declined, (await declined.ExecuteAsync(Call("open_folder", """{"folder":"downloads"}"""), Context)).Status);

        Assert.Empty(launcher.Opened);
    }

    [Fact]
    public void EveryFolderNameTheModelMayUseIsOneOfTheUsersOwn()
    {
        Assert.Equal(
            ["home", "desktop", "documents", "downloads", "pictures", "music", "videos"],
            OpenFolderTool.Names.Keys);
        Assert.Equal(Enum.GetValues<SystemFolder>().Order(), OpenFolderTool.Names.Values.Order());
    }

    // ---- The volume ----

    private static (ToolExecutor Executor, FakeSystem System) Volume(bool confirm = true, IPermissionPolicy? policy = null)
    {
        var system = new FakeSystem();
        var executor = Executor(
            [VolumeTools.GetVolume(system), VolumeTools.SetVolume(system), VolumeTools.Mute(system), VolumeTools.Unmute(system)], confirm, policy);
        return (executor, system);
    }

    [Fact]
    public async Task TheVolumeIsReadWithNoConfirmation()
    {
        var system = new FakeSystem { State = new VolumeState(63, true) };
        var confirmation = new FakeConfirmation(false);
        var executor = new ToolExecutor([VolumeTools.GetVolume(system)], confirmation);

        var result = await executor.ExecuteAsync(Call("get_volume"), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal("""{"volume":63,"muted":true}""", result.OutputJson);
        Assert.Equal(0, confirmation.Asked);
        Assert.Empty(system.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(35)]
    [InlineData(100)]
    public async Task TheVolumeIsSet_AndWhatTheSpeakersAreNowIsRead_Back(int percent)
    {
        var (executor, system) = Volume();

        var result = await executor.ExecuteAsync(Call("set_volume", $$"""{"percent":{{percent}}}"""), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([$"volume {percent}"], system.Calls);
        using var json = JsonDocument.Parse(result.OutputJson);
        Assert.Equal(percent, json.RootElement.GetProperty("volume").GetInt32());
        Assert.Equal($"The volume is now {percent}%.", json.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("""{"percent":101}""")]
    [InlineData("""{"percent":-5}""")]
    [InlineData("""{"percent":2147483648}""")]
    [InlineData("""{"percent":50.5}""")]
    [InlineData("""{"percent":"loud"}""")]
    [InlineData("""{"percent":"50"}""")]
    [InlineData("""{"level":50}""")]
    [InlineData("""{"percent":50,"device":"hdmi"}""")]
    [InlineData("""{}""")]
    public async Task AVolumeOutOfRangeOrOfTheWrongKind_IsRefusedAndNeverSet(string arguments)
    {
        var (executor, system) = Volume();

        var result = await executor.ExecuteAsync(Call("set_volume", arguments), Context);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(ToolErrors.InvalidArguments, Failure(result).Code);
        Assert.Contains("set_volume(percent: a whole number from 0 to 100)", result.OutputJson, StringComparison.Ordinal);
        Assert.Empty(system.Calls);
    }

    [Fact]
    public async Task MuteAndUnmuteChangeTheSwitch_AndSayWhatTheSpeakersAreNow()
    {
        var (executor, system) = Volume();

        var muted = await executor.ExecuteAsync(Call("mute"), Context);
        Assert.Equal("""{"volume":40,"muted":true,"message":"The sound is off."}""", muted.OutputJson);

        var unmuted = await executor.ExecuteAsync(Call("unmute"), Context);
        Assert.Equal("""{"volume":40,"muted":false,"message":"The sound is on."}""", unmuted.OutputJson);

        Assert.Equal(["mute", "unmute"], system.Calls);
    }

    [Theory]
    [InlineData("mute", """{"now":true}""")]
    [InlineData("unmute", """{"percent":50}""")]
    [InlineData("get_volume", """{"device":"x"}""")]
    public async Task TheToolsThatTakeNoArgumentsRefuseAny(string tool, string arguments)
    {
        var (executor, system) = Volume();

        var result = await executor.ExecuteAsync(Call(tool, arguments), Context);

        Assert.Equal(ToolErrors.InvalidArguments, Failure(result).Code);
        Assert.Empty(system.Calls);
    }

    [Fact]
    public async Task WithoutASoundOutput_EveryVolumeToolSaysSo()
    {
        var (executor, system) = Volume();
        system.Works = false;

        foreach (var (name, arguments) in new[] { ("get_volume", "{}"), ("set_volume", """{"percent":30}"""), ("mute", "{}"), ("unmute", "{}") })
        {
            var result = await executor.ExecuteAsync(Call(name, arguments), Context);

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Contains("no sound output", Failure(result).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ChangingTheVolumeNeedsTheUsersConfirmation_ReadingItDoesNot()
    {
        var (executor, system) = Volume(confirm: false);

        Assert.Equal(ToolResultStatus.Declined, (await executor.ExecuteAsync(Call("set_volume", """{"percent":30}"""), Context)).Status);
        Assert.Empty(system.Calls);

        // The sound's own switch is flipped without a question: the user asked for it in words and flips it back as easily.
        foreach (var name in new[] { "mute", "unmute" })
        {
            Assert.Equal(ToolResultStatus.Succeeded, (await executor.ExecuteAsync(Call(name), Context)).Status);
        }

        Assert.Equal(["mute", "unmute"], system.Calls);
        Assert.Equal(ToolResultStatus.Succeeded, (await executor.ExecuteAsync(Call("get_volume"), Context)).Status);
    }

    // ---- take_screenshot ----

    [Fact]
    public async Task AScreenshotIsTakenForTheConversation_AndTheModelIsToldHowBigAndWhatItCanDo()
    {
        var screenshots = new FakeScreenshots();
        var executor = Executor([new TakeScreenshotTool(screenshots, new ScreenToolsTests.FakeScreens())]);

        var result = await executor.ExecuteAsync(Call("take_screenshot"), Context);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Equal([Chat], screenshots.Asked);
        using var json = JsonDocument.Parse(result.OutputJson);
        Assert.True(json.RootElement.GetProperty("taken").GetBoolean());
        Assert.Equal((1920, 1080), (json.RootElement.GetProperty("width").GetInt32(), json.RootElement.GetProperty("height").GetInt32()));
        var message = json.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("read_screen_text", message, StringComparison.Ordinal);
        Assert.Contains("cannot see it in this answer", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutOcr_TheModelIsNotSentToATextReaderThatIsNotThere()
    {
        var executor = Executor([new TakeScreenshotTool(new FakeScreenshots(), new ScreenToolsTests.FakeScreens { Available = false })]);

        var result = await executor.ExecuteAsync(Call("take_screenshot"), Context);

        Assert.DoesNotContain("read_screen_text", Message(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScreenshotThatCouldNotBeTaken_IsAFailureAndNoPictureIsMentioned()
    {
        var screenshots = new FakeScreenshots { Outcome = new ScreenshotOutcome(ScreenshotStatus.Failed) };

        var result = await Executor([new TakeScreenshotTool(screenshots)]).ExecuteAsync(Call("take_screenshot"), Context);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("could not be captured", Failure(result).Message, StringComparison.Ordinal);
        Assert.DoesNotContain("taken", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScreenshotIsTakenOnlyWithScreenCaptureOn_AndWhenTheUserConfirms()
    {
        var screenshots = new FakeScreenshots();

        var off = await Executor([new TakeScreenshotTool(screenshots)], policy: new Allowing(PermissionCapability.Files)).ExecuteAsync(Call("take_screenshot"), Context);
        var declined = await Executor([new TakeScreenshotTool(screenshots)], confirm: false).ExecuteAsync(Call("take_screenshot"), Context);
        var withArguments = await Executor([new TakeScreenshotTool(screenshots)]).ExecuteAsync(Call("take_screenshot", """{"monitor":2}"""), Context);

        Assert.Equal(ToolErrors.PermissionOff, Failure(off).Code);
        Assert.Contains("Screen Capture is turned off", Failure(off).Message, StringComparison.Ordinal);
        Assert.Equal(ToolResultStatus.Declined, declined.Status);
        Assert.Equal(ToolErrors.InvalidArguments, Failure(withArguments).Code);
        Assert.Empty(screenshots.Asked);
    }

    // ---- The catalog ----

    private static IEnumerable<ITool> AllTools()
    {
        var system = new FakeSystem();
        var known = new ConversationFiles();
        var launcher = new RecordingFileLauncher();
        return
        [
            Calculator_Tool(),
            new OpenApplicationTool(new FakeApplications(), new RecordingApplicationLauncher()),
            new OpenFileTool(known, launcher),
            new RevealFileTool(known, launcher),
            new OpenFolderTool(system, known, launcher),
            VolumeTools.GetVolume(system),
            VolumeTools.SetVolume(system),
            VolumeTools.Mute(system),
            VolumeTools.Unmute(system),
            new TakeScreenshotTool(new FakeScreenshots()),
        ];
    }

    private static ITool Calculator_Tool() => Assistant.Tools.Calculator.CalculateTool.Create();

    [Fact]
    public void EveryToolMeetsTheRulesForARegisteredTool_AndNoneCanDestroyOrRunACommand()
    {
        var registry = new ToolRegistry(AllTools());

        Assert.Equal(
            [
                "calculate", "open_application", "open_file", "reveal_file", "open_folder", "get_volume", "set_volume", "mute", "unmute",
                "take_screenshot",
            ],
            registry.Tools.Select(tool => tool.Name));
        Assert.DoesNotContain(registry.Tools, tool => tool.RiskLevel == RiskLevel.Destructive);
        Assert.All(registry.Tools, tool => Assert.True(tool.EffectiveTimeout <= TimeSpan.FromSeconds(30)));
        Assert.All(registry.Tools, tool => Assert.NotEqual(PermissionCapability.DestructiveActions, tool.RequiredPermission));
    }

    [Fact]
    public void EachToolHasItsRiskLevelAndPermission_FixedInCode()
    {
        var registry = new ToolRegistry(AllTools());
        var expected = new Dictionary<string, (RiskLevel Risk, PermissionCapability? Permission)>
        {
            ["calculate"] = (RiskLevel.ReadOnly, null),
            ["get_volume"] = (RiskLevel.ReadOnly, null),
            ["open_application"] = (RiskLevel.SideEffect, null),
            ["set_volume"] = (RiskLevel.SideEffect, null),
            ["mute"] = (RiskLevel.SideEffect, null),
            ["unmute"] = (RiskLevel.SideEffect, null),
            ["open_file"] = (RiskLevel.SideEffect, PermissionCapability.Files),
            ["reveal_file"] = (RiskLevel.SideEffect, PermissionCapability.Files),
            ["open_folder"] = (RiskLevel.SideEffect, PermissionCapability.Files),
            ["take_screenshot"] = (RiskLevel.SideEffect, PermissionCapability.ScreenCapture),
        };

        Assert.Equal(expected.Count, registry.Tools.Count);
        foreach (var tool in registry.Tools)
        {
            Assert.Equal(expected[tool.Name], (tool.RiskLevel, tool.RequiredPermission));
        }
    }

    [Fact]
    public void EveryTypedToolForbidsArgumentsItDoesNotName_AndNoToolTakesSomethingToRun()
    {
        foreach (var tool in AllTools())
        {
            using var schema = JsonDocument.Parse(tool.Definition.InputSchemaJson);
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean(), tool.Definition.Name);

            foreach (var property in schema.RootElement.GetProperty("properties").EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, new[] { "command", "script", "program", "args", "arguments", "path", "executable", "url" });
            }
        }
    }
}
