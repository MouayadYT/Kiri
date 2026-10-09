namespace Assistant.Tools.Integrations;

/// <summary>How a request for an external app was resolved (PROJECT_SPEC §4.8, step 105).</summary>
public enum IntegrationResolutionKind
{
    /// <summary>The request is not one to do something in an external app that can be told with certainty. Nothing is done.</summary>
    NotAnAppRequest = 0,

    /// <summary>A working integration for the app is installed and offers the capability: it is used as it is.</summary>
    UseInstalled = 1,

    /// <summary>An integration for the app is installed but cannot be used for this now (<see cref="IntegrationResolution.Problem"/> says why).</summary>
    InstalledNotUsable = 2,

    /// <summary>The request asks for something the Assistant refuses to do in a connected app (deleting).</summary>
    Refused = 3,

    /// <summary>No integration is installed, but the user has a server for the app set up in another program on this PC.</summary>
    AvailableLocally = 4,

    /// <summary>No integration for the app is installed or available on this PC: the Integration Finder is next.</summary>
    NotInstalled = 5,

    /// <summary>The installed integrations could not be read, so nothing can be said. Nothing is done.</summary>
    CouldNotCheck = 6,
}

/// <summary>Why an installed integration cannot be used for a request.</summary>
public enum InstalledProblem
{
    /// <summary>It is turned off.</summary>
    Disabled = 0,

    /// <summary>It needs a sign-in the Assistant does not have, or the one it has stopped working.</summary>
    NeedsSignIn = 1,

    /// <summary>It could not be reached or started.</summary>
    Unreachable = 2,

    /// <summary>It speaks no protocol version or transport this version understands.</summary>
    Incompatible = 3,

    /// <summary>Using it would send something off this PC, and Local Only mode is on.</summary>
    BlockedByLocalOnly = 4,

    /// <summary>A permission its tools need is off.</summary>
    PermissionOff = 5,

    /// <summary>It works, but none of its tools does what the request asks.</summary>
    LacksCapability = 6,

    /// <summary>The user turned off network access for it (Settings, Integrations), and it reaches out.</summary>
    NetworkOff = 7,

    /// <summary>The user took its account access away (Settings, Integrations), and it signs in.</summary>
    AccountAccessOff = 8,

    /// <summary>The user turned off reading for it, and what the request asks needs reading; or its tools are all turned off.</summary>
    AccessOff = 9,
}

/// <summary>
/// The answer to "can the Assistant do this in that app?" (PROJECT_SPEC §4.8, step 105): which integration to use, or why not, or that none is known and
/// the finder should look. It holds no tool schema, no secret and nothing of the request but the two words of the capability.
/// </summary>
public sealed record IntegrationResolution
{
    private IntegrationResolution(IntegrationResolutionKind kind) => Kind = kind;

    /// <summary>How the request was resolved.</summary>
    public IntegrationResolutionKind Kind { get; private init; }

    /// <summary>The app and the capability the request asked for; <see langword="null"/> when it asked for none.</summary>
    public IntegrationNeed? Need { get; private init; }

    /// <summary>The installed integration that is used, or that cannot be.</summary>
    public InstalledIntegration? Integration { get; private init; }

    /// <summary>For <see cref="IntegrationResolutionKind.UseInstalled"/>, the names of its tools that do what was asked, best first.</summary>
    public IReadOnlyList<string> ToolNames { get; private init; } = [];

    /// <summary>Whether the answer came from the tool names the registry kept, without starting or reaching the integration.</summary>
    public bool FromCache { get; private init; }

    /// <summary>For <see cref="IntegrationResolutionKind.InstalledNotUsable"/>, why.</summary>
    public InstalledProblem? Problem { get; private init; }

    /// <summary>For <see cref="IntegrationResolutionKind.AvailableLocally"/>, the servers found.</summary>
    public IReadOnlyList<AvailableIntegration> Available { get; private init; } = [];

    /// <summary>
    /// Whether the Integration Finder may look for an integration: none is installed or on this PC, or the one installed works but has nothing for
    /// this. An installed integration that only needs fixing (turned off, signed out) is never looked for again.
    /// </summary>
    public bool DiscoveryAllowed =>
        Kind == IntegrationResolutionKind.NotInstalled || Kind == IntegrationResolutionKind.InstalledNotUsable && Problem == InstalledProblem.LacksCapability;

    /// <summary>The request is not one for an external app.</summary>
    public static IntegrationResolution NotAnAppRequest { get; } = new(IntegrationResolutionKind.NotAnAppRequest);

    /// <summary>The installed integrations could not be read.</summary>
    public static IntegrationResolution CouldNotCheck { get; } = new(IntegrationResolutionKind.CouldNotCheck);

    internal static IntegrationResolution Use(IntegrationNeed need, InstalledIntegration integration, IReadOnlyList<string> toolNames, bool fromCache) =>
        new(IntegrationResolutionKind.UseInstalled) { Need = need, Integration = integration, ToolNames = toolNames, FromCache = fromCache };

    internal static IntegrationResolution NotUsable(IntegrationNeed need, InstalledIntegration integration, InstalledProblem problem) =>
        new(IntegrationResolutionKind.InstalledNotUsable) { Need = need, Integration = integration, Problem = problem };

    internal static IntegrationResolution Refuse(IntegrationNeed need) => new(IntegrationResolutionKind.Refused) { Need = need };

    internal static IntegrationResolution Local(IntegrationNeed need, IReadOnlyList<AvailableIntegration> available) =>
        new(IntegrationResolutionKind.AvailableLocally) { Need = need, Available = available };

    internal static IntegrationResolution Missing(IntegrationNeed need) => new(IntegrationResolutionKind.NotInstalled) { Need = need };
}
