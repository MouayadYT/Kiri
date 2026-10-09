namespace Assistant.Core.Contracts;

/// <summary>
/// Opens a web page in the user's default browser (PROJECT_SPEC §3.4): the browser does the network access, not the Assistant. It is the
/// user's own action, such as clicking an image search result, never a tool call, and only an http or https address is ever opened.
/// </summary>
public interface IUrlLauncher
{
    /// <summary>Opens <paramref name="url"/> in the default browser.</summary>
    /// <returns><see langword="false"/> when it is not an absolute http or https address, or the browser could not be started.</returns>
    bool Open(Uri url);

    /// <summary>
    /// Opens <paramref name="url"/> in a private window (InPrivate, incognito) of a browser on this PC, which has none of the accounts the user's everyday browser is signed in to:
    /// for signing in to a service as another account.
    /// </summary>
    /// <returns><see langword="false"/> when it is not an absolute http or https address, no browser here can open a private window, or it could not be started.</returns>
    bool OpenPrivate(Uri url) => false;
}
