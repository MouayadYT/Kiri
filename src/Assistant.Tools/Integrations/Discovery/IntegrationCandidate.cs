using System.Text.Json.Serialization;

namespace Assistant.Tools.Integrations;

/// <summary>
/// How far a candidate integration can be trusted to be the app maker's own (PROJECT_SPEC §4.8, step 106). Only the first level is established by the
/// Assistant; the others are claims or the lack of one.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CandidateTrust>))]
public enum CandidateTrust
{
    /// <summary>Nothing is known of who made it.</summary>
    Unknown = 0,

    /// <summary>Made by someone who is not the app's maker, as far as can be told. Most integrations are.</summary>
    Community = 1,

    /// <summary>It calls itself official, but nothing the Assistant can check says the app's maker published it.</summary>
    ClaimsOfficial = 2,

    /// <summary>
    /// Published under a GitHub account, an npm scope or a domain that the Assistant knows belongs to the app's maker (<see cref="KnownApp.Owners"/>,
    /// <see cref="KnownApp.Domains"/>). The registries check that a publisher owns the name it publishes under, so this means the maker's own.
    /// </summary>
    VerifiedVendor = 3,
}

/// <summary>How a candidate integration is installed or reached.</summary>
public enum CandidateInstallMethod
{
    /// <summary>A server the maker hosts, reached over the internet.</summary>
    Remote = 0,

    /// <summary>A package from npm, run with Node.js.</summary>
    Npm = 1,

    /// <summary>A package from PyPI, run with Python.</summary>
    PyPi = 2,

    /// <summary>A container image, run with Docker.</summary>
    Container = 3,

    /// <summary>A NuGet package, run with .NET.</summary>
    NuGet = 4,

    /// <summary>An MCP bundle.</summary>
    Bundle = 5,

    /// <summary>Only the source code is published; it would have to be built.</summary>
    SourceOnly = 6,
}

/// <summary>What has to be on the PC to run a candidate.</summary>
public enum CandidateRuntime
{
    /// <summary>Nothing: it is a server that is reached over the internet.</summary>
    None = 0,

    /// <summary>Node.js.</summary>
    NodeJs = 1,

    /// <summary>Python.</summary>
    Python = 2,

    /// <summary>Docker.</summary>
    Container = 3,

    /// <summary>.NET.</summary>
    DotNet = 4,

    /// <summary>Not known.</summary>
    Unknown = 5,
}

/// <summary>What the local model made of whether a candidate does what was asked.</summary>
public enum CandidateAssessment
{
    /// <summary>The model was not asked, or gave no usable answer.</summary>
    NotJudged = 0,

    /// <summary>The model read the candidate and judged that it offers the capability.</summary>
    Supports = 1,

    /// <summary>The model read the candidate and judged that it does not clearly offer the capability.</summary>
    DoesNotSupport = 2,
}

/// <summary>One way to install a candidate.</summary>
/// <param name="Method">How.</param>
/// <param name="Identifier">The package, image or server address, cleaned.</param>
/// <param name="Version">The version, when the source gives one.</param>
/// <param name="Sha256">The SHA-256 of the file, in hexadecimal, when the source gives one (an MCP bundle's listing does).</param>
public sealed record CandidatePackage(CandidateInstallMethod Method, string Identifier, string? Version, string? Sha256 = null);

/// <summary>
/// An MCP integration that might do what a request asked, found by the Integration Finder (PROJECT_SPEC §4.8, step 106). It is a description and a link: nothing
/// in it has been downloaded, installed or run. Every text in it came from the web, so each is cleaned (one line, no control characters, cut to
/// a length) and every address is <c>https</c>; none is an instruction, and none is shown or passed to the model unchecked.
/// </summary>
public sealed record IntegrationCandidate
{
    /// <summary>What it is called (a registry name, a repository's full name or a package).</summary>
    public required string Name { get; init; }

    /// <summary>Where to read about it: its repository when it has one, otherwise its package or registry page. Always <c>https</c>.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>Its source code repository, when it has one.</summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>Who publishes it: the account, scope or domain, as its source gives it.</summary>
    public string? Publisher { get; init; }

    /// <summary>Whether it is the app maker's own.</summary>
    public CandidateTrust Trust { get; init; }

    /// <summary>Its licence as an SPDX identifier, when its source says.</summary>
    public string? License { get; init; }

    /// <summary>When its repository or package was last changed.</summary>
    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>Whether its repository is archived (no longer maintained).</summary>
    public bool Archived { get; init; }

    /// <summary>How many stars its repository has.</summary>
    public int? Stars { get; init; }

    /// <summary>What it says it is, cleaned and cut.</summary>
    public string? Description { get; init; }

    /// <summary>The ways it can be installed.</summary>
    public IReadOnlyList<CandidatePackage> Packages { get; init; } = [];

    /// <summary>The address of a server the maker hosts, when it has one. Always <c>https</c>.</summary>
    public string? RemoteUrl { get; init; }

    /// <summary>What has to be on the PC to run it.</summary>
    public CandidateRuntime Runtime { get; init; } = CandidateRuntime.Unknown;

    /// <summary>
    /// The names of the keys or tokens it asks for (environment variables, headers): what it would need to be given, which is the permission it
    /// asks for. Names only; never a value.
    /// </summary>
    public IReadOnlyList<string> RequiredSecrets { get; init; } = [];

    /// <summary>The names of the tools it lists, when its documentation lists them.</summary>
    public IReadOnlyList<string> ToolNames { get; init; } = [];

    /// <summary>Its version, as its source gives it.</summary>
    public string? Version { get; init; }

    /// <summary>The commit its source code was at when it was looked at, so that what was reviewed can be told from what changes later.</summary>
    public string? CommitSha { get; init; }

    /// <summary>How much its tools or description say that it does what was asked.</summary>
    public CapabilityEvidence Evidence { get; init; }

    /// <summary>What the local model made of it, when it was asked.</summary>
    public CandidateAssessment Assessment { get; init; }

    /// <summary>The sources that listed it (<c>mcp-registry</c>, <c>github</c>, <c>npm</c>, <c>pypi</c>).</summary>
    public IReadOnlyList<string> FoundIn { get; init; } = [];
}

/// <summary>How a search for an integration ended.</summary>
public enum DiscoveryStatus
{
    /// <summary>It found at least one candidate about the app.</summary>
    Found = 0,

    /// <summary>The places that list integrations answered, and none is about the app and fits.</summary>
    NothingPlausible = 1,

    /// <summary>Looking was not allowed, so nothing was sent anywhere (<see cref="IntegrationDiscoveryResult.Block"/> says why).</summary>
    Blocked = 2,

    /// <summary>The places that list integrations could not be reached.</summary>
    Failed = 3,
}

/// <summary>Why looking was not allowed.</summary>
public enum DiscoveryBlock
{
    /// <summary>It was allowed.</summary>
    None = 0,

    /// <summary>Local Only mode is on (Settings, Privacy).</summary>
    LocalOnly = 1,

    /// <summary>The External Web and Image Search permission is off.</summary>
    PermissionOff = 2,

    /// <summary>The External Web and Image Search permission is set to ask every time, and the user did not allow this look (or could not be asked).</summary>
    NotAllowedNow = 3,
}

/// <summary>What the Integration Finder found for a need (PROJECT_SPEC §4.8, step 106).</summary>
public sealed record IntegrationDiscoveryResult
{
    /// <summary>How the search ended.</summary>
    public DiscoveryStatus Status { get; init; }

    /// <summary>For <see cref="DiscoveryStatus.Blocked"/>, why.</summary>
    public DiscoveryBlock Block { get; init; }

    /// <summary>The candidates, best first; at most <see cref="IntegrationFinderOptions.MaxCandidates"/>.</summary>
    public IReadOnlyList<IntegrationCandidate> Candidates { get; init; } = [];

    /// <summary>The places that answered (<c>mcp-registry</c>, <c>github</c>, <c>npm</c>, <c>pypi</c>).</summary>
    public IReadOnlyList<string> SourcesAnswered { get; init; } = [];

    /// <summary>The places that could not be reached, were too slow or refused.</summary>
    public IReadOnlyList<string> SourcesFailed { get; init; } = [];

    /// <summary>Whether this is an earlier search's answer, kept for a while, and nothing was sent anywhere now.</summary>
    public bool FromCache { get; init; }

    /// <summary>When the search was made.</summary>
    public DateTimeOffset SearchedAt { get; init; }

    /// <summary>A search that was not allowed.</summary>
    public static IntegrationDiscoveryResult BlockedBy(DiscoveryBlock block, DateTimeOffset at) =>
        new() { Status = DiscoveryStatus.Blocked, Block = block, SearchedAt = at };
}
