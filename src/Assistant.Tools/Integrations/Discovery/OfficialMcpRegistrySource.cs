using System.Text.Json;

namespace Assistant.Tools.Integrations;

/// <summary>
/// The official MCP registry (<c>registry.modelcontextprotocol.io</c>, PROJECT_SPEC §4.8, step 106): a catalog whose entries are published under a name the registry has
/// checked the publisher owns (a GitHub account or a domain). The registry's search is a part of a name, so it is asked for the app's one word;
/// whether an entry is about the app and fits is decided afterwards by the finder. An entry published under the app maker's account or domain is
/// <see cref="CandidateTrust.VerifiedVendor"/>; one that calls itself official is <see cref="CandidateTrust.ClaimsOfficial"/>; the rest
/// are <see cref="CandidateTrust.Community"/>. Old versions of an entry and entries that are not active are left out. It is slow (tens of seconds at times), so the finder does not wait for it
/// once a better answer is in.
/// </summary>
internal sealed class OfficialMcpRegistrySource(IDiscoveryHttp http) : IIntegrationDiscoverySource
{
    private const string Endpoint = "https://registry.modelcontextprotocol.io/v0/servers";
    private const int PageSize = 40;
    private const int MaxBytes = 2 * 1024 * 1024;

    /// <inheritdoc/>
    public string Id => "mcp-registry";

    /// <inheritdoc/>
    public DiscoveryStage Stage => DiscoveryStage.First;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IntegrationCandidate>> SearchAsync(DiscoveryQuery query, CancellationToken cancellationToken)
    {
        var uri = new Uri($"{Endpoint}?search={Uri.EscapeDataString(query.Term)}&limit={PageSize}");
        var response = await http.GetAsync(uri, "application/json", MaxBytes, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return [];
        }

        using var document = WebJson.Parse(response.Body);
        var candidates = new List<IntegrationCandidate>();
        foreach (var entry in WebJson.Items(document.RootElement, "servers"))
        {
            if (Read(entry, query) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    // One entry of the registry, or null when it is old, not active or has no name or address worth keeping.
    internal static IntegrationCandidate? Read(JsonElement entry, DiscoveryQuery query)
    {
        if (WebJson.Member(entry, "server") is not { ValueKind: JsonValueKind.Object } server || WebJson.Text(server, "name") is not { } rawName)
        {
            return null;
        }

        var official = WebJson.Member(WebJson.Member(entry, "_meta") ?? default, "io.modelcontextprotocol.registry/official");
        var meta = official ?? default;
        if (official is not null && (WebJson.Flag(meta, "isLatest") == false || WebJson.Text(meta, "status") is { } status && !status.Equals("active", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var name = CandidateText.Line(rawName, 120);
        var description = CandidateText.Line(WebJson.Text(server, "description"), 300);
        if (name is null)
        {
            return null;
        }

        var repository = WebJson.Member(server, "repository") is { } repo ? CandidateText.Https(WebJson.Text(repo, "url")) : null;
        var repositoryUrl = CandidateText.GitHubRepositoryUrl(repository) ?? repository;
        var (publisher, trust) = Publisher(name, description, query);
        var packages = Packages(server, out var runtime, out var secrets);
        var remote = Remote(server, secrets);
        if (remote is not null)
        {
            // A server the publisher hosts needs nothing on this PC.
            packages.Insert(0, new CandidatePackage(CandidateInstallMethod.Remote, remote, null));
            runtime = CandidateRuntime.None;
        }

        var sourceUrl = repositoryUrl
            ?? CandidateText.Https(WebJson.Text(server, "websiteUrl"))
            ?? "https://registry.modelcontextprotocol.io/v0/servers?search=" + Uri.EscapeDataString(name);
        return new IntegrationCandidate
        {
            Name = name,
            SourceUrl = sourceUrl,
            RepositoryUrl = repositoryUrl,
            Publisher = publisher,
            Trust = trust,
            LastActivity = WebJson.Date(meta, "updatedAt") ?? WebJson.Date(meta, "publishedAt"),
            Description = description,
            Packages = packages,
            RemoteUrl = remote,
            Runtime = runtime,
            RequiredSecrets = [.. secrets.Distinct(StringComparer.Ordinal).Take(8)],
            Version = CandidateText.Line(WebJson.Text(server, "version"), 40),
            FoundIn = ["mcp-registry"],
        };
    }

    // A registry name is <reverse-DNS namespace>/<server>: io.github.<account> for a GitHub account, com.<company> for a domain the publisher proved it owns.
    private static (string? Publisher, CandidateTrust Trust) Publisher(string name, string? description, DiscoveryQuery query)
    {
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        var space = slash > 0 ? name[..slash] : null;
        if (space is null)
        {
            return (null, CandidateText.ClaimsOfficial(description) ? CandidateTrust.ClaimsOfficial : CandidateTrust.Unknown);
        }

        const string gitHubSpace = "io.github.";
        if (space.StartsWith(gitHubSpace, StringComparison.OrdinalIgnoreCase))
        {
            var account = space[gitHubSpace.Length..];
            var shown = CandidateText.Line(account, 60);
            if (query.IsOwner(account))
            {
                return (shown, CandidateTrust.VerifiedVendor);
            }

            return (shown, CandidateText.ClaimsOfficial(description) ? CandidateTrust.ClaimsOfficial : CandidateTrust.Community);
        }

        // com.microsoft is microsoft.com: the namespace read backwards.
        var domain = string.Join('.', space.Split('.').Reverse());
        var publisher = CandidateText.Line(domain, 60);
        if (query.IsVendorDomain(domain))
        {
            return (publisher, CandidateTrust.VerifiedVendor);
        }

        return (publisher, CandidateText.ClaimsOfficial(description) ? CandidateTrust.ClaimsOfficial : CandidateTrust.Community);
    }

    private static List<CandidatePackage> Packages(JsonElement server, out CandidateRuntime runtime, out List<string> secrets)
    {
        runtime = CandidateRuntime.Unknown;
        secrets = [];
        var packages = new List<CandidatePackage>();
        foreach (var package in WebJson.Items(server, "packages").Take(6))
        {
            var method = WebJson.Text(package, "registryType")?.ToLowerInvariant() switch
            {
                "npm" => CandidateInstallMethod.Npm,
                "pypi" => CandidateInstallMethod.PyPi,
                "oci" => CandidateInstallMethod.Container,
                "nuget" => CandidateInstallMethod.NuGet,
                "mcpb" => CandidateInstallMethod.Bundle,
                _ => (CandidateInstallMethod?)null,
            };
            var identifier = CandidateText.Identifier(WebJson.Text(package, "identifier"));
            if (method is null || identifier is null)
            {
                continue;
            }

            var sha = WebJson.Text(package, "fileSha256")?.Trim().ToLowerInvariant();
            packages.Add(new CandidatePackage(
                method.Value,
                identifier,
                CandidateText.Line(WebJson.Text(package, "version"), 40),
                sha is { Length: 64 } && sha.All(char.IsAsciiHexDigit) ? sha : null));
            if (runtime == CandidateRuntime.Unknown)
            {
                runtime = RuntimeOf(method.Value);
            }

            foreach (var variable in WebJson.Items(package, "environmentVariables"))
            {
                if ((WebJson.Flag(variable, "isRequired") == true || WebJson.Flag(variable, "isSecret") == true) && CandidateText.Line(WebJson.Text(variable, "name"), 64) is { } variableName)
                {
                    secrets.Add(variableName);
                }
            }
        }

        return packages;
    }

    // The address of a server the publisher hosts, and the names of the headers it wants filled in.
    private static string? Remote(JsonElement server, List<string> secrets)
    {
        string? address = null;
        foreach (var remote in WebJson.Items(server, "remotes").Take(4))
        {
            var url = CandidateText.Https(WebJson.Text(remote, "url"));
            if (url is null)
            {
                continue;
            }

            address ??= url;
            foreach (var header in WebJson.Items(remote, "headers"))
            {
                if ((WebJson.Flag(header, "isRequired") == true || WebJson.Flag(header, "isSecret") == true) && CandidateText.Line(WebJson.Text(header, "name"), 64) is { } headerName)
                {
                    secrets.Add(headerName);
                }
            }
        }

        return address;
    }

    internal static CandidateRuntime RuntimeOf(CandidateInstallMethod method) => method switch
    {
        CandidateInstallMethod.Remote => CandidateRuntime.None,
        CandidateInstallMethod.Npm => CandidateRuntime.NodeJs,
        CandidateInstallMethod.PyPi => CandidateRuntime.Python,
        CandidateInstallMethod.Container => CandidateRuntime.Container,
        CandidateInstallMethod.NuGet => CandidateRuntime.DotNet,
        _ => CandidateRuntime.Unknown,
    };
}
