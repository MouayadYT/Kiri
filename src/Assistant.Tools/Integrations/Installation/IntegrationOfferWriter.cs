using System.Globalization;
using Assistant.Core.Domain;

namespace Assistant.Tools.Integrations;

/// <summary>
/// Writes what the approval panel says (PROJECT_SPEC §4.8, step 108) from a reviewed <see cref="InstallCandidate"/> and the plan of what installing it would
/// download. The words are the Assistant's, built from facts it checked; the few texts that came from the web (a name, a publisher, an address, the
/// names of keys) are cleaned to one line, cut, and set off in quotation marks so that they cannot pass for the Assistant speaking. Where something is not
/// known the panel says so; it never says an integration is safe.
/// </summary>
internal static class IntegrationOfferWriter
{
    // The review findings that the panel's own lines already say, so that they are not said twice.
    private static readonly HashSet<ReviewCode> SaidElsewhere =
    [
        ReviewCode.UnsandboxedProgram, ReviewCode.SecretsNeeded, ReviewCode.HostedElsewhere, ReviewCode.SignInUnknown, ReviewCode.DependenciesUnpinned,
    ];

    /// <summary>The offer for <paramref name="candidate"/>, to be shown to the user under <paramref name="offerId"/>.</summary>
    /// <param name="candidate">What was reviewed.</param>
    /// <param name="plan">What installing it would download.</param>
    /// <param name="offerId">The id the installer knows the offer by.</param>
    /// <param name="current">For an update, the integration that is installed now.</param>
    public static IntegrationOffer Write(InstallCandidate candidate, InstallPlan plan, string offerId, InstalledIntegration? current = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(plan);
        var app = Plain(candidate.AppName, 60);
        var update = current is not null;
        return new IntegrationOffer
        {
            OfferId = offerId,
            Kind = update ? IntegrationOfferKind.Update : IntegrationOfferKind.Install,
            AppName = app,
            IntegrationName = Plain(candidate.Name, 120),
            Maker = candidate.Trust switch
            {
                CandidateTrust.VerifiedVendor => IntegrationOfferMaker.Official,
                CandidateTrust.ClaimsOfficial => IntegrationOfferMaker.ClaimsOfficial,
                CandidateTrust.Community => IntegrationOfferMaker.Community,
                _ => IntegrationOfferMaker.Unknown,
            },
            MakerText = Maker(candidate, app),
            Provides = Provides(candidate, update),
            Source = Source(candidate),
            Needs = Needs(candidate, app),
            Requirements = Requirements(candidate, plan),
            Notes = Notes(candidate),
            CurrentVersion = current?.InstalledVersion is { } version ? Plain(version, 40) : null,
            NewVersion = candidate.Source.Version is { } next ? Plain(next, 40) : null,
        };
    }

    /// <summary>The offer to connect <paramref name="endpoint"/>, or, with <paramref name="signIn"/>, to sign in again to it, to be shown under <paramref name="offerId"/>.</summary>
    public static IntegrationOffer WriteConnect(KnownEndpoint endpoint, string offerId, bool signIn, IntegrationCapability? capability)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var app = Plain(endpoint.Name, 60);
        var uri = new Uri(endpoint.Endpoint);
        var place = uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture)) + (uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : string.Empty);
        var wanted = capability is null ? null : IntegrationReplyWriter.Wanted(capability);
        var needs = new List<string>
        {
            $"Your browser opens on {app}'s own page, where you choose to allow it. I never see your password.",
            endpoint.IsBundled
                ? $"It is a small program that comes with the Assistant. It talks to {app} through Microsoft, so what you ask of it is sent over the internet to Microsoft."
                : endpoint.RunsOnThisPc
                    ? $"It is {app}'s own server on this PC: what you ask of it stays on this PC."
                    : $"It runs on {app}'s server, not on this PC: what you ask of it is sent over the internet to that server.",
        };
        if (!endpoint.RunsOnThisPc)
        {
            needs.Add("While Local Only mode is on, I will not use it.");
        }

        return new IntegrationOffer
        {
            OfferId = offerId,
            Kind = signIn ? IntegrationOfferKind.SignIn : IntegrationOfferKind.Connect,
            AppName = app,
            IntegrationName = app + " (official)",
            Maker = endpoint.IsBundled ? IntegrationOfferMaker.Unknown : IntegrationOfferMaker.Official,
            MakerText = endpoint.IsBundled
                ? $"Made for the Assistant, and it ships with it. It uses {app}'s own service from Microsoft; it is not made by Microsoft."
                : $"Official. This is {app}'s own server, at an address I have built in.",
            Provides = wanted is null
                ? $"Lets me use {app} for you."
                : $"{char.ToUpperInvariant(wanted[0])}{wanted[1..]}, and whatever else {app} lets me do for you.",
            Source = endpoint.IsBundled
                ? $"A program that comes with the Assistant ({Plain(endpoint.Program!, 60)}), which uses Microsoft Graph."
                : endpoint.RunsOnThisPc ? $"{app}'s server on this PC, at {place}." : $"Server hosted by {app}'s maker, at {place}.",
            Needs = needs,
            Requirements = endpoint.IsBundled
                ? ["Nothing is downloaded or installed. The program is already here. I keep your sign-in in Windows' own credential store."]
                : ["Nothing is downloaded or installed. I only record where the server is, and keep your sign-in in Windows' own credential store."],
            Notes = [],
        };
    }

    private static string Maker(InstallCandidate candidate, string app)
    {
        var publisher = candidate.Publisher is { } name ? Quote(name, 60) : null;
        return candidate.Trust switch
        {
            CandidateTrust.VerifiedVendor =>
                publisher is null ? $"Official. Made by {app}'s own maker." : $"Official. Published under {publisher}, which belongs to {app}'s maker.",
            CandidateTrust.ClaimsOfficial =>
                $"It says it is official, but I could not confirm that{(publisher is null ? string.Empty : $". Published by {publisher}")}. Treat it as community-made.",
            CandidateTrust.Community =>
                $"Community-made{(publisher is null ? string.Empty : $" by {publisher}")}. It is not made by {app}'s maker, as far as I can tell.",
            _ => "I could not tell who made it.",
        };
    }

    private static string Provides(InstallCandidate candidate, bool update)
    {
        if (update)
        {
            return "A newer version of what you have installed, with the same tools unless it says otherwise.";
        }

        if (candidate.Capability is not { } capability)
        {
            return "Connects me to " + Plain(candidate.AppName, 60) + ".";
        }

        var wanted = IntegrationReplyWriter.Wanted(capability);
        var sentence = char.ToUpperInvariant(wanted[0]) + wanted[1..];
        return candidate.Evidence switch
        {
            CapabilityEvidence.ToolListed when candidate.MatchedTools.Count > 0 =>
                $"{sentence}. It lists a tool for this: {Quote(candidate.MatchedTools[0], 64)}.",
            CapabilityEvidence.ToolListed or CapabilityEvidence.Described => $"{sentence}, going by its description. I will check its tools once it is installed.",
            _ => $"{sentence}. I could not confirm that it can. I will check its tools once it is installed.",
        };
    }

    private static string Source(InstallCandidate candidate)
    {
        var source = candidate.Source;
        var what = source.Kind switch
        {
            InstallSourceKind.Npm => $"npm package {Quote(source.Identifier, 120)}",
            InstallSourceKind.PyPi => $"Python package {Quote(source.Identifier, 120)}",
            InstallSourceKind.Bundle => "MCP bundle",
            _ => "Server hosted by its maker",
        };
        var version = source.Version is { } v ? $", version {Plain(v, 40)}" : string.Empty;
        var where = Where(candidate.RepositoryUrl ?? candidate.SourceUrl ?? (source.Kind == InstallSourceKind.Remote ? source.Identifier : source.DownloadUrl));
        var commit = candidate.CommitSha is { Length: >= 7 } sha ? $", reviewed at commit {sha[..7]}" : string.Empty;
        return $"{what}{version}{(where is null ? string.Empty : $", from {where}")}{commit}.";
    }

    // The place without its scheme: github.com/Doist/todoist-ai.
    private static string? Where(string? address)
    {
        if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return CandidateText.Line(uri.Host + (path.Length > 1 ? path : string.Empty), 100);
    }

    private static List<string> Needs(InstallCandidate candidate, string app)
    {
        var lines = new List<string>();
        var names = candidate.RequiredSecrets.Count == 0
            ? string.Empty
            : " (" + string.Join(", ", candidate.RequiredSecrets.Take(4).Select(name => Quote(name, 64))) + ")";
        if (candidate.RequiredSecrets.Count > 0)
        {
            var count = candidate.RequiredSecrets.Count;
            lines.Add(
                $"An account with {app}: it asks for {(count == 1 ? "a key" : count.ToString(CultureInfo.InvariantCulture) + " keys")}{names}. "
                + $"After it is installed you give {(count == 1 ? "it" : "them")} in Settings, under Integrations, and it is not used until you do.");
        }
        else if (candidate.IsRemote)
        {
            lines.Add($"It may ask you to sign in to {app}. If it does, you can sign in, or give a token, in Settings, under Integrations.");
        }
        else
        {
            lines.Add("No account or key.");
        }

        lines.Add(candidate.IsRemote
            ? "It runs on a server, not on this PC: what you ask of it is sent over the internet to that server."
            : "It runs as a program on this PC with the same access to your files and the internet that you have. I cannot limit what it does.");
        if (candidate.LeavesThisPc)
        {
            lines.Add("While Local Only mode is on, I will not use it.");
        }

        lines.Add("It does not need administrator rights.");
        return lines;
    }

    private static List<string> Requirements(InstallCandidate candidate, InstallPlan plan)
    {
        var lines = new List<string>();
        if (plan.Blocker is { } blocker)
        {
            lines.Add(blocker);
            return lines;
        }

        if (candidate.IsRemote)
        {
            lines.Add("Nothing is downloaded or installed on this PC. I only record where the server is.");
            return lines;
        }

        foreach (var download in plan.Downloads)
        {
            var size = download.Megabytes is { } megabytes ? $" (about {megabytes.ToString(CultureInfo.InvariantCulture)} MB)" : string.Empty;
            lines.Add(download.IsRuntime
                ? $"{download.Name} is not set up for me yet, so I will download it too{size}. It stays in my own folder and does not touch any you have."
                : $"Download {download.Name}{size}.");
        }

        foreach (var runtime in plan.ReusedRuntimes)
        {
            lines.Add($"I will reuse the {runtime} I already set up.");
        }

        if (plan.IntegrationAlreadyDownloaded)
        {
            lines.Add("It was downloaded before and is still here, so nothing of it is downloaded again.");
        }

        if (candidate.Source.DependencyCount is > 0 and var dependencies)
        {
            lines.Add($"It uses {dependencies.ToString(CultureInfo.InvariantCulture)} other packages, which are fetched when it is installed.");
        }

        if (candidate.Runtime == CandidateRuntime.Unknown && candidate.Source.Kind == InstallSourceKind.Bundle)
        {
            var node = RuntimeCatalog.For(RuntimeKind.NodeJs);
            lines.Add(node is null
                ? "I cannot tell what the bundle needs to run until it is downloaded."
                : $"It may also need Node.js, which I would then download too (about {node.ApproximateMegabytes.ToString(CultureInfo.InvariantCulture)} MB).");
        }

        lines.Add("Nothing is run until you click Install, and then only to check that it starts.");
        return lines;
    }

    private static List<string> Notes(InstallCandidate candidate) =>
        [.. candidate.Notes
            .Where(note => note.Severity == ReviewSeverity.Caution && !SaidElsewhere.Contains(note.Code))
            .Select(note => note.Text)
            .Distinct(StringComparer.Ordinal)
            .Take(6)];

    // A text from the web, set off in quotation marks so that it cannot pass for the Assistant's own words.
    private static string Quote(string text, int maxLength) => "“" + Plain(text, maxLength).Replace("“", string.Empty, StringComparison.Ordinal).Replace("”", string.Empty, StringComparison.Ordinal) + "”";

    // One line, cut, without a mark that anything could read as formatting or a link.
    private static string Plain(string text, int maxLength) => (CandidateText.Line(text, maxLength) ?? string.Empty).Replace('<', ' ').Replace('>', ' ');
}
