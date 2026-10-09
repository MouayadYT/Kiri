namespace Assistant.Core.Ipc;

/// <summary>
/// What the browser extension captured when the user chose its entry in the right-click menu of selected text (PROJECT_SPEC §4.5,
/// §5.7): the selected text, the title and address of the page it is on, and the name of the browser. When the user has chosen
/// "Selection + Nearby Context" in the extension's options (step 88), also a short stretch of the page's text just before and just after
/// the selection, cleaned and bounded by the extension. Nothing else about the page is part of it.
/// </summary>
/// <param name="Text">The selected text; never empty or only blanks.</param>
/// <param name="IsTruncated">Whether more text was selected than <see cref="MaxTextLength"/>, so <paramref name="Text"/> ends before the selection does.</param>
/// <param name="PageTitle">The page's title, or empty when the browser did not give it.</param>
/// <param name="PageUrl">The page's address, or empty when the browser did not give it.</param>
/// <param name="BrowserName">What the browser calls itself, such as "Microsoft Edge", or empty when the extension could not tell.</param>
/// <param name="NearbyBefore">The page's text just before the selection, or empty when the extension did not send any (Selection Only, or nothing was there).</param>
/// <param name="NearbyAfter">The page's text just after the selection, or empty when the extension did not send any.</param>
public sealed record BrowserSelection(
    string Text, bool IsTruncated, string PageTitle, string PageUrl, string BrowserName = "", string NearbyBefore = "", string NearbyAfter = "")
{
    /// <summary>The most characters of selected text one request holds: what the app reads from other applications (PROJECT_SPEC §4.5), and what the extension cuts to.</summary>
    public const int MaxTextLength = 200_000;

    /// <summary>The longest page title, in UTF-16 characters.</summary>
    public const int MaxTitleLength = 500;

    /// <summary>The longest page address, in UTF-16 characters.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>The longest browser name, in UTF-16 characters.</summary>
    public const int MaxBrowserNameLength = 64;

    /// <summary>The most characters of page text one request holds on each side of the selection, in UTF-16 characters: what the extension cuts to.</summary>
    public const int MaxNearbyLength = 1_000;

    /// <summary>Whether the request carries page text around the selection (either side).</summary>
    public bool HasNearbyContext => !string.IsNullOrWhiteSpace(NearbyBefore) || !string.IsNullOrWhiteSpace(NearbyAfter);

    /// <summary>Whether the values are within the limits above.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Text) && Text.Length <= MaxTextLength
        && PageTitle is { Length: <= MaxTitleLength } && PageUrl is { Length: <= MaxUrlLength } && BrowserName is { Length: <= MaxBrowserNameLength }
        && NearbyBefore is { Length: <= MaxNearbyLength } && NearbyAfter is { Length: <= MaxNearbyLength };
}
