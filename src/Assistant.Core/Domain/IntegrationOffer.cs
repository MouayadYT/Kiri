using System.Text;

namespace Assistant.Core.Domain;

/// <summary>Whether an offer installs an integration or replaces an installed one with a newer version.</summary>
public enum IntegrationOfferKind
{
    /// <summary>Install an integration that is not installed.</summary>
    Install = 0,

    /// <summary>Replace an installed integration with a newer version of it.</summary>
    Update = 1,

    /// <summary>Connect an app whose server the Assistant knows the address of: record it, then sign in to it in the browser.</summary>
    Connect = 2,

    /// <summary>Sign in again to an app that is connected but whose sign-in has run out or was never finished.</summary>
    SignIn = 3,
}

/// <summary>How far the maker of an offered integration is known (PROJECT_SPEC §4.8, steps 107-108).</summary>
public enum IntegrationOfferMaker
{
    /// <summary>Nothing is known of who made it.</summary>
    Unknown = 0,

    /// <summary>Someone other than the app's maker, as far as can be told.</summary>
    Community = 1,

    /// <summary>It calls itself official, and nothing the Assistant can check confirms that.</summary>
    ClaimsOfficial = 2,

    /// <summary>Published under an account, scope or domain that the Assistant knows is the app maker's own.</summary>
    Official = 3,
}

/// <summary>
/// What the user is shown before an integration is downloaded or run (PROJECT_SPEC §4.8, step 108): the facts of a reviewed install
/// candidate, worded by the Assistant. Every text that came from the web is already cleaned (one line, cut to a length). It names the offer
/// by an id that only the Assistant's installer can act on, so nothing in here, and nothing a model says, can start an installation: only the
/// user's click on the panel that shows it does.
/// </summary>
public sealed record IntegrationOffer
{
    /// <summary>The id the installer knows the offer by. A click on Install hands it back; an unknown, used or expired id installs nothing.</summary>
    public required string OfferId { get; init; }

    /// <summary>Whether this installs an integration or updates one.</summary>
    public IntegrationOfferKind Kind { get; init; }

    /// <summary>The app, as the user knows it ("Todoist").</summary>
    public required string AppName { get; init; }

    /// <summary>What the integration is called by its publisher (a package or a registry name).</summary>
    public required string IntegrationName { get; init; }

    /// <summary>How far its maker is known.</summary>
    public IntegrationOfferMaker Maker { get; init; }

    /// <summary>Who made it, in words: "Official: published by Doist" or "Community-made by someone else".</summary>
    public required string MakerText { get; init; }

    /// <summary>What it lets the Assistant do for the request, in words: "Create a task".</summary>
    public required string Provides { get; init; }

    /// <summary>Where it comes from: the kind of package, its version, and its address.</summary>
    public required string Source { get; init; }

    /// <summary>The account, keys and access it may need, and what the Assistant cannot limit, one line each.</summary>
    public IReadOnlyList<string> Needs { get; init; } = [];

    /// <summary>What has to be downloaded or set up on this PC besides the integration, one line each.</summary>
    public IReadOnlyList<string> Requirements { get; init; } = [];

    /// <summary>What the Assistant could not check, and cautions from the review, one line each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>For an update, the version installed now.</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>The version that would be installed.</summary>
    public string? NewVersion { get; init; }

    // Keeps what the web said out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Kind = {Kind}");
        return true;
    }
}
