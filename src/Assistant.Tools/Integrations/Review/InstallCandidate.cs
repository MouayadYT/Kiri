using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Assistant.Core.Domain;

namespace Assistant.Tools.Integrations;

/// <summary>How an integration that passed the review would be put on this PC (PROJECT_SPEC §4.8, step 107).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InstallSourceKind>))]
public enum InstallSourceKind
{
    /// <summary>A package from npm, run with a Node.js the Assistant sets up for it.</summary>
    Npm = 0,

    /// <summary>A package from PyPI, run with a Python the Assistant sets up for it.</summary>
    PyPi = 1,

    /// <summary>An MCP bundle (<c>.mcpb</c>): a zip with a manifest and the program, which needs nothing else or a Node.js.</summary>
    Bundle = 2,

    /// <summary>A server the maker hosts: nothing is downloaded or run on this PC, the Assistant only records where it is.</summary>
    Remote = 3,
}

/// <summary>What a download must hash to, as hexadecimal in lower case.</summary>
/// <param name="Algorithm"><c>sha256</c> or <c>sha512</c>.</param>
/// <param name="Hex">The hash, as 64 (SHA-256) or 128 (SHA-512) hexadecimal digits.</param>
public sealed record ContentHash(string Algorithm, string Hex)
{
    /// <summary>Whether the algorithm is one the Assistant checks and the digits are the right number of hexadecimal digits for it.</summary>
    public bool IsValid =>
        (Algorithm == "sha256" && Hex.Length == 64 || Algorithm == "sha512" && Hex.Length == 128)
        && Hex.All(character => char.IsAsciiHexDigitLower(character) || char.IsAsciiDigit(character));

    /// <summary>The hash of <paramref name="bytes"/> by the same algorithm, for comparing.</summary>
    public string Compute(ReadOnlySpan<byte> bytes) => Algorithm == "sha512"
        ? Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant()
        : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The hash as <c>algorithm:hex</c>, for showing.</summary>
    public override string ToString() => Algorithm + ":" + Hex;
}

/// <summary>
/// What exactly would be installed, pinned by the review (PROJECT_SPEC §4.8, step 107): an exact version and the hash the download must have, so
/// that what the user approves, and what the review looked at, is what is installed and not whatever the registry holds later.
/// </summary>
public sealed record InstallSource
{
    /// <summary>How it is installed.</summary>
    public InstallSourceKind Kind { get; init; }

    /// <summary>The npm or PyPI package, or for a remote server its address, or for a bundle the address it is downloaded from.</summary>
    public required string Identifier { get; init; }

    /// <summary>The exact version (never a range, a tag or <c>latest</c>); <see langword="null"/> for a remote server, which has none the Assistant can pin.</summary>
    public string? Version { get; init; }

    /// <summary>Where the package or bundle is downloaded from. Always <c>https</c>.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>What the download must hash to. Required for everything that is downloaded.</summary>
    public ContentHash? Hash { get; init; }

    /// <summary>How large the download is, when the source says.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>For an npm package, the name of the program it provides (its <c>bin</c>), when it provides one; for PyPI, the script it is expected to provide.</summary>
    public string? EntryName { get; init; }

    /// <summary>Whether the package has scripts that npm would run when installing it. The Assistant never runs them.</summary>
    public bool HasInstallScripts { get; init; }

    /// <summary>How many packages it depends on directly. They are fetched when it is installed and are not reviewed one by one.</summary>
    public int? DependencyCount { get; init; }

    /// <summary>What version of Node.js or Python the package says it needs (<c>&gt;=18</c>), as published.</summary>
    public string? RuntimeConstraint { get; init; }
}

/// <summary>How a licence reads to the Assistant.</summary>
public enum LicenseStatus
{
    /// <summary>The source gives none.</summary>
    Missing = 0,

    /// <summary>One is given, but it is not one the Assistant recognizes as an open-source licence (or it says that none is granted).</summary>
    Unrecognized = 1,

    /// <summary>A well-known open-source licence.</summary>
    Open = 2,
}

/// <summary>How recently the project was changed.</summary>
public enum UpkeepStatus
{
    /// <summary>Not known.</summary>
    Unknown = 0,

    /// <summary>Within a year.</summary>
    Recent = 1,

    /// <summary>Between one and two years ago.</summary>
    Aging = 2,

    /// <summary>More than two years ago.</summary>
    Stale = 3,
}

/// <summary>What shows that something is an MCP server.</summary>
[Flags]
public enum McpServerEvidence
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>It is listed in the official MCP registry, which lists servers.</summary>
    ListedInRegistry = 1,

    /// <summary>Its documentation lists tools.</summary>
    ToolsListed = 2,

    /// <summary>Its name or description says it is an MCP server.</summary>
    Described = 4,
}

/// <summary>How serious a thing the review found is.</summary>
public enum ReviewSeverity
{
    /// <summary>A fact worth showing.</summary>
    Info = 0,

    /// <summary>Something the Assistant could not check, or that the user should weigh. It does not stop the install.</summary>
    Caution = 1,

    /// <summary>A reason the candidate must not be offered.</summary>
    Blocker = 2,
}

/// <summary>What a review finding is about. Fixed codes, so that nothing the web said has to be repeated to say what was found.</summary>
public enum ReviewCode
{
    /// <summary>The candidate's own description of itself is malformed.</summary>
    Malformed = 0,

    /// <summary>Nothing shows that it is an MCP server.</summary>
    NotAnMcpServer = 1,

    /// <summary>It lists its tools and none does what was asked.</summary>
    CapabilityMissing = 2,

    /// <summary>Nothing says whether it can do what was asked, apart from a general description.</summary>
    CapabilityUnconfirmed = 3,

    /// <summary>Only the capability's description says it can; its tool list was not read.</summary>
    CapabilityDescribed = 4,

    /// <summary>The way it is distributed is not one the Assistant installs (a container, a .NET package, source code).</summary>
    UnsupportedInstall = 5,

    /// <summary>The package is named in a way that is not a registry name (an address, a path, a git reference).</summary>
    UnsafePackageSource = 6,

    /// <summary>There is no exact version to pin.</summary>
    UnpinnedVersion = 7,

    /// <summary>There is no checksum to verify the download against.</summary>
    NoIntegrityHash = 8,

    /// <summary>The package has no program to run.</summary>
    NoEntryPoint = 9,

    /// <summary>The package needs a newer runtime than the one the Assistant sets up.</summary>
    RuntimeIncompatible = 10,

    /// <summary>The package's metadata could not be read, or the web could not be reached.</summary>
    CouldNotCheck = 11,

    /// <summary>Checking needs the web, and Local Only mode is on or the External Web and Image Search permission is off.</summary>
    WebLocked = 12,

    /// <summary>The project is archived: its maker no longer maintains it.</summary>
    Archived = 13,

    /// <summary>The version was withdrawn or marked deprecated by its author.</summary>
    Withdrawn = 14,

    /// <summary>Who made it is not known, or it only claims to be official.</summary>
    PublisherUnverified = 15,

    /// <summary>No licence is given, or it is not a recognised open-source licence.</summary>
    LicenseUnclear = 16,

    /// <summary>It has not been changed for a long time, or when is not known.</summary>
    Maintenance = 17,

    /// <summary>It has install scripts, which the Assistant does not run.</summary>
    InstallScripts = 18,

    /// <summary>What it depends on is chosen when it is installed and is not reviewed.</summary>
    DependenciesUnpinned = 19,

    /// <summary>The commit its package was built from is not known.</summary>
    CommitUnknown = 20,

    /// <summary>It asks for keys or an account.</summary>
    SecretsNeeded = 21,

    /// <summary>The Assistant cannot limit what a program it runs does.</summary>
    UnsandboxedProgram = 22,

    /// <summary>The server is hosted by someone else, and what is asked of it is sent to it.</summary>
    HostedElsewhere = 23,

    /// <summary>It may need a sign-in that this version cannot complete.</summary>
    SignInUnknown = 24,

    /// <summary>A fact the review reports as it is.</summary>
    Fact = 25,
}

/// <summary>One thing the review found.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Code">What it is about.</param>
/// <param name="Text">What it is, in words the Assistant wrote (never a text from the web).</param>
public sealed record ReviewFinding(ReviewSeverity Severity, ReviewCode Code, string Text);

/// <summary>
/// An integration that passed the review and may be offered to the user (PROJECT_SPEC §4.8, step 107): everything the approval panel shows and
/// everything the installer needs, pinned and checked, and nothing downloaded, installed or run yet. It is the object the installer consumes
/// (<see cref="IIntegrationInstaller"/>), which checks it again and never believes it blindly. Where something could not be checked it says
/// so in <see cref="Notes"/> and leaves the member empty or unknown; it never claims more than was verified.
/// </summary>
public sealed record InstallCandidate
{
    /// <summary>The id the integration would be installed under (<see cref="IntegrationRules.IsValidId"/>).</summary>
    public required string Id { get; init; }

    /// <summary>The app, as the user knows it.</summary>
    public required string AppName { get; init; }

    /// <summary>What the integration is called (a package or a registry name), cleaned.</summary>
    public required string Name { get; init; }

    /// <summary>What exactly would be installed.</summary>
    public required InstallSource Source { get; init; }

    /// <summary>Whether it is the app maker's own, as far as the Assistant could establish (<see cref="CandidateTrust"/>).</summary>
    public CandidateTrust Trust { get; init; }

    /// <summary>Who publishes it, as its source says.</summary>
    public string? Publisher { get; init; }

    /// <summary>Where to read about it. Always <c>https</c>.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Its source repository, when it has one. Always <c>https</c>.</summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>What it says it is, cleaned and cut.</summary>
    public string? Description { get; init; }

    /// <summary>Its licence as its source gives it.</summary>
    public string? License { get; init; }

    /// <summary>How its licence reads.</summary>
    public LicenseStatus LicenseStatus { get; init; }

    /// <summary>When it was last changed, when its source says.</summary>
    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>How recently that was.</summary>
    public UpkeepStatus Activity { get; init; }

    /// <summary>What has to be on this PC to run it, which the Assistant sets up itself when it is missing.</summary>
    public CandidateRuntime Runtime { get; init; }

    /// <summary>The names of the keys or tokens it asks for. Names only; never a value.</summary>
    public IReadOnlyList<string> RequiredSecrets { get; init; } = [];

    /// <summary>How it signs in, as far as is known.</summary>
    public IntegrationAuthKind Authentication { get; init; }

    /// <summary>
    /// Whether using it may send what is asked of it off this PC: always for a hosted server, and for a program from the internet unless it is known
    /// not to reach out, which the Assistant cannot check. While Local Only mode is on an integration that may is not used.
    /// </summary>
    public bool LeavesThisPc { get; init; } = true;

    /// <summary>What the request wanted done; <see langword="null"/> for a newer version of an integration that is installed already.</summary>
    public IntegrationCapability? Capability { get; init; }

    /// <summary>How much its tools or description say that it does that.</summary>
    public CapabilityEvidence Evidence { get; init; }

    /// <summary>The tools it lists that do what was asked, best first.</summary>
    public IReadOnlyList<string> MatchedTools { get; init; } = [];

    /// <summary>What shows that it is an MCP server.</summary>
    public McpServerEvidence McpEvidence { get; init; }

    /// <summary>The commit its source was at when it was looked at, when known.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Where it was found (<c>mcp-registry</c>, <c>github</c>, <c>npm</c>, <c>pypi</c>).</summary>
    public IReadOnlyList<string> FoundIn { get; init; } = [];

    /// <summary>What the review could not check, and what the user should weigh: the incomplete parts of what is known.</summary>
    public IReadOnlyList<ReviewFinding> Notes { get; init; } = [];

    /// <summary>When it was reviewed.</summary>
    public DateTimeOffset ReviewedAt { get; init; }

    /// <summary>A hash of what was reviewed (the identity and the pinned source), so that a change after the review is seen.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The exact version that would be installed, or <see langword="null"/> for a hosted server.</summary>
    public string? Version => Source.Version;

    /// <summary>Whether it runs on the maker's server and nothing is installed on this PC.</summary>
    public bool IsRemote => Source.Kind == InstallSourceKind.Remote;

    /// <summary>Works out the fingerprint of <paramref name="id"/>, <paramref name="appName"/> and <paramref name="source"/>.</summary>
    public static string FingerprintOf(string id, string appName, InstallSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var text = string.Join(
            '\n',
            id,
            appName,
            source.Kind.ToString(),
            source.Identifier,
            source.Version ?? string.Empty,
            source.DownloadUrl ?? string.Empty,
            source.Hash?.ToString() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>Whether <see cref="Fingerprint"/> is the one this candidate's identity and source work out to.</summary>
    public bool FingerprintMatches => string.Equals(Fingerprint, FingerprintOf(Id, AppName, Source), StringComparison.Ordinal);

    // Keeps the web's words out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Id = {Id}, Kind = {Source.Kind}");
        return true;
    }
}

/// <summary>Whether a candidate may be offered.</summary>
public enum ReviewVerdict
{
    /// <summary>It passed: <see cref="CandidateReview.Candidate"/> is set.</summary>
    Accepted = 0,

    /// <summary>It did not: <see cref="CandidateReview.Blockers"/> say why.</summary>
    Rejected = 1,
}

/// <summary>The outcome of reviewing one candidate (PROJECT_SPEC §4.8, step 107).</summary>
public sealed record CandidateReview
{
    /// <summary>Whether it may be offered.</summary>
    public ReviewVerdict Verdict { get; init; }

    /// <summary>For an accepted candidate, what the installer consumes; otherwise <see langword="null"/>.</summary>
    public InstallCandidate? Candidate { get; init; }

    /// <summary>Everything the review found, blockers first.</summary>
    public IReadOnlyList<ReviewFinding> Findings { get; init; } = [];

    /// <summary>Whether it passed.</summary>
    public bool IsAccepted => Verdict == ReviewVerdict.Accepted;

    /// <summary>The reasons it did not pass.</summary>
    public IEnumerable<ReviewFinding> Blockers => Findings.Where(finding => finding.Severity == ReviewSeverity.Blocker);

    /// <summary>A review that rejected the candidate for <paramref name="blockers"/>.</summary>
    public static CandidateReview Reject(IEnumerable<ReviewFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return new CandidateReview { Verdict = ReviewVerdict.Rejected, Findings = Order(findings) };
    }

    /// <summary>A review that accepted <paramref name="candidate"/>.</summary>
    public static CandidateReview Accept(InstallCandidate candidate, IEnumerable<ReviewFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(findings);
        return new CandidateReview { Verdict = ReviewVerdict.Accepted, Candidate = candidate, Findings = Order(findings) };
    }

    private static List<ReviewFinding> Order(IEnumerable<ReviewFinding> findings) =>
        [.. findings.OrderByDescending(finding => finding.Severity)];
}
