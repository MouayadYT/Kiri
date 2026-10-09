using Assistant.UI.Integrations;

namespace Assistant.UI.Explorer;

/// <summary>Adds and removes File Explorer's Ask Assistant entry for the current user (PROJECT_SPEC §4.4, §4.9).</summary>
public interface IExplorerMenuInstaller
{
    /// <summary>Adds the entry, pointing at the entry point shipped with this copy of the app. Returns whether it worked.</summary>
    Task<bool> InstallAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the entry. Returns whether it worked.</summary>
    Task<bool> RemoveAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a registered entry already exists; null when registration cannot be checked.</summary>
    Task<bool?> IsInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult<bool?>(null);

    /// <summary>
    /// Whether the entry is in File Explorer's first menu (Windows 11's short menu) and not only under Show more options. Windows allows that only for a registered package,
    /// and accepts the Assistant's only while Developer Mode is on, so this is what Settings checks to say so.
    /// </summary>
    Task<bool> IsInFirstMenuAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
}

/// <summary>
/// Runs <c>Assistant.ExplorerExtension.exe register</c> or <c>unregister</c> from the app's folder: the entry point owns its
/// registration (PROJECT_SPEC §5.2), and the app only asks for it.
/// </summary>
internal sealed class ExplorerMenuInstaller(string directory) : IExplorerMenuInstaller
{
    public Task<bool?> IsInstalledAsync(CancellationToken cancellationToken = default) => Task.Run<bool?>(() =>
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\SystemFileAssociations\.txt\shell\Assistant.AskAssistant\command");
            return key?.GetValue(null) is string command && command.Contains(ExecutableName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { return null; }
    }, cancellationToken);

    /// <summary>The entry point's executable, beside the app's.</summary>
    public const string ExecutableName = "Assistant.ExplorerExtension.exe";

    /// <inheritdoc/>
    public Task<bool> InstallAsync(CancellationToken cancellationToken = default) =>
        EntryPointRunner.RunAsync(directory, ExecutableName, "register", cancellationToken);

    /// <inheritdoc/>
    public Task<bool> RemoveAsync(CancellationToken cancellationToken = default) =>
        EntryPointRunner.RunAsync(directory, ExecutableName, "unregister", cancellationToken);

    /// <summary>The name of the package the entry point registers for the first menu (<c>TopMenuPackage.PackageName</c> in the entry point).</summary>
    public const string FirstMenuPackageName = "Assistant.ExplorerMenu";

    /// <inheritdoc/>
    public Task<bool> IsInFirstMenuAsync(CancellationToken cancellationToken = default) => Task.Run(
        () =>
        {
            try
            {
                return new global::Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty).Any(package => package.Id.Name == FirstMenuPackageName);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                return false;
            }
        },
        cancellationToken);
}
