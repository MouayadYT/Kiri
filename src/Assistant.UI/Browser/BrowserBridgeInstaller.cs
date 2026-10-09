using Assistant.UI.Integrations;

namespace Assistant.UI.Browser;

/// <summary>Adds and removes the browser bridge's native-messaging host for the current user (PROJECT_SPEC §4.5, §4.9).</summary>
public interface IBrowserBridgeInstaller
{
    /// <summary>Registers the host shipped with this copy of the app with Edge, Chrome, Brave and Chromium. Returns whether it worked.</summary>
    Task<bool> InstallAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the registration. Returns whether it worked.</summary>
    Task<bool> RemoveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs <c>Assistant.BrowserBridge.exe register</c> or <c>unregister</c> from the app's folder: the host owns its registration
/// (PROJECT_SPEC §5.2), and the app only asks for it.
/// </summary>
internal sealed class BrowserBridgeInstaller(string directory) : IBrowserBridgeInstaller
{
    /// <summary>The host's executable, beside the app's.</summary>
    public const string ExecutableName = "Assistant.BrowserBridge.exe";

    /// <inheritdoc/>
    public Task<bool> InstallAsync(CancellationToken cancellationToken = default) =>
        EntryPointRunner.RunAsync(directory, ExecutableName, "register", cancellationToken);

    /// <inheritdoc/>
    public Task<bool> RemoveAsync(CancellationToken cancellationToken = default) =>
        EntryPointRunner.RunAsync(directory, ExecutableName, "unregister", cancellationToken);
}
