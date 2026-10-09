using System.Security;
using Assistant.Core.Startup;
using Microsoft.Win32;

namespace Assistant.Windows.Startup;

/// <summary>
/// Starts the Assistant at sign-in through the current user's <c>Run</c> key, the entry Windows lists under Settings > Apps > Startup and
/// Task Manager's Startup apps (PROJECT_SPEC §4.9). It is per user, so it needs no administrator rights, and the user can turn it off in
/// those places: Windows then records that in <c>StartupApproved\Run</c>, which this reads (<see cref="LaunchAtLoginState.TurnedOffInWindows"/>)
/// and clears when the user asks the Assistant to start at sign-in again. The entry runs the app with
/// <see cref="LaunchOptions.BackgroundArgument"/>.
/// </summary>
public sealed class RegistryLaunchAtLogin : ILaunchAtLogin
{
    /// <summary>The key, under the current user's, whose values Windows runs at sign-in.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The key where Windows records which of those the user turned off.</summary>
    public const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The name of the Assistant's value in both.</summary>
    public const string ValueName = "Assistant";

    private readonly string _command;
    private readonly string _runKeyPath;
    private readonly string _approvedKeyPath;
    private readonly string _valueName;

    /// <summary>Creates the service for the app at <paramref name="executablePath"/>.</summary>
    /// <param name="executablePath">The full path of the app's executable, which the entry starts.</param>
    /// <param name="runKeyPath">The key to put the entry in; a test's own.</param>
    /// <param name="approvedKeyPath">The key Windows records the user's choice in; a test's own.</param>
    /// <param name="valueName">The name of the value.</param>
    public RegistryLaunchAtLogin(
        string executablePath, string runKeyPath = RunKeyPath, string approvedKeyPath = ApprovedKeyPath, string valueName = ValueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _command = Command(executablePath);
        _runKeyPath = runKeyPath;
        _approvedKeyPath = approvedKeyPath;
        _valueName = valueName;
    }

    /// <summary>The command line the entry holds for the app at <paramref name="executablePath"/>: the path quoted, then the switch.</summary>
    public static string Command(string executablePath) => $"\"{executablePath}\" {LaunchOptions.BackgroundArgument}";

    /// <inheritdoc/>
    public LaunchAtLoginState GetState()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(_runKeyPath);
            if (run?.GetValue(_valueName) is not string)
            {
                return LaunchAtLoginState.Off;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(_approvedKeyPath);
            return approved?.GetValue(_valueName) is byte[] { Length: > 0 } flags && IsDisabled(flags)
                ? LaunchAtLoginState.TurnedOffInWindows
                : LaunchAtLoginState.On;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return LaunchAtLoginState.Off;
        }
    }

    /// <inheritdoc/>
    public Task<bool> EnableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(_runKeyPath))
            {
                run.SetValue(_valueName, _command, RegistryValueKind.String);
            }

            // What the user turned off in Windows earlier is turned on again: they ask for it now.
            using var approved = Registry.CurrentUser.OpenSubKey(_approvedKeyPath, writable: true);
            approved?.DeleteValue(_valueName, throwOnMissingValue: false);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc/>
    public Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(_runKeyPath);

            // What Windows has recorded about the entry is not touched: if the user turned it off there, it stays off.
            if (run.GetValue(_valueName) is not string current || !string.Equals(current, _command, StringComparison.Ordinal))
            {
                run.SetValue(_valueName, _command, RegistryValueKind.String);
            }

            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DisableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true))
            {
                run?.DeleteValue(_valueName, throwOnMissingValue: false);
            }

            // Windows' note about the entry goes with it, so a later one starts clean.
            using var approved = Registry.CurrentUser.OpenSubKey(_approvedKeyPath, writable: true);
            approved?.DeleteValue(_valueName, throwOnMissingValue: false);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(false);
        }
    }

    // Windows writes twelve bytes: the first says enabled (an even number, 2 or 6) or disabled (odd, 3 or 7), the rest is when.
    private static bool IsDisabled(byte[] flags) => (flags[0] & 1) == 1;
}
