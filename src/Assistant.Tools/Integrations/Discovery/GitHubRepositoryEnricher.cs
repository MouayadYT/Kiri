namespace Assistant.Tools.Integrations;

/// <summary>Adds to a candidate what its repository's README and latest commit say.</summary>
internal interface IRepositoryEnricher
{
    /// <summary>
    /// <paramref name="candidate"/> with the tools its README lists, the keys it asks for, what it needs to run, whether it speaks of the capability, and the
    /// commit it was at. A candidate with no GitHub repository, or whose repository cannot be read, comes back as it was.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IntegrationCandidate> EnrichAsync(IntegrationCandidate candidate, IntegrationCapability capability, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the README and the latest commit of a candidate's GitHub repository (PROJECT_SPEC §4.8, step 106), two small requests: the README as text through the
/// GitHub API (read as data by <see cref="ReadmeReader"/>, never followed) and the commit's identifier, so that what was looked at can be told from
/// what the repository becomes later. Nothing is cloned or downloaded. A request that fails leaves the candidate as it was.
/// </summary>
internal sealed class GitHubRepositoryEnricher(IDiscoveryHttp http) : IRepositoryEnricher
{
    private const int MaxReadmeBytes = 120 * 1024;
    private const int CommitIdLength = 40;

    /// <inheritdoc/>
    public async Task<IntegrationCandidate> EnrichAsync(IntegrationCandidate candidate, IntegrationCapability capability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(capability);
        if (!CandidateText.TryGitHubRepository(candidate.RepositoryUrl, out var owner, out var repo))
        {
            return candidate;
        }

        var readme = ReadAsync(
            new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/readme"), "application/vnd.github.raw+json", MaxReadmeBytes, cancellationToken);
        var commit = ReadAsync(
            new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/commits/HEAD"), "application/vnd.github.sha", 256, cancellationToken);
        var text = await readme.ConfigureAwait(false);
        var sha = (await commit.ConfigureAwait(false))?.Trim();
        if (text is null && sha is null)
        {
            // Nothing could be read: the candidate stays exactly as it was.
            return candidate;
        }

        var tools = ReadmeReader.ToolNames(text);
        var secrets = candidate.RequiredSecrets.Concat(ReadmeReader.SecretNames(text)).Distinct(StringComparer.Ordinal).Take(8).ToArray();
        var evidence = CapabilityMatcher.EvidenceOf(capability, tools, candidate.Description);
        if (evidence < CapabilityEvidence.Described && CapabilityMatcher.Mentions(capability, text))
        {
            evidence = CapabilityEvidence.Described;
        }

        return candidate with
        {
            ToolNames = [.. tools.Select(tool => CandidateText.Line(tool, 64)).OfType<string>()],
            RequiredSecrets = secrets,
            Runtime = candidate.Runtime == CandidateRuntime.Unknown ? ReadmeReader.RuntimeOf(text) : candidate.Runtime,
            CommitSha = sha is { Length: CommitIdLength } && sha.All(char.IsAsciiHexDigit) ? sha.ToLowerInvariant() : candidate.CommitSha,
            Evidence = evidence > candidate.Evidence ? evidence : candidate.Evidence,
        };
    }

    // The text of a page, or null when it cannot be read (the candidate is then left as it was); stopping goes on.
    private async Task<string?> ReadAsync(Uri uri, string accept, int maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetAsync(uri, accept, maxBytes, cancellationToken).ConfigureAwait(false);
            return response.IsSuccess ? response.Body : null;
        }
        catch (DiscoveryException)
        {
            return null;
        }
    }
}
