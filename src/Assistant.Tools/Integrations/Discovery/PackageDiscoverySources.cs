using System.Text.Json;

namespace Assistant.Tools.Integrations;

/// <summary>
/// The npm registry's search (PROJECT_SPEC §4.8, step 106), asked only when the first search found nothing about the app that fits. A package is
/// <see cref="CandidateTrust.VerifiedVendor"/> only when it is published under an npm scope that is one of the app maker's (<c>@doist/todoist-mcp</c>):
/// the repository a package names for itself is its own claim and is not believed. One that calls itself official is
/// <see cref="CandidateTrust.ClaimsOfficial"/>.
/// </summary>
internal sealed class NpmDiscoverySource(IDiscoveryHttp http) : IIntegrationDiscoverySource
{
    private const string Endpoint = "https://registry.npmjs.org/-/v1/search";
    private const int Size = 15;
    private const int MaxBytes = 2 * 1024 * 1024;

    /// <inheritdoc/>
    public string Id => "npm";

    /// <inheritdoc/>
    public DiscoveryStage Stage => DiscoveryStage.WhenNeeded;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IntegrationCandidate>> SearchAsync(DiscoveryQuery query, CancellationToken cancellationToken)
    {
        var uri = new Uri($"{Endpoint}?text={Uri.EscapeDataString(query.Text + " mcp")}&size={Size}");
        var response = await http.GetAsync(uri, "application/json", MaxBytes, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return [];
        }

        using var document = WebJson.Parse(response.Body);
        var candidates = new List<IntegrationCandidate>();
        foreach (var entry in WebJson.Items(document.RootElement, "objects"))
        {
            if (WebJson.Member(entry, "package") is { } package && Read(package, query) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    internal static IntegrationCandidate? Read(JsonElement package, DiscoveryQuery query)
    {
        var name = CandidateText.Identifier(WebJson.Text(package, "name"));
        if (name is null)
        {
            return null;
        }

        var scope = name.StartsWith('@') && name.IndexOf('/', StringComparison.Ordinal) is > 1 and var slash ? name[..slash] : null;
        var links = WebJson.Member(package, "links") ?? default;
        var repository = CandidateText.GitHubRepositoryUrl(WebJson.Text(links, "repository"));
        var description = CandidateText.Line(WebJson.Text(package, "description"), 300);
        var trust = scope is not null && query.IsOwner(scope)
            ? CandidateTrust.VerifiedVendor
            : CandidateText.ClaimsOfficial(description) ? CandidateTrust.ClaimsOfficial : CandidateTrust.Community;
        var sourceUrl = repository
            ?? CandidateText.Https(WebJson.Text(links, "homepage"))
            ?? CandidateText.Https(WebJson.Text(links, "npm"))
            ?? "https://www.npmjs.com/package/" + name;
        var version = CandidateText.Line(WebJson.Text(package, "version"), 40);
        return new IntegrationCandidate
        {
            Name = name,
            SourceUrl = sourceUrl,
            RepositoryUrl = repository,
            Publisher = scope ?? (repository is not null && CandidateText.TryGitHubRepository(repository, out var owner, out _) ? owner : null),
            Trust = trust,
            License = CandidateText.Line(WebJson.Text(package, "license"), 40),
            LastActivity = WebJson.Date(package, "date"),
            Description = description,
            Packages = [new CandidatePackage(CandidateInstallMethod.Npm, name, version)],
            Runtime = CandidateRuntime.NodeJs,
            Version = version,
            FoundIn = ["npm"],
        };
    }
}

/// <summary>
/// PyPI (PROJECT_SPEC §4.8, step 106), asked only when the first search found nothing about the app that fits. PyPI has no search that can be asked for, so the
/// names an MCP server for an app is usually given (<c>todoist-mcp</c>, <c>mcp-todoist</c>, <c>todoist-mcp-server</c>, <c>mcp-server-todoist</c>) are looked up
/// one by one; most are not there. PyPI says nothing that can show who the app's maker is, so a package is at best
/// <see cref="CandidateTrust.ClaimsOfficial"/>.
/// </summary>
internal sealed class PyPiDiscoverySource(IDiscoveryHttp http) : IIntegrationDiscoverySource
{
    private const int MaxBytes = 1024 * 1024;

    /// <inheritdoc/>
    public string Id => "pypi";

    /// <inheritdoc/>
    public DiscoveryStage Stage => DiscoveryStage.WhenNeeded;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IntegrationCandidate>> SearchAsync(DiscoveryQuery query, CancellationToken cancellationToken)
    {
        var slug = query.Text.Replace(' ', '-');
        string[] names = [$"{slug}-mcp", $"mcp-{slug}", $"{slug}-mcp-server", $"mcp-server-{slug}"];
        var looked = await Task.WhenAll(names.Select(name => LookUpAsync(name, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (looked.All(answer => answer.Failure is not null) && looked.Select(answer => answer.Failure).FirstOrDefault() is { } failure)
        {
            throw new DiscoveryException(failure);
        }

        return [.. looked.Select(answer => answer.Candidate).OfType<IntegrationCandidate>()];
    }

    private async Task<(IntegrationCandidate? Candidate, DiscoveryFailure? Failure)> LookUpAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetAsync(new Uri("https://pypi.org/pypi/" + Uri.EscapeDataString(name) + "/json"), "application/json", MaxBytes, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return (null, null);
            }

            using var document = WebJson.Parse(response.Body);
            return (Read(document.RootElement), null);
        }
        catch (DiscoveryException exception)
        {
            return (null, exception.Failure);
        }
    }

    internal static IntegrationCandidate? Read(JsonElement root)
    {
        if (WebJson.Member(root, "info") is not { ValueKind: JsonValueKind.Object } info || CandidateText.Identifier(WebJson.Text(info, "name")) is not { } name)
        {
            return null;
        }

        string? repository = null;
        if (WebJson.Member(info, "project_urls") is { ValueKind: JsonValueKind.Object } urls)
        {
            repository = urls.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .Select(property => CandidateText.GitHubRepositoryUrl(property.Value.GetString()))
                .FirstOrDefault(url => url is not null);
        }

        var summary = CandidateText.Line(WebJson.Text(info, "summary"), 300);
        var license = CandidateText.Line(WebJson.Text(info, "license_expression"), 40)
            ?? (WebJson.Text(info, "license") is { Length: > 0 and <= 40 } shortLicense ? CandidateText.Line(shortLicense, 40) : null);
        var version = CandidateText.Line(WebJson.Text(info, "version"), 40);
        DateTimeOffset? uploaded = null;
        if (WebJson.Items(root, "urls").FirstOrDefault() is { ValueKind: JsonValueKind.Object } file)
        {
            uploaded = WebJson.Date(file, "upload_time_iso_8601");
        }

        return new IntegrationCandidate
        {
            Name = name,
            SourceUrl = repository ?? "https://pypi.org/project/" + name + "/",
            RepositoryUrl = repository,
            Publisher = repository is not null && CandidateText.TryGitHubRepository(repository, out var owner, out _) ? owner : null,
            Trust = CandidateText.ClaimsOfficial(summary) ? CandidateTrust.ClaimsOfficial : CandidateTrust.Community,
            License = license,
            LastActivity = uploaded,
            Description = summary,
            Packages = [new CandidatePackage(CandidateInstallMethod.PyPi, name, version)],
            Runtime = CandidateRuntime.Python,
            Version = version,
            FoundIn = ["pypi"],
        };
    }
}
