namespace Assistant.Tools.Integrations;

/// <summary>Where an installation is.</summary>
public enum InstallStep
{
    /// <summary>Checking what is to be installed and that it may be.</summary>
    Preparing = 0,

    /// <summary>Downloading a runtime the integration needs (Node.js, Python).</summary>
    DownloadingRuntime = 1,

    /// <summary>Unpacking the runtime.</summary>
    UnpackingRuntime = 2,

    /// <summary>Downloading the integration.</summary>
    Downloading = 3,

    /// <summary>Unpacking the integration.</summary>
    Unpacking = 4,

    /// <summary>Fetching what the integration depends on.</summary>
    InstallingDependencies = 5,

    /// <summary>Checking that it starts and works.</summary>
    Checking = 6,

    /// <summary>Saving it as installed.</summary>
    Finishing = 7,
}

/// <summary>How far an installation has got, in words the user can read (never a text from the web).</summary>
/// <param name="Step">Where it is.</param>
/// <param name="Message">What is happening, as a short sentence.</param>
/// <param name="Fraction">How much of the step is done, from 0 to 1, when it can be told.</param>
public sealed record InstallProgress(InstallStep Step, string Message, double? Fraction = null);

/// <summary>How an installation ended.</summary>
public enum InstallStatus
{
    /// <summary>The integration is installed and recorded.</summary>
    Installed = 0,

    /// <summary>It was not installed: <see cref="InstallOutcome.Failure"/> says why. Nothing it downloaded is left.</summary>
    Failed = 1,

    /// <summary>The user cancelled. Nothing it downloaded is left.</summary>
    Cancelled = 2,
}

/// <summary>Why an installation did not happen.</summary>
public enum InstallFailure
{
    /// <summary>It did not fail.</summary>
    None = 0,

    /// <summary>The offer was never made, was used already, or has expired: nothing was approved.</summary>
    OfferExpired = 1,

    /// <summary>Local Only mode is on or the External Web and Image Search permission is off, and the download is from the web.</summary>
    WebLocked = 2,

    /// <summary>The candidate breaks a rule the installer checks again (<see cref="InstallRules"/>), or was changed after it was reviewed.</summary>
    NotAllowed = 3,

    /// <summary>An integration is installed under that id already.</summary>
    AlreadyInstalled = 4,

    /// <summary>Another installation is running.</summary>
    Busy = 5,

    /// <summary>A download failed, was too large, or the web could not be reached.</summary>
    DownloadFailed = 6,

    /// <summary>A download was not what the review checked: its hash is different.</summary>
    HashMismatch = 7,

    /// <summary>The runtime the integration needs could not be set up.</summary>
    RuntimeUnavailable = 8,

    /// <summary>The package could not be unpacked or its dependencies fetched.</summary>
    SetupFailed = 9,

    /// <summary>The package has no program the Assistant can start.</summary>
    EntryPointMissing = 10,

    /// <summary>The integration was installed, but it did not start or did not speak MCP, so it was taken away again.</summary>
    DidNotStart = 11,

    /// <summary>It started, but it has no tool for what was asked, so it was taken away again.</summary>
    LacksCapability = 12,

    /// <summary>The disk could not be written, or the list of installed integrations could not be saved.</summary>
    DiskFailed = 13,

    /// <summary>It is a kind of package the installer cannot set up.</summary>
    Unsupported = 14,

    /// <summary>The user did not finish signing in, or the app refused the sign-in.</summary>
    SignInFailed = 15,

    /// <summary>The app that is to be connected is a program on this PC, and it is not running or its server is off.</summary>
    AppNotRunning = 16,
}

/// <summary>What an installation did.</summary>
public sealed record InstallOutcome
{
    /// <summary>How it ended.</summary>
    public InstallStatus Status { get; init; }

    /// <summary>For a failure, why.</summary>
    public InstallFailure Failure { get; init; }

    /// <summary>What to tell the user, in words the Assistant wrote.</summary>
    public required string Message { get; init; }

    /// <summary>The integration as it was recorded, when it was installed.</summary>
    public InstalledIntegration? Integration { get; init; }

    /// <summary>The names of the tools it offered when it was checked, when it was checked.</summary>
    public IReadOnlyList<string> ToolNames { get; init; } = [];

    /// <summary>Whether it was installed.</summary>
    public bool IsInstalled => Status == InstallStatus.Installed;

    /// <summary>An outcome that failed.</summary>
    public static InstallOutcome Fail(InstallFailure failure, string message) => new() { Status = InstallStatus.Failed, Failure = failure, Message = message };

    /// <summary>An outcome that was cancelled.</summary>
    public static InstallOutcome Cancel() => new() { Status = InstallStatus.Cancelled, Message = "Cancelled. Nothing was installed." };
}

/// <summary>One thing an installation would download.</summary>
/// <param name="IsRuntime">Whether it is a runtime and not the integration.</param>
/// <param name="Name">What it is, in words (<c>Node.js 24</c>).</param>
/// <param name="Megabytes">About how large, when known.</param>
public sealed record PlannedDownload(bool IsRuntime, string Name, int? Megabytes);

/// <summary>What installing a candidate would download and set up, worked out without downloading or running anything (PROJECT_SPEC §4.8, step 108).</summary>
public sealed record InstallPlan
{
    /// <summary>What would be downloaded.</summary>
    public IReadOnlyList<PlannedDownload> Downloads { get; init; } = [];

    /// <summary>A runtime the integration needs that is set up already, so it is reused, by name.</summary>
    public IReadOnlyList<string> ReusedRuntimes { get; init; } = [];

    /// <summary>Whether the integration itself was downloaded before and is still there, so that nothing of it is downloaded again.</summary>
    public bool IntegrationAlreadyDownloaded { get; init; }

    /// <summary>Why it cannot be installed here, or <see langword="null"/>.</summary>
    public string? Blocker { get; init; }

    /// <summary>Whether it would need administrator rights. The installer never asks for them: everything goes in the Assistant's own folders.</summary>
    public bool NeedsAdministrator => false;
}

/// <summary>Installs integrations that passed the review (PROJECT_SPEC §4.8, step 108).</summary>
public interface IIntegrationInstaller
{
    /// <summary>What installing <paramref name="candidate"/> would download and set up. Nothing is downloaded or run.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<InstallPlan> PlanAsync(InstallCandidate candidate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs <paramref name="candidate"/> into the Assistant's own folders, after the user approved it: nothing here asks the user, so it is only called
    /// for an approved offer (<see cref="IIntegrationOffers"/>). It downloads only what the review pinned, checks it against the checksum, sets up the
    /// runtime it needs if that is missing, checks that the integration starts and offers what was asked, and records it. When any of that fails it
    /// leaves nothing behind. It never throws for a failure: the outcome says why.
    /// </summary>
    Task<InstallOutcome> InstallAsync(InstallCandidate candidate, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the installed integration <paramref name="integrationId"/> with the version <paramref name="candidate"/> pins, after the user approved the
    /// update. The new version is installed beside the old and checked first; only then is the record changed and the old version removed. When
    /// anything fails the installed version is left as it was.
    /// </summary>
    Task<InstallOutcome> UpdateAsync(
        InstallCandidate candidate, string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes what an interrupted installation left behind (the folder of what was being installed, the folder of an integration that is not installed,
    /// an old version that an update replaced). It does nothing while an installation runs. Returns how many folders it deleted.
    /// </summary>
    Task<int> CleanUpAsync(CancellationToken cancellationToken = default);
}

/// <summary>An installation step failed. The installer turns it into an <see cref="InstallOutcome"/>; it never carries a text from the web or a path.</summary>
public sealed class InstallException(InstallFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Why.</summary>
    public InstallFailure Failure { get; } = failure;
}

/// <summary>
/// What an installed integration remembers about how the Assistant installed it (PROJECT_SPEC §4.8, step 108): enough to tell what it is, to look for a newer
/// version, to update it, and to know which runtime it needs, and no more. It holds no secret.
/// </summary>
public sealed record ManagedInstall
{
    /// <summary>How it was installed.</summary>
    public InstallSourceKind Kind { get; init; }

    /// <summary>The package, the bundle's address, or the hosted server's address.</summary>
    public required string Package { get; init; }

    /// <summary>The checksum the download had, as <c>algorithm:hex</c>, when it was downloaded.</summary>
    public string? Hash { get; init; }

    /// <summary>The commit its source was at when it was reviewed, when known.</summary>
    public string? Commit { get; init; }

    /// <summary>Its source repository.</summary>
    public string? Repository { get; init; }

    /// <summary>Who published it.</summary>
    public string? Publisher { get; init; }

    /// <summary>Whether it was the app maker's own when it was installed.</summary>
    public CandidateTrust Trust { get; init; }

    /// <summary>The runtime it needs, when it needs one the Assistant sets up.</summary>
    public RuntimeKind? Runtime { get; init; }

    /// <summary>The version of that runtime it was set up with.</summary>
    public string? RuntimeVersion { get; init; }

    /// <summary>When it was installed.</summary>
    public DateTimeOffset InstalledAt { get; init; }

    /// <summary>The review's fingerprint of what was installed.</summary>
    public string? Fingerprint { get; init; }
}

/// <summary>The rules an <see cref="InstallCandidate"/> must satisfy for the installer to act on it, checked again by the installer whatever the review said.</summary>
public static class InstallRules
{
    /// <summary>Everything wrong with <paramref name="candidate"/>; empty when it breaks no rule.</summary>
    public static IReadOnlyList<string> Problems(InstallCandidate? candidate)
    {
        if (candidate is null)
        {
            return ["There is nothing to install."];
        }

        var problems = new List<string>();
        if (!IntegrationRules.IsValidId(candidate.Id))
        {
            problems.Add("The id is not valid.");
        }

        if (string.IsNullOrWhiteSpace(candidate.AppName) || candidate.AppName.Length > IntegrationRules.MaxNameLength || candidate.AppName.Any(char.IsControl))
        {
            problems.Add("The app's name is not valid.");
        }

        var source = candidate.Source;
        if (source is null || !Enum.IsDefined(source.Kind) || string.IsNullOrWhiteSpace(source.Identifier) || source.Identifier.Length > 300 || source.Identifier.Any(char.IsControl))
        {
            problems.Add("The source is not valid.");
            return problems;
        }

        if (!candidate.FingerprintMatches)
        {
            problems.Add("It was changed after it was reviewed.");
        }

        switch (source.Kind)
        {
            case InstallSourceKind.Remote:
                if (Mcp.McpEndpointRules.Problem(source.Identifier) is not null || !source.Identifier.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("The server's address is not a secure web address.");
                }

                break;
            case InstallSourceKind.Npm or InstallSourceKind.PyPi or InstallSourceKind.Bundle:
                if (!ReviewRules.IsExactVersion(source.Version))
                {
                    problems.Add("There is no exact version.");
                }

                if (source.Hash is not { IsValid: true })
                {
                    problems.Add("There is no checksum to check the download against.");
                }

                if (source.DownloadUrl is null || !Uri.TryCreate(source.DownloadUrl, UriKind.Absolute, out _))
                {
                    problems.Add("There is no address to download it from.");
                }

                if (source.Kind == InstallSourceKind.Npm && !ReviewRules.IsNpmName(source.Identifier)
                    || source.Kind == InstallSourceKind.PyPi && !ReviewRules.IsPyPiName(source.Identifier))
                {
                    problems.Add("The package is not named as a registry names packages.");
                }

                break;
        }

        if (candidate.RequiredSecrets.Count > 8 || candidate.RequiredSecrets.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 64))
        {
            problems.Add("The keys it asks for are not valid.");
        }

        return problems;
    }
}
