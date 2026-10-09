using Microsoft.Win32;

namespace Assistant.SmokeTests.Support;

/// <summary>
/// The places in the user's own registry (HKCU) that the Assistant's installer and uninstaller read or write: the start-with-Windows entry, its Startup Apps
/// switch, the Settings &gt; Apps entry, File Explorer's Ask Assistant entry and the browsers' native-messaging hosts. A check that runs them takes a picture
/// of File Explorer's entry and the browsers' hosts first (they must be exactly as they were afterwards), and puts back what the installer changes on purpose:
/// the installer repoints an existing start-with-Windows entry at the copy it installs, which must not outlive the check.
/// </summary>
internal sealed class UserRegistry : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Assistant";
    private const string ValueName = "Assistant";

    private static readonly string[] Browsers =
    [
        @"Software\Microsoft\Edge\NativeMessagingHosts", @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts", @"Software\Chromium\NativeMessagingHosts",
    ];

    private readonly string _scratch;
    private readonly object? _run;
    private readonly RegistryValueKind _runKind;
    private readonly byte[]? _startupApproved;

    /// <param name="scratch">The check's folder: an Apps entry whose install folder is inside it was made by the check.</param>
    public UserRegistry(string scratch)
    {
        _scratch = scratch;
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        _run = run?.GetValue(ValueName);
        _runKind = _run is null ? RegistryValueKind.None : run!.GetValueKind(ValueName);
        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
        _startupApproved = approved?.GetValue(ValueName) as byte[];
        Integrations = DescribeIntegrations();
    }

    /// <summary>What File Explorer's entry and the browsers' hosts held when the check began: the check must leave them exactly so.</summary>
    public IReadOnlyList<string> Integrations { get; }

    /// <summary>Whether Windows already lists an installed Assistant, which an installer run by a check must then not overwrite.</summary>
    public static bool HasAppsEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key is not null;
    }

    /// <summary>The Settings &gt; Apps entry's install folder, or <see langword="null"/> when there is none.</summary>
    public static string? AppsEntryLocation()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>The start-with-Windows entry's command line now, or <see langword="null"/> when there is none.</summary>
    public static string? RunEntry()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(ValueName) as string;
    }

    /// <summary>One line for each File Explorer entry and browser host, in a fixed order.</summary>
    public static IReadOnlyList<string> DescribeIntegrations()
    {
        var lines = new List<string>();
        using (var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes\SystemFileAssociations"))
        {
            foreach (var extension in (classes?.GetSubKeyNames() ?? []).Order(StringComparer.Ordinal))
            {
                using var command = classes!.OpenSubKey($@"{extension}\shell\Assistant.AskAssistant\command");
                if (command is not null)
                {
                    lines.Add($@"{extension}\shell\Assistant.AskAssistant\command = {command.GetValue(null)}");
                }
            }
        }

        foreach (var browser in Browsers)
        {
            using var hosts = Registry.CurrentUser.OpenSubKey(browser);
            foreach (var host in (hosts?.GetSubKeyNames() ?? []).Order(StringComparer.Ordinal))
            {
                using var key = hosts!.OpenSubKey(host);
                lines.Add($@"{browser}\{host} = {key?.GetValue(null)}");
            }
        }

        return lines;
    }

    /// <summary>Puts back the start-with-Windows entry and its switch as they were, and removes an Apps entry the check made.</summary>
    public void Dispose()
    {
        if (AppsEntryLocation() is { } location && location.StartsWith(_scratch, StringComparison.OrdinalIgnoreCase))
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        }

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (_run is null)
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                run.SetValue(ValueName, _run, _runKind);
            }
        }

        using (var approved = Registry.CurrentUser.CreateSubKey(StartupApprovedKey))
        {
            if (_startupApproved is null)
            {
                approved.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                approved.SetValue(ValueName, _startupApproved, RegistryValueKind.Binary);
            }
        }
    }
}
