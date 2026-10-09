using System.Text;

namespace Assistant.Core.Domain;

/// <summary>
/// Where a piece of context came from on the web: the page the user had selected text on, and the browser it was open in. It tells the
/// model what the text is part of; the page itself is never read, except, when the user chose "Selection + Nearby Context" in the
/// browser extension (step 88), the short stretch of its text just before and after the selection. Memory only, like the text it goes
/// with (PROJECT_SPEC §3.5).
/// </summary>
/// <param name="Title">The page's title, or empty when the browser did not give it.</param>
/// <param name="Url">The page's address, or empty when the browser did not give it.</param>
/// <param name="BrowserName">What the browser calls itself, such as "Microsoft Edge", or empty when it is not known.</param>
/// <param name="NearbyBefore">The cleaned page text just before the selection (<see cref="NearbyPageText"/>), or empty when none was sent.</param>
/// <param name="NearbyAfter">The cleaned page text just after the selection, or empty when none was sent.</param>
public sealed record WebPageOrigin(string Title, string Url, string BrowserName, string NearbyBefore = "", string NearbyAfter = "")
{
    /// <summary>Whether none of the title, the address and the browser's name is known, and no page text came with the selection.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Url) && string.IsNullOrWhiteSpace(BrowserName) && !HasNearbyContext;

    /// <summary>Whether page text around the selection came with it, beyond the selection itself.</summary>
    public bool HasNearbyContext => !string.IsNullOrWhiteSpace(NearbyBefore) || !string.IsNullOrWhiteSpace(NearbyAfter);

    /// <summary>How many characters of page text around the selection there are, both sides together.</summary>
    public int NearbyLength => NearbyBefore.Length + NearbyAfter.Length;

    // Keeps the page's title, address and text out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder) => false;
}
