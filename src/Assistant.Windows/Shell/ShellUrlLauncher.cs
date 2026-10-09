using System.ComponentModel;
using System.Diagnostics;
using Assistant.Core.Contracts;

namespace Assistant.Windows.Shell;

/// <summary>
/// Opens a web page the user chose in their default browser (PROJECT_SPEC §3.4), through the shell, as clicking a link anywhere would.
/// Only an absolute http or https address is handed on, so nothing else (a file, a program, a script) can be started by an address a
/// search provider returned. The address is never logged, and a failure is only <see langword="false"/>.
/// </summary>
public sealed class ShellUrlLauncher : IUrlLauncher
{
    private readonly Func<ProcessStartInfo, bool> _start;
    private readonly Func<PrivateBrowser?> _privateBrowser;

    /// <summary>Creates the launcher over the real shell.</summary>
    public ShellUrlLauncher()
        : this(StartWithShell, () => PrivateBrowserFinder.Find(new WindowsBrowserRegistry()))
    {
    }

    internal ShellUrlLauncher(Func<ProcessStartInfo, bool> start, Func<PrivateBrowser?>? privateBrowser = null)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _privateBrowser = privateBrowser ?? (() => null);
    }

    /// <inheritdoc/>
    public bool Open(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host))
        {
            return false;
        }

        try
        {
            // AbsoluteUri is the escaped form, so it can hold no space or quote that the shell could take for another argument.
            return _start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public bool OpenPrivate(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host))
        {
            return false;
        }

        try
        {
            if (_privateBrowser() is not { } browser)
            {
                return false;
            }

            // The program is started directly, not through the shell, and the address is its own argument (escaped, so it cannot be taken for a switch or for two arguments).
            var info = new ProcessStartInfo(browser.Executable) { UseShellExecute = false };
            info.ArgumentList.Add(browser.Switch);
            info.ArgumentList.Add(url.AbsoluteUri);
            return _start(info);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool StartWithShell(ProcessStartInfo info)
    {
        // A shell verb that reuses a running browser gives no process back; that is still a success.
        using var process = Process.Start(info);
        return true;
    }
}
