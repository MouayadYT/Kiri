using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>How the candidate reviewer is bounded.</summary>
public sealed record CandidateReviewerOptions
{
    /// <summary>The most time reading one package's facts from its registry is given.</summary>
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>The most time reviewing a whole list of candidates takes.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How many candidates are reviewed at once.</summary>
    public int MaxParallel { get; init; } = 3;
}

/// <summary>The trust review between the Integration Finder and the installer (PROJECT_SPEC §4.8, step 107).</summary>
public interface ICandidateReviewer
{
    /// <summary>
    /// Reviews <paramref name="candidate"/> for the <paramref name="need"/>: whether it is well formed, is an MCP server, offers what was asked and can be
    /// installed safely, and what could not be checked. An accepted candidate comes with an <see cref="InstallCandidate"/>, pinned to an exact version and
    /// a checksum. Nothing is downloaded, installed or run; the only network use is reading the package's facts from its registry, which needs Local Only
    /// mode off and the External Web and Image Search permission on (a candidate that needs that and cannot have it is rejected, with the reason).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<CandidateReview> ReviewAsync(IntegrationCandidate candidate, IntegrationNeed need, CancellationToken cancellationToken = default);

    /// <summary>Reviews each of <paramref name="candidates"/>, a few at once, within a time limit; the reviews are in the order of the candidates.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<CandidateReview>> ReviewAllAsync(
        IReadOnlyList<IntegrationCandidate> candidates, IntegrationNeed need, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reviews a newer version of the installed integration <paramref name="integrationId"/> (<paramref name="appName"/> is its app): everything is checked as
    /// for an install, except that no particular capability is required, since what the integration is for was decided when it was installed. The
    /// candidate that comes out keeps the installed integration's id.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<CandidateReview> ReviewUpdateAsync(IntegrationCandidate candidate, string appName, string integrationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reviews the candidates the Integration Finder found before any of them can be offered for installation (PROJECT_SPEC §4.8, step 107). The finder never
/// chooses and runs arbitrary code from the internet, and neither does this: it rejects a candidate that is malformed, is not shown to be an MCP
/// server, lists tools that cannot do what was asked, is only source code, a container or a package named by an address, is archived, or whose package
/// has no checksum, no program to run, or needs a newer runtime than the one the Assistant sets up. What it accepts is described in an
/// <see cref="InstallCandidate"/>, pinned to an exact version and the checksum the registry publishes, with a note for everything that could not be
/// checked: it states no safety guarantee, and says what is not known. Findings use the Assistant's own words; nothing the web said is repeated in one.
/// Logs say counts only.
/// </summary>
internal sealed partial class CandidateReviewer : ICandidateReviewer
{
    private readonly IPackageMetadata _metadata;
    private readonly IInstalledIntegrationRegistry? _registry;
    private readonly ISettingsService _settings;
    private readonly IPermissionPolicy _permissions;
    private readonly TimeProvider _clock;
    private readonly CandidateReviewerOptions _options;
    private readonly ILogger<CandidateReviewer> _logger;

    /// <summary>Creates the reviewer.</summary>
    public CandidateReviewer(
        IPackageMetadata metadata,
        IInstalledIntegrationRegistry? registry,
        ISettingsService settings,
        IPermissionPolicy permissions,
        TimeProvider clock,
        CandidateReviewerOptions options,
        ILogger<CandidateReviewer> logger)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _metadata = metadata;
        _registry = registry;
        _settings = settings;
        _permissions = permissions;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<CandidateReview> ReviewAsync(IntegrationCandidate candidate, IntegrationNeed need, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(need);

        // A need that names no app (step 116) has no name to install under: the integration is shown and kept under the name it has itself, made readable.
        if (need.IsForAnyApp)
        {
            var name = ReviewRules.AppNameFrom(candidate.Name, need.AppName);
            return ReviewCoreAsync(candidate, name, AppIdentity.KeyOf(name), keepId: null, need.Capability, cancellationToken);
        }

        return ReviewCoreAsync(candidate, need.AppName, need.AppKey, keepId: null, need.Capability, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<CandidateReview> ReviewUpdateAsync(IntegrationCandidate candidate, string appName, string integrationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        if (!IntegrationRules.IsValidId(integrationId))
        {
            throw new ArgumentException("Not an integration id.", nameof(integrationId));
        }

        return ReviewCoreAsync(candidate, appName, integrationId, integrationId, capability: null, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CandidateReview>> ReviewAllAsync(
        IReadOnlyList<IntegrationCandidate> candidates, IntegrationNeed need, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(need);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(_options.TotalTimeout);
        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallel));
        var reviews = await Task.WhenAll(candidates.Select(async candidate =>
        {
            var entered = false;
            try
            {
                await gate.WaitAsync(total.Token).ConfigureAwait(false);
                entered = true;
                return await ReviewAsync(candidate, need, total.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The whole review ran out of time: this one was not finished.
                return CandidateReview.Reject([ReviewRules.Block(ReviewCode.CouldNotCheck, "I ran out of time before I could check it.")]);
            }
            finally
            {
                if (entered)
                {
                    gate.Release();
                }
            }
        })).ConfigureAwait(false);
        LogReviewed(_logger, reviews.Length, reviews.Count(review => review.IsAccepted));
        return reviews;
    }

    private async Task<CandidateReview> ReviewCoreAsync(
        IntegrationCandidate candidate, string appName, string appKey, string? keepId, IntegrationCapability? capability, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var findings = new List<ReviewFinding>();
        if (!ReviewRules.WellFormed(candidate, findings))
        {
            return CandidateReview.Reject(findings);
        }

        var mcp = ReviewRules.McpEvidenceOf(candidate);
        if (mcp == McpServerEvidence.None)
        {
            findings.Add(ReviewRules.Block(ReviewCode.NotAnMcpServer, "Nothing it says shows that it is an MCP server."));
        }

        IReadOnlyList<string> matched = [];
        if (capability is not null)
        {
            matched = ReviewRules.CheckCapability(candidate, capability, findings);
        }

        if (candidate.Archived)
        {
            findings.Add(ReviewRules.Block(ReviewCode.Archived, "Its project is archived, which means its maker no longer maintains it."));
        }

        var options = ReviewRules.InstallOptions(candidate, findings);
        if (findings.Any(finding => finding.Severity == ReviewSeverity.Blocker))
        {
            return CandidateReview.Reject(findings);
        }

        // The ways of installing it are tried in order; the first that can be pinned and checked is the one.
        Resolved? resolved = null;
        var failures = new List<ReviewFinding>();
        foreach (var option in options)
        {
            var attempt = new List<ReviewFinding>();
            resolved = await ResolveAsync(candidate, option, attempt, cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                findings.AddRange(attempt);
                break;
            }

            failures.AddRange(attempt);
        }

        if (resolved is null)
        {
            findings.AddRange(failures.DistinctBy(failure => failure.Text));
            return CandidateReview.Reject(findings);
        }

        var license = candidate.License ?? resolved.License;
        var licenseStatus = ReviewRules.LicenseOf(license);
        var activity = candidate.LastActivity ?? resolved.Updated;
        var status = ReviewRules.ActivityOf(activity, now);
        var authentication = Authentication(resolved.Source, candidate.RequiredSecrets, findings);
        AddTrustAndCareFindings(findings, candidate, appName, resolved, licenseStatus, status, authentication.Names.Count);

        var taken = keepId is not null || _registry is null ? [] : (await _registry.ListAsync(cancellationToken).ConfigureAwait(false)).Select(integration => integration.Id).ToList();
        var id = keepId ?? ReviewRules.IdFor(appKey, taken);
        var source = resolved.Source;
        var accepted = new InstallCandidate
        {
            Id = id,
            AppName = appName,
            Name = candidate.Name,
            Source = source,
            Trust = candidate.Trust,
            Publisher = candidate.Publisher,
            SourceUrl = candidate.SourceUrl,
            RepositoryUrl = candidate.RepositoryUrl ?? resolved.Repository,
            Description = candidate.Description ?? resolved.Description,
            License = license,
            LicenseStatus = licenseStatus,
            LastActivity = activity,
            Activity = status,
            Runtime = resolved.Runtime,
            RequiredSecrets = authentication.Names,
            Authentication = authentication.Kind,
            LeavesThisPc = true,
            Capability = capability,
            Evidence = candidate.Evidence,
            MatchedTools = matched,
            McpEvidence = mcp,
            CommitSha = candidate.CommitSha ?? resolved.Commit,
            FoundIn = candidate.FoundIn,
            Notes = [.. findings.Where(finding => finding.Severity != ReviewSeverity.Blocker)],
            ReviewedAt = now,
            Fingerprint = InstallCandidate.FingerprintOf(id, appName, source),
        };
        return CandidateReview.Accept(accepted, findings);
    }

    // The facts of one way of installing the candidate, pinned; null with the reasons in findings when it cannot be.
    private async Task<Resolved?> ResolveAsync(IntegrationCandidate candidate, CandidatePackage package, List<ReviewFinding> findings, CancellationToken cancellationToken)
    {
        switch (package.Method)
        {
            case CandidateInstallMethod.Remote:
                findings.Add(ReviewRules.Caution(ReviewCode.HostedElsewhere, "It runs on a server, not on this PC, so what you ask of it is sent over the internet to that server."));
                return new Resolved(
                    new InstallSource { Kind = InstallSourceKind.Remote, Identifier = package.Identifier }, CandidateRuntime.None, null, null, null, null, null);
            case CandidateInstallMethod.Bundle:
                return ResolveBundle(candidate, package, findings);
            case CandidateInstallMethod.Npm:
                return await ResolveNpmAsync(candidate, package, findings, cancellationToken).ConfigureAwait(false);
            default:
                return await ResolvePyPiAsync(candidate, package, findings, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Resolved? ResolveBundle(IntegrationCandidate candidate, CandidatePackage package, List<ReviewFinding> findings)
    {
        var version = package.Version ?? candidate.Version;
        if (!ReviewRules.IsExactVersion(version))
        {
            findings.Add(ReviewRules.Block(ReviewCode.UnpinnedVersion, "The bundle has no exact version, so I cannot tell which one I would be installing."));
            return null;
        }

        if (package.Sha256 is not { } sha || !(new ContentHash("sha256", sha.ToLowerInvariant()).IsValid))
        {
            findings.Add(ReviewRules.Block(ReviewCode.NoIntegrityHash, "The bundle has no checksum, so I could not tell that what I download is what was published."));
            return null;
        }

        if (CandidateText.Https(package.Identifier) is not { } address || !PackageDownloadPolicy.Standard.IsAllowed(new Uri(address)))
        {
            findings.Add(ReviewRules.Block(ReviewCode.UnsafePackageSource, "The bundle is not on a web address I download from."));
            return null;
        }

        findings.Add(ReviewRules.Caution(ReviewCode.CouldNotCheck, "I cannot tell what the bundle needs to run, or what is in it, until it is downloaded."));
        return new Resolved(
            new InstallSource
            {
                Kind = InstallSourceKind.Bundle,
                Identifier = address,
                Version = version,
                DownloadUrl = address,
                Hash = new ContentHash("sha256", sha.ToLowerInvariant()),
            },
            CandidateRuntime.Unknown, null, null, null, null, null);
    }

    private async Task<Resolved?> ResolveNpmAsync(IntegrationCandidate candidate, CandidatePackage package, List<ReviewFinding> findings, CancellationToken cancellationToken)
    {
        var node = RuntimeCatalog.For(RuntimeKind.NodeJs);
        if (node is null)
        {
            findings.Add(ReviewRules.Block(ReviewCode.UnsupportedInstall, "I do not set up Node.js for this kind of PC, which this package needs."));
            return null;
        }

        if (await WebBlockAsync(cancellationToken).ConfigureAwait(false) is { } block)
        {
            findings.Add(block);
            return null;
        }

        NpmFacts? facts;
        try
        {
            facts = await WithinAsync(token => _metadata.GetNpmAsync(package.Identifier, package.Version, token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DiscoveryException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            findings.Add(ReviewRules.Block(ReviewCode.CouldNotCheck, "I could not reach npm to check the package."));
            return null;
        }

        if (facts is null)
        {
            findings.Add(ReviewRules.Block(ReviewCode.CouldNotCheck, "npm has no such package or version, so there is nothing to check."));
            return null;
        }

        if (!string.Equals(facts.Name, package.Identifier, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(ReviewRules.Block(ReviewCode.Malformed, "npm answered about a different package than the one asked about."));
            return null;
        }

        var blocked = false;
        if (!ReviewRules.IsExactVersion(facts.Version))
        {
            findings.Add(ReviewRules.Block(ReviewCode.UnpinnedVersion, "The package has no exact version I can pin."));
            blocked = true;
        }

        if (facts.TarballUrl is null || !IsRegistryTarball(facts.TarballUrl))
        {
            findings.Add(ReviewRules.Block(ReviewCode.UnsafePackageSource, "The package's file is not on the npm registry's own address, so I will not download it."));
            blocked = true;
        }

        if (facts.Hash is null)
        {
            findings.Add(ReviewRules.Block(ReviewCode.NoIntegrityHash, "npm publishes no checksum for it, so I could not tell that what I download is what was published."));
            blocked = true;
        }

        if (facts.Bin.Count == 0)
        {
            findings.Add(ReviewRules.Block(ReviewCode.NoEntryPoint, "The package provides no program to run, so it looks like a library and not a server."));
            blocked = true;
        }

        if (facts.Deprecated is not null)
        {
            findings.Add(ReviewRules.Caution(ReviewCode.Withdrawn, "Its author marked this version as deprecated."));
        }

        if (Version.TryParse(node.Version, out var nodeVersion))
        {
            switch (VersionConstraint.SatisfiesNpm(facts.NodeConstraint, nodeVersion))
            {
                case false:
                    findings.Add(ReviewRules.Block(ReviewCode.RuntimeIncompatible, $"It needs a different version of Node.js than the {node.DisplayName} that I set up."));
                    blocked = true;
                    break;
                case null:
                    findings.Add(ReviewRules.Caution(ReviewCode.CouldNotCheck, "I could not read which version of Node.js it needs, so I could not check it works with the one I set up."));
                    break;
            }
        }

        if (blocked)
        {
            return null;
        }

        if (facts.HasInstallScripts)
        {
            findings.Add(ReviewRules.Caution(ReviewCode.InstallScripts, "It has scripts that npm would run when installing it. I do not run them, so it may not work."));
        }

        if (facts.DependencyCount > 0)
        {
            findings.Add(ReviewRules.Caution(ReviewCode.DependenciesUnpinned, $"It depends on {facts.DependencyCount} other packages, which are chosen when it is installed and which I do not review one by one."));
        }

        var entry = facts.Bin.Keys.OrderBy(name => name, StringComparer.Ordinal).First();
        return new Resolved(
            new InstallSource
            {
                Kind = InstallSourceKind.Npm,
                Identifier = facts.Name,
                Version = facts.Version,
                DownloadUrl = facts.TarballUrl,
                Hash = facts.Hash,
                SizeBytes = facts.UnpackedSize,
                EntryName = entry,
                HasInstallScripts = facts.HasInstallScripts,
                DependencyCount = facts.DependencyCount,
                RuntimeConstraint = facts.NodeConstraint,
            },
            CandidateRuntime.NodeJs, facts.License, facts.GitHead, null, facts.RepositoryUrl, facts.Description);
    }

    private async Task<Resolved?> ResolvePyPiAsync(IntegrationCandidate candidate, CandidatePackage package, List<ReviewFinding> findings, CancellationToken cancellationToken)
    {
        var python = RuntimeCatalog.For(RuntimeKind.Python);
        if (python is null)
        {
            findings.Add(ReviewRules.Block(ReviewCode.UnsupportedInstall, "I do not set up Python for this kind of PC, which this package needs."));
            return null;
        }

        if (await WebBlockAsync(cancellationToken).ConfigureAwait(false) is { } block)
        {
            findings.Add(block);
            return null;
        }

        PyPiFacts? facts;
        try
        {
            facts = await WithinAsync(token => _metadata.GetPyPiAsync(package.Identifier, package.Version, token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DiscoveryException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            findings.Add(ReviewRules.Block(ReviewCode.CouldNotCheck, "I could not reach PyPI to check the package."));
            return null;
        }

        if (facts is null)
        {
            findings.Add(ReviewRules.Block(ReviewCode.CouldNotCheck, "PyPI has no such package or version, so there is nothing to check."));
            return null;
        }

        if (!string.Equals(Normalize(facts.Name), Normalize(package.Identifier), StringComparison.Ordinal))
        {
            findings.Add(ReviewRules.Block(ReviewCode.Malformed, "PyPI answered about a different package than the one asked about."));
            return null;
        }

        var blocked = false;
        if (facts.Yanked)
        {
            findings.Add(ReviewRules.Block(ReviewCode.Withdrawn, "Its author withdrew this version from PyPI."));
            blocked = true;
        }

        if (!ReviewRules.IsExactVersion(facts.Version))
        {
            findings.Add(ReviewRules.Block(ReviewCode.UnpinnedVersion, "The package has no exact version I can pin."));
            blocked = true;
        }

        if (facts.Wheel is null)
        {
            findings.Add(ReviewRules.Block(
                ReviewCode.UnsupportedInstall,
                facts.HasSourceOnly
                    ? "It is only published as source code, which has to be built when it is installed, and building runs the package's own setup code, which I do not do."
                    : "It has no file that works on this kind of PC."));
            blocked = true;
        }
        else if (!PackageDownloadPolicy.Standard.IsAllowed(new Uri(facts.Wheel.Url)))
        {
            findings.Add(ReviewRules.Block(ReviewCode.UnsafePackageSource, "The package's file is not on PyPI's own download address, so I will not download it."));
            blocked = true;
        }

        if (Version.TryParse(python.Version, out var pythonVersion))
        {
            switch (VersionConstraint.SatisfiesPython(facts.RequiresPython, pythonVersion))
            {
                case false:
                    findings.Add(ReviewRules.Block(ReviewCode.RuntimeIncompatible, $"It needs a different version of Python than the {python.DisplayName} that I set up."));
                    blocked = true;
                    break;
                case null:
                    findings.Add(ReviewRules.Caution(ReviewCode.CouldNotCheck, "I could not read which version of Python it needs, so I could not check it works with the one I set up."));
                    break;
            }
        }

        if (blocked || facts.Wheel is null)
        {
            return null;
        }

        if (facts.DependencyCount > 0)
        {
            findings.Add(ReviewRules.Caution(ReviewCode.DependenciesUnpinned, $"It depends on {facts.DependencyCount} other packages, which are chosen when it is installed and which I do not review one by one."));
        }

        findings.Add(ReviewRules.Caution(ReviewCode.CouldNotCheck, "PyPI does not say which program it provides; I will look when it is installed."));
        return new Resolved(
            new InstallSource
            {
                Kind = InstallSourceKind.PyPi,
                Identifier = facts.Name,
                Version = facts.Version,
                DownloadUrl = facts.Wheel.Url,
                Hash = new ContentHash("sha256", facts.Wheel.Sha256),
                SizeBytes = facts.Wheel.SizeBytes,
                DependencyCount = facts.DependencyCount,
                RuntimeConstraint = facts.RequiresPython,
            },
            CandidateRuntime.Python, facts.License, null, facts.UploadedAt, facts.RepositoryUrl, facts.Summary);
    }

    // The registry's own file: https, the npm registry's host, a .tgz.
    private static bool IsRegistryTarball(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("registry.npmjs.org", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    // PyPI treats - _ . as the same and ignores case.
    private static string Normalize(string name) => name.Replace('_', '-').Replace('.', '-').ToLowerInvariant();

    private static (IntegrationAuthKind Kind, IReadOnlyList<string> Names) Authentication(
        InstallSource source, IReadOnlyList<string> secrets, List<ReviewFinding> findings)
    {
        var names = secrets.Distinct(StringComparer.Ordinal).Take(8).ToList();
        if (names.Count == 0)
        {
            if (source.Kind == InstallSourceKind.Remote)
            {
                findings.Add(ReviewRules.Caution(ReviewCode.SignInUnknown, "It does not say whether it needs you to sign in. If it does, you can sign in in Settings, under Integrations."));
            }

            return (IntegrationAuthKind.None, []);
        }

        if (source.Kind == InstallSourceKind.Remote)
        {
            var headers = names.Where(IntegrationRules.IsValidHeaderKeyName).ToList();
            if (headers.Count < names.Count)
            {
                findings.Add(ReviewRules.Caution(ReviewCode.CouldNotCheck, "Some of the keys it asks for are named in a way I cannot send, so I left them out."));
            }

            var bearer = headers.Count == 1 && headers[0].Equals("Authorization", StringComparison.OrdinalIgnoreCase);
            return (headers.Count == 0 ? IntegrationAuthKind.None : bearer ? IntegrationAuthKind.BearerToken : IntegrationAuthKind.HeaderKey, headers);
        }

        var variables = names.Where(Mcp.McpLaunchRules.IsValidVariableName).ToList();
        if (variables.Count < names.Count)
        {
            findings.Add(ReviewRules.Caution(ReviewCode.CouldNotCheck, "Some of the keys it asks for are named in a way I cannot give it, so I left them out."));
        }

        return (variables.Count == 0 ? IntegrationAuthKind.None : IntegrationAuthKind.EnvironmentSecret, variables);
    }

    private static void AddTrustAndCareFindings(
        List<ReviewFinding> findings,
        IntegrationCandidate candidate,
        string appName,
        Resolved resolved,
        LicenseStatus licenseStatus,
        UpkeepStatus activity,
        int secrets)
    {
        findings.Add(ReviewRules.TrustFinding(candidate.Trust, appName));
        findings.Add(licenseStatus switch
        {
            LicenseStatus.Open => ReviewRules.Info(ReviewCode.Fact, "It has an open-source licence."),
            LicenseStatus.Unrecognized => ReviewRules.Caution(ReviewCode.LicenseUnclear, "It has a licence I do not recognise as open source, so I cannot tell what you may do with it."),
            _ => ReviewRules.Caution(ReviewCode.LicenseUnclear, "It lists no licence, so I cannot tell what you may do with it."),
        });
        findings.Add(activity switch
        {
            UpkeepStatus.Recent => ReviewRules.Info(ReviewCode.Fact, "It was changed within the last year."),
            UpkeepStatus.Aging => ReviewRules.Info(ReviewCode.Maintenance, "It was last changed between one and two years ago."),
            UpkeepStatus.Stale => ReviewRules.Caution(ReviewCode.Maintenance, "It has not been changed for over two years, so it may not be maintained."),
            _ => ReviewRules.Caution(ReviewCode.Maintenance, "I could not tell when it was last changed."),
        });
        if (candidate.CommitSha is null && resolved.Commit is null)
        {
            findings.Add(ReviewRules.Caution(ReviewCode.CommitUnknown, "I could not tell which version of its source code the package was built from, so I cannot tell that the two match."));
        }

        if (secrets > 0)
        {
            findings.Add(ReviewRules.Caution(ReviewCode.SecretsNeeded, $"It asks for {secrets} key{(secrets == 1 ? string.Empty : "s")}, so you would need an account with {appName} to give it."));
        }

        if (resolved.Source.Kind != InstallSourceKind.Remote)
        {
            findings.Add(ReviewRules.Caution(ReviewCode.UnsandboxedProgram, "It would run as a program on this PC with the same access to your files and the internet that you have. I cannot limit what it does."));
        }
    }

    // Why reading a package's facts is not allowed now, or null: it is the web, so it needs both locks open, as looking for integrations does.
    private async Task<ReviewFinding?> WebBlockAsync(CancellationToken cancellationToken)
    {
        if ((await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Privacy.LocalOnly)
        {
            return ReviewRules.Block(ReviewCode.WebLocked, "Checking the package needs the web, and Local Only mode is on. You can turn it off in Settings > Privacy.");
        }

        if (!(await _permissions.CheckAsync(PermissionCapability.ExternalSearch, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return ReviewRules.Block(ReviewCode.WebLocked, "Checking the package needs the web, and External Web and Image Search is off in Settings > Permissions.");
        }

        return null;
    }

    // One registry call with its own time limit.
    private async Task<T?> WithinAsync<T>(Func<CancellationToken, Task<T?>> read, CancellationToken cancellationToken)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        own.CancelAfter(_options.MetadataTimeout);
        return await read(own.Token).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 3170, Level = LogLevel.Information, Message = "Integration candidates reviewed: {Candidates} reviewed, {Accepted} passed")]
    private static partial void LogReviewed(ILogger logger, int candidates, int accepted);

    private sealed record Resolved(
        InstallSource Source, CandidateRuntime Runtime, string? License, string? Commit, DateTimeOffset? Updated, string? Repository, string? Description);
}
