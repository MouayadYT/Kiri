using System.Text.Json;

namespace Assistant.Tools.Integrations;

/// <summary>
/// GitHub's repository search (PROJECT_SPEC §4.8, step 106), asked three focused questions at once: the app maker's own accounts first (<c>todoist mcp user:Doist</c>), then
/// repositories whose name or description is about the app and MCP, then those whose README speaks of the app, what is wanted and MCP. A repository is
/// <see cref="CandidateTrust.VerifiedVendor"/> only when its owner is one of the app maker's accounts the Assistant knows; one that calls itself
/// official is <see cref="CandidateTrust.ClaimsOfficial"/>; the rest are <see cref="CandidateTrust.Community"/>. Forks, disabled repositories and
/// repositories GitHub marks private are left out. One of the three questions failing (GitHub limits the unauthenticated search to a few a minute) does
/// not fail the others.
/// </summary>
internal sealed class GitHubDiscoverySource(IDiscoveryHttp http) : IIntegrationDiscoverySource
{
    private const string Endpoint = "https://api.github.com/search/repositories";
    private const int PerPage = 8;
    private const int MaxBytes = 1024 * 1024;
    private const int MaxOwnerQueries = 2;

    /// <inheritdoc/>
    public string Id => "github";

    /// <inheritdoc/>
    public DiscoveryStage Stage => DiscoveryStage.First;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IntegrationCandidate>> SearchAsync(DiscoveryQuery query, CancellationToken cancellationToken)
    {
        var questions = new List<string>();
        questions.AddRange(query.Owners.Take(MaxOwnerQueries).Select(owner => $"{query.Text} mcp user:{owner}"));
        questions.Add($"{query.Text} mcp in:name,description");
        if (query.ObjectWord is { } objectWord)
        {
            questions.Add($"{query.Text} {objectWord} mcp in:name,description,readme");
        }

        var asked = await Task.WhenAll(questions.Select(question => AskAsync(question, query, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Some of the questions failing is not a failure; all of them failing is.
        if (asked.All(answer => answer.Failure is not null) && asked.Select(answer => answer.Failure).FirstOrDefault() is { } failure)
        {
            throw new DiscoveryException(failure);
        }

        return [.. asked.SelectMany(answer => answer.Candidates)];
    }

    private async Task<Answer> AskAsync(string question, DiscoveryQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri($"{Endpoint}?q={Uri.EscapeDataString(question)}&per_page={PerPage}");
            var response = await http.GetAsync(uri, "application/vnd.github+json", MaxBytes, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return new Answer([], null);
            }

            using var document = WebJson.Parse(response.Body);
            var candidates = new List<IntegrationCandidate>();
            foreach (var item in WebJson.Items(document.RootElement, "items"))
            {
                if (Read(item, query) is { } candidate)
                {
                    candidates.Add(candidate);
                }
            }

            return new Answer(candidates, null);
        }
        catch (DiscoveryException exception)
        {
            return new Answer([], exception.Failure);
        }
    }

    // One repository of a search, or null when it is a fork, disabled or has nothing worth keeping.
    internal static IntegrationCandidate? Read(JsonElement item, DiscoveryQuery query)
    {
        if (item.ValueKind != JsonValueKind.Object || WebJson.Flag(item, "fork") == true || WebJson.Flag(item, "disabled") == true || WebJson.Flag(item, "private") == true)
        {
            return null;
        }

        var url = CandidateText.GitHubRepositoryUrl(WebJson.Text(item, "html_url"));
        var owner = WebJson.Member(item, "owner") is { } ownerElement ? WebJson.Text(ownerElement, "login") : null;
        var name = CandidateText.Line(WebJson.Text(item, "full_name"), 120);
        if (url is null || name is null || owner is null)
        {
            return null;
        }

        var description = CandidateText.Line(WebJson.Text(item, "description"), 300);
        var topics = string.Join(' ', WebJson.Items(item, "topics").Where(topic => topic.ValueKind == JsonValueKind.String).Select(topic => topic.GetString()));
        var trust = query.IsOwner(owner)
            ? CandidateTrust.VerifiedVendor
            : CandidateText.ClaimsOfficial(description) || CandidateText.ClaimsOfficial(topics) ? CandidateTrust.ClaimsOfficial : CandidateTrust.Community;
        var license = WebJson.Member(item, "license") is { } licenseElement ? WebJson.Text(licenseElement, "spdx_id") : null;
        return new IntegrationCandidate
        {
            Name = name,
            SourceUrl = url,
            RepositoryUrl = url,
            Publisher = CandidateText.Line(owner, 60),
            Trust = trust,
            License = license is null or "NOASSERTION" ? null : CandidateText.Line(license, 40),
            LastActivity = WebJson.Date(item, "pushed_at"),
            Archived = WebJson.Flag(item, "archived") == true,
            Stars = WebJson.Whole(item, "stargazers_count"),
            Description = description,
            Packages = [new CandidatePackage(CandidateInstallMethod.SourceOnly, url, null)],
            Runtime = RuntimeOfLanguage(WebJson.Text(item, "language")),
            FoundIn = ["github"],
        };
    }

    private static CandidateRuntime RuntimeOfLanguage(string? language) => language switch
    {
        "TypeScript" or "JavaScript" => CandidateRuntime.NodeJs,
        "Python" => CandidateRuntime.Python,
        "C#" => CandidateRuntime.DotNet,
        _ => CandidateRuntime.Unknown,
    };

    private sealed record Answer(IReadOnlyList<IntegrationCandidate> Candidates, DiscoveryFailure? Failure);
}
