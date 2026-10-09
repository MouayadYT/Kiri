using System.Globalization;
using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Tools.Integrations;

/// <summary>
/// Writes the Assistant's own answer to a request for an external app that it cannot serve now (PROJECT_SPEC §4.8, steps 105-106). The words are the Assistant's, built
/// from facts it checked: what was resolved and what the finder found. Every text that came from the web goes into the answer cleaned and
/// inside inline code, so that nothing a candidate says can be read as the Assistant speaking, as a link with other words on it, or as an
/// instruction. It says plainly what was and was not done: nothing was installed, downloaded, run or sent but the app's name and what was wanted.
/// </summary>
internal static class IntegrationReplyWriter
{
    // The answer is a chat message: the best few are listed, however many the finder kept.
    private const int MaxShown = 3;

    /// <summary>
    /// The answer to a resolution that is not one for the model to answer. <paramref name="discovery"/> is the finder's result when the finder was asked,
    /// <paramref name="reviews"/> the review of each of its candidates (in the same order) when they were reviewed, and <paramref name="offer"/> the offer made
    /// for the one that passed, when there is one.
    /// </summary>
    public static ConnectedAppReply? Write(
        IntegrationResolution resolution,
        IntegrationDiscoveryResult? discovery,
        DateTimeOffset now,
        IReadOnlyList<CandidateReview>? reviews = null,
        IntegrationOffer? offer = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Need is not { } need)
        {
            return null;
        }

        var app = Plain(need.AppName, 60);
        var wanted = Wanted(need.Capability);

        // A need for a kind of thing and not for a named app (step 116) is worded without "in <app>": "I can't read your events yet: no calendar integration is installed."
        var inApp = need.IsForAnyApp ? string.Empty : $" in {app}";
        switch (resolution.Kind)
        {
            case IntegrationResolutionKind.Refused:
                return new ConnectedAppReply(
                    $"I don't delete things in connected apps, so I won't do that in {app}. You can do it in {app} yourself.", ConnectedAppReplyKind.Refused);

            case IntegrationResolutionKind.AvailableLocally:
                return new ConnectedAppReply(AvailableLocally(app, wanted, inApp, resolution.Available), ConnectedAppReplyKind.FoundOnThisPc);

            case IntegrationResolutionKind.InstalledNotUsable when resolution.Problem is { } problem && problem != InstalledProblem.LacksCapability:
                return new ConnectedAppReply($"I can't {wanted}{inApp} right now: {Problem(app, problem)}", ConnectedAppReplyKind.InstalledNotUsable);

            case IntegrationResolutionKind.InstalledNotUsable:
            case IntegrationResolutionKind.NotInstalled:
                if (discovery is null)
                {
                    return null;
                }

                var installed = resolution.Kind == IntegrationResolutionKind.InstalledNotUsable;
                var lead = installed
                    ? need.IsForAnyApp ? $"The integrations you have installed don't offer a way to {wanted}." : $"The {app} integration you have installed doesn't offer a way to {wanted}."
                    : $"I can't {wanted}{inApp} yet: no {app} integration is installed.";
                return Discovery(lead, app, wanted, need, discovery, now, reviews, offer, installed);

            default:
                return null;
        }
    }

    /// <summary>
    /// What the Assistant says when the user does not install the integration it offered (step 110): that it did not, so it did not do what was asked. The
    /// request is not carried out and nothing is said of what the user wrote, only the app and the two words of the capability. <paramref name="another"/>
    /// is for an app that has an integration installed already, which has nothing for this.
    /// </summary>
    public static string NotInstalled(IntegrationNeed need, bool another)
    {
        ArgumentNullException.ThrowIfNull(need);
        var app = Plain(need.AppName, 60);
        if (need.IsForAnyApp)
        {
            return $"I didn't install {Article(app)} {app} integration, so I didn't {Wanted(need.Capability)}. Ask me again whenever you want to set it up.";
        }

        var which = another ? "another " + app : "the " + app;
        return $"I didn't install {which} integration, so I didn't {Wanted(need.Capability)} in {app}. Ask me again whenever you want to set it up.";
    }

    /// <summary>
    /// What the Assistant says when an app whose server it knows is not connected, or needs a sign-in again: that it can connect it, what happens when the user
    /// agrees, and that the request goes on by itself afterwards. The offer carries the details.
    /// </summary>
    public static ConnectedAppReply ConnectOffer(IntegrationNeed need, KnownEndpoint endpoint, IntegrationOffer offer, bool signIn)
    {
        ArgumentNullException.ThrowIfNull(need);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(offer);
        var app = Plain(endpoint.Name, 60);
        var wanted = Wanted(need.Capability);
        var lead = signIn ? $"I can't {wanted} in {app} right now: you need to sign in to {app} again." : $"I can't {wanted} in {app} yet: {app} isn't connected.";
        var how = endpoint.IsBundled
            ? $"It uses a small program that comes with the Assistant, which talks to {app} over the internet."
            : endpoint.RunsOnThisPc
                ? $"It is {app}'s own program on this PC, so nothing goes over the internet."
                : $"It uses {app}'s own server, so what you ask is sent to {app} over the internet.";
        return new ConnectedAppReply(
            $"{lead} I can {(signIn ? "sign you in" : "connect it")} now. When you click {(signIn ? "Sign in" : "Connect")} below, your browser opens on {app}'s own page, where you choose to allow the Assistant; I never see your password. {how} "
            + "Then I carry on with what you asked.",
            ConnectedAppReplyKind.ConnectOffered,
            offer,
            NotConnected(need, signIn));
    }

    /// <summary>What the Assistant says when the user does not connect the app it offered to (or does not sign in): that it did not, so the request was not carried out.</summary>
    public static string NotConnected(IntegrationNeed need, bool signIn)
    {
        ArgumentNullException.ThrowIfNull(need);
        var app = Plain(need.AppName, 60);
        return $"I didn't {(signIn ? "sign in to" : "connect")} {app}, so I didn't {Wanted(need.Capability)} in {app}. Ask me again whenever you want to set it up.";
    }

    /// <summary>What the Assistant says when the app is reached over the internet and Local Only mode keeps it from being connected.</summary>
    public static ConnectedAppReply ConnectBlockedByLocalOnly(IntegrationNeed need, KnownEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(need);
        ArgumentNullException.ThrowIfNull(endpoint);
        var app = Plain(endpoint.Name, 60);
        return new ConnectedAppReply(
            $"I can't {Wanted(need.Capability)} in {app}: {app} is reached over the internet, and Local Only mode is on. You can turn Local Only off in Settings > Privacy, and then ask me again.",
            ConnectedAppReplyKind.DiscoveryBlocked);
    }

    /// <summary>What the Assistant says when the user stopped it while it looked for an integration (step 110): nothing was installed and nothing was done.</summary>
    public static string LookupStopped(IntegrationNeed need)
    {
        ArgumentNullException.ThrowIfNull(need);
        var app = Plain(need.AppName, 60);
        var inApp = need.IsForAnyApp ? string.Empty : $" in {app}";
        return $"I stopped looking for {Article(app)} {app} integration, so I didn't install one and I didn't {Wanted(need.Capability)}{inApp}. Ask me again whenever you want me to look.";
    }

    // The thing wanted, as a person would say it: "create a task".
    internal static string Wanted(IntegrationCapability capability)
    {
        if (capability.Object is not { } obj)
        {
            return "do that";
        }

        var plural = obj.EndsWith('s') ? obj : obj + "s";
        return capability.Action switch
        {
            CapabilityAction.Create => $"create {Article(obj)} {obj}",
            CapabilityAction.Read => $"read your {plural}",
            CapabilityAction.Search => $"search your {plural}",
            CapabilityAction.Update => $"change {Article(obj)} {obj}",
            CapabilityAction.Complete => $"complete {Article(obj)} {obj}",
            CapabilityAction.Send => $"send {Article(obj)} {obj}",
            _ => "do that",
        };
    }

    private static string Article(string noun) => "aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "an" : "a";

    private static string Problem(string app, InstalledProblem problem) => problem switch
    {
        InstalledProblem.Disabled => $"the {app} integration is installed but turned off, and I can't turn it on for you yet.",
        InstalledProblem.NeedsSignIn => $"the {app} integration needs you to sign in, or to give it its key. You can do that in Settings > Integrations.",
        InstalledProblem.Unreachable => $"I couldn't reach the {app} integration. Try again in a minute.",
        InstalledProblem.Incompatible => $"the {app} integration speaks a version of the protocol that I don't understand yet.",
        InstalledProblem.BlockedByLocalOnly => $"the {app} integration works over the internet, and Local Only mode is on. You can turn Local Only off in Settings > Privacy.",
        InstalledProblem.PermissionOff => $"a permission the {app} integration needs is turned off. You can turn it on in Settings > Permissions.",
        InstalledProblem.NetworkOff => $"you turned off network access for the {app} integration. You can turn it on in Settings > Integrations.",
        InstalledProblem.AccountAccessOff => $"you turned off account access for the {app} integration, so it can't sign in. You can turn it on in Settings > Integrations.",
        InstalledProblem.AccessOff => $"you turned off both reading and changing things for the {app} integration. You can turn them on in Settings > Integrations.",
        _ => $"the {app} integration can't be used.",
    };

    private static string AvailableLocally(string app, string wanted, string inApp, IReadOnlyList<AvailableIntegration> available)
    {
        var shown = available.Take(3).Select(entry => $"`{Code(entry.Name, 60)}` in {Plain(entry.Where, 30)}");
        return $"I can't {wanted}{inApp} yet. A server for it is already set up on this PC ({string.Join(", ", shown)}), but not in the Assistant, "
            + "and I can't import it yet. I haven't used it, and I haven't looked for another.";
    }

    private static ConnectedAppReply Discovery(
        string lead,
        string app,
        string wanted,
        IntegrationNeed need,
        IntegrationDiscoveryResult discovery,
        DateTimeOffset now,
        IReadOnlyList<CandidateReview>? reviews,
        IntegrationOffer? offer,
        bool anotherThanInstalled)
    {
        switch (discovery.Status)
        {
            case DiscoveryStatus.Blocked:
                var why = discovery.Block switch
                {
                    DiscoveryBlock.LocalOnly =>
                        "Local Only mode is on. If you turn it off in Settings > Privacy and allow External Web and Image Search in Settings > Permissions, I can look.",
                    DiscoveryBlock.NotAllowedNow => "You did not allow External Web and Image Search this time, so I did not look. If you ask again, I will ask you again.",
                    _ => "External Web and Image Search is off in Settings > Permissions. If you allow it, I can look.",
                };
                return new ConnectedAppReply(
                    $"{lead} To look for one I would have to search the web. {why} Only the app's name and what you want to do would be sent, never your text.",
                    ConnectedAppReplyKind.DiscoveryBlocked);

            case DiscoveryStatus.Failed:
                return new ConnectedAppReply(
                    $"{lead} I tried to look for one, but I couldn't reach {Places(discovery.SourcesFailed)}. Try again later.", ConnectedAppReplyKind.DiscoveryFailed);

            case DiscoveryStatus.NothingPlausible:
                var searched = discovery.SourcesAnswered.Count > 0 ? $" ({Places(discovery.SourcesAnswered)})" : string.Empty;
                var earlier = discovery.FromCache ? " This is from a search I made earlier." : string.Empty;
                return new ConnectedAppReply(
                    $"{lead} I looked{searched} but found no integration that fits.{earlier}", ConnectedAppReplyKind.DiscoveryEmpty);

            default:
                if (reviews is { Count: > 0 } && reviews.Count == discovery.Candidates.Count)
                {
                    return new ConnectedAppReply(
                        Reviewed(lead, discovery, reviews, offer),
                        offer is not null ? ConnectedAppReplyKind.InstallOffered : ConnectedAppReplyKind.NothingPassedReview,
                        offer,
                        offer is not null ? NotInstalled(need, anotherThanInstalled) : null);
                }

                return new ConnectedAppReply(Found(lead, app, need, discovery, now), ConnectedAppReplyKind.DiscoveryFound);
        }
    }

    // What the finder found, after each candidate was reviewed: the one that passed is below the answer for the user to approve, and what did not pass says why.
    private static string Reviewed(string lead, IntegrationDiscoveryResult discovery, IReadOnlyList<CandidateReview> reviews, IntegrationOffer? offer)
    {
        var text = new StringBuilder();
        text.Append(lead).Append(" I looked on the web (").Append(Places(discovery.SourcesAnswered)).Append(") and found ");
        text.Append(discovery.Candidates.Count == 1 ? "1 possible integration" : $"{discovery.Candidates.Count} possible integrations");
        text.Append(discovery.FromCache ? " in a search I made earlier" : string.Empty);
        text.Append(" and checked ").Append(discovery.Candidates.Count == 1 ? "it. " : "each one. ");
        var ruledOut = new List<string>();
        for (var index = 0; index < reviews.Count; index++)
        {
            if (!reviews[index].IsAccepted)
            {
                var reason = reviews[index].Blockers.FirstOrDefault()?.Text ?? "It did not pass.";
                ruledOut.Add($"`{Code(discovery.Candidates[index].Name, 100)}`: {reason}");
            }
        }

        if (offer is not null)
        {
            text.Append($"`{Code(offer.IntegrationName, 120)}` passed my checks, so I have put it below for you to look over.");
            if (ruledOut.Count > 0)
            {
                text.Append(ruledOut.Count == 1 ? " I ruled out 1 other:" : $" I ruled out {ruledOut.Count} others:");
                text.Append("\n\n");
                foreach (var line in ruledOut.Take(MaxShown))
                {
                    text.Append("- ").Append(line).Append('\n');
                }
            }
        }
        else
        {
            text.Append("None of them passed my checks:\n\n");
            foreach (var line in ruledOut.Take(MaxShown))
            {
                text.Append("- ").Append(line).Append('\n');
            }
        }

        text.Append(offer is not null
            ? "\nNothing has been downloaded, installed or run yet, and nothing was sent but the app's name and what you want to do. I only install it if you click Install. Then I carry on with what you asked."
            : "\nNothing was downloaded, installed or run, and nothing was sent but the app's name and what you want to do.");
        return text.ToString();
    }

    private static string Found(string lead, string app, IntegrationNeed need, IntegrationDiscoveryResult discovery, DateTimeOffset now)
    {
        var shown = discovery.Candidates.Take(MaxShown).ToList();
        var text = new StringBuilder();
        text.Append(lead).Append(" I looked on the web (").Append(Places(discovery.SourcesAnswered)).Append(") and found ");
        text.Append(discovery.Candidates.Count == 1 ? "1 possible integration" : $"{discovery.Candidates.Count} possible integrations");
        text.Append(discovery.FromCache ? " in a search I made earlier" : string.Empty);
        text.Append(shown.Count < discovery.Candidates.Count ? $". These are the best {shown.Count}:" : ":").Append("\n\n");
        for (var index = 0; index < shown.Count; index++)
        {
            Candidate(text, index + 1, shown[index], app, need, now);
        }

        text.Append("\nI haven't reviewed them, and I haven't downloaded, installed or run anything. These are only leads, and nothing was sent but the app's name and what you want to do.");
        return text.ToString();
    }

    private static void Candidate(StringBuilder text, int number, IntegrationCandidate candidate, string app, IntegrationNeed need, DateTimeOffset now)
    {
        var publisher = candidate.Publisher is { } name ? $" (`{Code(name, 60)}`)" : string.Empty;
        var trust = candidate.Trust switch
        {
            CandidateTrust.VerifiedVendor => $"made by {app}'s maker{publisher}",
            CandidateTrust.ClaimsOfficial => $"says it's official, but I couldn't confirm that{publisher}",
            CandidateTrust.Community => $"community-made{publisher}",
            _ => "publisher unknown",
        };
        var facts = new List<string> { trust, candidate.License is { } license ? $"{Plain(license, 40)} licence" : "no licence listed" };
        if (candidate.LastActivity is { } at)
        {
            facts.Add(Updated(now - at));
        }

        text.Append(number).Append(". `").Append(Code(candidate.Name, 120)).Append("` - ").Append(string.Join(", ", facts)).Append('\n');

        var details = new List<string> { Evidence(candidate, need) };
        if (Install(candidate) is { } install)
        {
            details.Add(install);
        }

        if (candidate.RequiredSecrets.Count > 0)
        {
            details.Add("Asks for " + string.Join(", ", candidate.RequiredSecrets.Take(4).Select(secret => $"`{Code(secret, 64)}`")) + ".");
        }

        // What was looked at, so that it can be told from what the project becomes later.
        var seen = new List<string>();
        if (candidate.Version is { } version)
        {
            seen.Add("version " + Plain(version, 40));
        }

        if (candidate.CommitSha is { Length: >= 7 } sha)
        {
            seen.Add("commit " + sha[..7]);
        }

        if (seen.Count > 0)
        {
            details.Add("Looked at " + string.Join(", ", seen) + ".");
        }

        text.Append("   ").Append(string.Join(' ', details)).Append('\n');
        text.Append("   ").Append(candidate.SourceUrl).Append('\n');
    }

    private static string Evidence(IntegrationCandidate candidate, IntegrationNeed need)
    {
        var best = CapabilityMatcher.Match(need.Capability, [.. candidate.ToolNames.Select(tool => new ToolFacts(tool))]).FirstOrDefault();
        var text = candidate.Evidence switch
        {
            CapabilityEvidence.ToolListed when best is not null => $"Lists a tool for this: `{Code(best, 64)}`.",
            CapabilityEvidence.ToolListed or CapabilityEvidence.Described => "Its description mentions this.",
            _ => "I couldn't confirm that it can do this.",
        };
        return candidate.Assessment switch
        {
            CandidateAssessment.Supports => text + " My local model judged that it fits.",
            CandidateAssessment.DoesNotSupport => text + " My local model judged that it may not fit.",
            _ => text,
        };
    }

    private static string? Install(IntegrationCandidate candidate)
    {
        var ways = candidate.Packages.Select(package => package.Method).Distinct().Take(2).Select(method => method switch
        {
            CandidateInstallMethod.Remote => "a server the maker hosts, reached over the internet",
            CandidateInstallMethod.Npm => "an npm package (needs Node.js)",
            CandidateInstallMethod.PyPi => "a Python package",
            CandidateInstallMethod.Container => "a container image (needs Docker)",
            CandidateInstallMethod.NuGet => "a .NET package",
            CandidateInstallMethod.Bundle => "an MCP bundle",
            _ => candidate.Runtime switch
            {
                CandidateRuntime.NodeJs => "source code that runs on Node.js",
                CandidateRuntime.Python => "source code that runs on Python",
                CandidateRuntime.DotNet => "source code that runs on .NET",
                _ => "source code only",
            },
        }).ToList();
        return ways.Count == 0 ? null : "It comes as " + string.Join(" or ", ways) + ".";
    }

    private static string Updated(TimeSpan age)
    {
        if (age < TimeSpan.Zero || age.TotalDays < 1)
        {
            return "updated today";
        }

        if (age.TotalDays < 60)
        {
            var days = (int)age.TotalDays;
            return days == 1 ? "updated yesterday" : $"updated {days.ToString(CultureInfo.InvariantCulture)} days ago";
        }

        if (age.TotalDays < 730)
        {
            return $"updated {((int)(age.TotalDays / 30)).ToString(CultureInfo.InvariantCulture)} months ago";
        }

        return "not updated for over two years";
    }

    private static string Places(IReadOnlyList<string> ids)
    {
        var names = ids.Select(id => id switch
        {
            "mcp-registry" => "the MCP registry",
            "github" => "GitHub",
            "npm" => "npm",
            "pypi" => "PyPI",
            _ => "a registry",
        }).Distinct().ToList();
        return names.Count switch
        {
            0 => "the places that list integrations",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    // A text from the web for the inside of inline code: no backtick can end the code, and no line break.
    private static string Code(string text, int maxLength) => (CandidateText.Line(text.Replace('`', '\''), maxLength) ?? string.Empty);

    // A text for the running words: no mark that Markdown would read.
    private static string Plain(string text, int maxLength)
    {
        var line = CandidateText.Line(text, maxLength) ?? string.Empty;
        var builder = new StringBuilder(line.Length);
        foreach (var character in line)
        {
            builder.Append(character is '*' or '_' or '~' or '`' or '[' or ']' or '<' or '>' or '\\' or '|' ? ' ' : character);
        }

        return builder.ToString().Trim();
    }
}
