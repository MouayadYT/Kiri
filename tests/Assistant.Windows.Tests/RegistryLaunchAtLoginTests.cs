using Assistant.Core.Startup;
using Assistant.Windows.Startup;
using Microsoft.Win32;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// Starting with Windows (PROJECT_SPEC §4.9, step 121) through the Run key, on throwaway keys of the current user so the real startup list is
/// never touched: the command, what Windows records when the user turns the entry off, and what the app may and may not overwrite.
/// </summary>
public sealed class RegistryLaunchAtLoginTests : IDisposable
{
    private const string Executable = @"C:\Program Files\Assistant\Assistant.UI.exe";

    private readonly string _root = @"Software\AssistantTests\" + Guid.NewGuid().ToString("N");

    private string RunKey => _root + @"\Run";

    private string ApprovedKey => _root + @"\Approved";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    private RegistryLaunchAtLogin Create(string executable = Executable) => new(executable, RunKey, ApprovedKey, "Assistant");

    private string? ReadEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Assistant") as string;
    }

    // What Windows writes when the user turns a startup app off (odd first byte) or on (even), then the time.
    private void WindowsRecords(byte firstByte)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        key.SetValue("Assistant", new byte[] { firstByte, 0, 0, 0, 0xAB, 0xCD, 0xEF, 1, 2, 3, 4, 5 }, RegistryValueKind.Binary);
    }

    private bool HasWindowsRecord()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return key?.GetValue("Assistant") is not null;
    }

    [Fact]
    public async Task EnablingAddsAnEntryThatStartsThisCopyInTheBackground()
    {
        var launch = Create();
        Assert.Equal(LaunchAtLoginState.Off, launch.GetState());

        Assert.True(await launch.EnableAsync());

        Assert.Equal(LaunchAtLoginState.On, launch.GetState());
        Assert.Equal("\"C:\\Program Files\\Assistant\\Assistant.UI.exe\" --background", ReadEntry());
        Assert.Equal(RegistryLaunchAtLogin.Command(Executable), ReadEntry());
    }

    [Fact]
    public async Task TheCommandQuotesAPathWithSpacesAndEndsWithTheSwitchTheAppReads()
    {
        var command = RegistryLaunchAtLogin.Command(Executable);

        Assert.StartsWith("\"" + Executable + "\" ", command, StringComparison.Ordinal);
        Assert.EndsWith(" " + LaunchOptions.BackgroundArgument, command, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DisablingRemovesTheEntry_AndDisablingWhatIsNotThereWorks()
    {
        var launch = Create();
        Assert.True(await launch.DisableAsync());

        await launch.EnableAsync();
        Assert.True(await launch.DisableAsync());

        Assert.Equal(LaunchAtLoginState.Off, launch.GetState());
        Assert.Null(ReadEntry());
    }

    [Theory]
    [InlineData(0x03, LaunchAtLoginState.TurnedOffInWindows)]
    [InlineData(0x07, LaunchAtLoginState.TurnedOffInWindows)]
    [InlineData(0x02, LaunchAtLoginState.On)]
    [InlineData(0x06, LaunchAtLoginState.On)]
    public async Task AnEntryTheUserTurnedOffInWindowsIsReportedAsThat(byte recorded, LaunchAtLoginState expected)
    {
        var launch = Create();
        await launch.EnableAsync();

        WindowsRecords(recorded);

        Assert.Equal(expected, launch.GetState());
    }

    [Fact]
    public async Task AskingForTheEntryAgainTurnsOnWhatTheUserTurnedOffInWindows()
    {
        var launch = Create();
        await launch.EnableAsync();
        WindowsRecords(0x03);
        Assert.Equal(LaunchAtLoginState.TurnedOffInWindows, launch.GetState());

        Assert.True(await launch.EnableAsync());

        Assert.Equal(LaunchAtLoginState.On, launch.GetState());
        Assert.False(HasWindowsRecord());
    }

    [Fact]
    public async Task RefreshingPointsTheEntryAtThisCopy_AndNeverTurnsOnWhatTheUserTurnedOff()
    {
        await Create(@"D:\Old\Assistant.UI.exe").EnableAsync();
        var moved = Create(@"E:\New\Assistant.UI.exe");
        WindowsRecords(0x03);

        Assert.True(await moved.RefreshAsync());

        Assert.Equal("\"E:\\New\\Assistant.UI.exe\" --background", ReadEntry());
        Assert.Equal(LaunchAtLoginState.TurnedOffInWindows, moved.GetState());
        Assert.True(HasWindowsRecord());
    }

    [Fact]
    public async Task RefreshingAddsTheEntryWhenItWentMissing_AndLeavesAGoodOneAlone()
    {
        var launch = Create();

        Assert.True(await launch.RefreshAsync());
        Assert.Equal(LaunchAtLoginState.On, launch.GetState());
        Assert.Equal(RegistryLaunchAtLogin.Command(Executable), ReadEntry());

        Assert.True(await launch.RefreshAsync());
        Assert.Equal(RegistryLaunchAtLogin.Command(Executable), ReadEntry());
    }

    [Fact]
    public async Task DisablingAlsoForgetsWhatWindowsRecordedAboutTheEntry()
    {
        var launch = Create();
        await launch.EnableAsync();
        WindowsRecords(0x03);

        await launch.DisableAsync();
        await launch.EnableAsync();

        Assert.Equal(LaunchAtLoginState.On, launch.GetState());
    }

    [Fact]
    public void TheRealKeysAreTheOnesWindowsListsAsStartupApps()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RegistryLaunchAtLogin.RunKeyPath);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", RegistryLaunchAtLogin.ApprovedKeyPath);
    }
}
