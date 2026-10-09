namespace Assistant.Tools.Integrations;

/// <summary>
/// What the Integration Finder searches with (PROJECT_SPEC §4.8, step 106): the app's name, one word for what is wanted done, and the word MCP. It is built from an
/// <see cref="IntegrationNeed"/> alone, so it can hold nothing the user wrote beyond the name of the app and the capability: not the text of a task, not a
/// person's name. It also says whose repositories, packages and domains are the app maker's own, which is how a candidate is told apart from a copy.
/// </summary>
internal sealed record DiscoveryQuery
{
    private DiscoveryQuery(IntegrationNeed need, KnownApp? app, string text, string term)
    {
        Need = need;
        App = app;
        Text = text;
        Term = term;
    }

    /// <summary>The need.</summary>
    public IntegrationNeed Need { get; }

    /// <summary>The app, when the Assistant knows it by name.</summary>
    public KnownApp? App { get; }

    /// <summary>The app's name as words for a search ("microsoft todo"); lower-case letters, digits and single spaces.</summary>
    public string Text { get; }

    /// <summary>The one word the registries are searched with ("todo"); a registry's search is a part of a name.</summary>
    public string Term { get; }

    /// <summary>A word for the kind of thing the capability is about ("task"), or <see langword="null"/>.</summary>
    public string? ObjectWord => Need.Capability.Object is { } obj ? obj.Replace(' ', '-') : null;

    /// <summary>The GitHub accounts and npm scopes that are the app maker's own.</summary>
    public IReadOnlyList<string> Owners => App?.Owners ?? [];

    /// <summary>The domains that are the app maker's own.</summary>
    public IReadOnlyList<string> Domains => App?.Domains ?? [];

    /// <summary>Makes the query for <paramref name="need"/>; <see langword="null"/> when its app has no name that is safe to search with.</summary>
    public static DiscoveryQuery? For(IntegrationNeed need)
    {
        ArgumentNullException.ThrowIfNull(need);
        var app = KnownApps.ByAppKey(need.AppKey);

        // The shortest way of writing the name that is still the name ("microsoft todo" for Microsoft To Do).
        var written = app is null
            ? [need.AppName]
            : app.Aliases.Prepend(app.Name).Where(alias => AppIdentity.Squash(alias) == app.Key).ToArray();
        var words = written.Select(name => AppIdentity.Words(name).ToArray()).Where(name => name.Length > 0).OrderBy(name => name.Length).ThenBy(name => string.Concat(name).Length).FirstOrDefault();
        if (words is null || words.Any(word => CandidateText.SearchWord(word) is null) || words.Length > 4)
        {
            return null;
        }

        var text = string.Join(' ', words);
        var term = CandidateText.SearchWord(app?.SearchTerm)
            ?? CandidateText.SearchWord(need.AppKey)
            ?? words.OrderByDescending(word => word.Length).Select(word => CandidateText.SearchWord(word)).FirstOrDefault(word => word is not null);
        return term is null ? null : new DiscoveryQuery(need, app, text, term);
    }

    /// <summary>Whether <paramref name="account"/> (a GitHub account or an npm scope, with or without a leading <c>@</c>) belongs to the app's maker.</summary>
    public bool IsOwner(string? account) =>
        account is not null && Owners.Contains(account.TrimStart('@'), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="domain"/> (<c>microsoft.com</c>) is the app maker's, or a subdomain of it.</summary>
    public bool IsVendorDomain(string? domain) =>
        !string.IsNullOrEmpty(domain)
        && Domains.Any(own => domain.Equals(own, StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + own, StringComparison.OrdinalIgnoreCase));
}
