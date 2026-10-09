using Assistant.Core.Contracts;
using Assistant.Tools.Mcp.Auth;

namespace Assistant.UI.Integrations;

/// <summary>
/// Opens the page of a sign-in in the user's default browser (PROJECT_SPEC §4.8), through the same launcher that opens any link the user chose: only an http or https
/// address is ever handed to the shell. The page is the app's own; what the user does on it, the Assistant does not see.
/// </summary>
internal sealed class UrlLauncherOAuthBrowser(IUrlLauncher urls) : IOAuthBrowser
{
    /// <inheritdoc/>
    public Task<bool> OpenAsync(Uri address, CancellationToken cancellationToken = default) => Task.FromResult(urls.Open(address));

    /// <inheritdoc/>
    public Task<bool> OpenPrivateAsync(Uri address, CancellationToken cancellationToken = default) => Task.FromResult(urls.OpenPrivate(address));
}
