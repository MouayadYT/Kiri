using System.Text.RegularExpressions;

namespace Assistant.Tools.Integrations;

/// <summary>
/// What the review checks of a candidate that needs no network (PROJECT_SPEC §4.8, step 107): whether its own description of itself is well formed,
/// whether anything shows that it is an MCP server, whether it offers what was asked, how it can be installed, and how its licence, upkeep and maker
/// read. Every finding's words are the Assistant's own: nothing a candidate says is repeated in one, so nothing from the web can pass for the
/// Assistant speaking or for an instruction.
/// </summary>
internal static partial class ReviewRules
{
    private const int MaxNameLength = 200;

    // npm: an optional @scope/, then lower-case letters, digits and . _ ~ -; a version spec, an address or a path is not a name.
    [GeneratedRegex(@"^(?:@[a-z0-9][a-z0-9._~-]*/)?[a-z0-9][a-z0-9._~-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NpmName();

    // PyPI: letters, digits and . _ - , starting and ending with a letter or digit.
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex PyPiName();

    // MCP as a word, or the protocol's name.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])mcp(?![\p{L}\p{N}])|model[\s-]*context[\s-]*protocol", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex McpWords();

    // A version a package has: numbers first, then anything a version may hold. A range, a tag or a wildcard is not one.
    [GeneratedRegex(@"^\d+(?:\.\d+){0,3}(?:[-+.][0-9A-Za-z.+-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersion();

    // Licences the Assistant recognises as open source (SPDX identifiers).
    private static readonly HashSet<string> OpenLicenses = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIT", "MIT-0", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "MPL-2.0", "GPL-2.0", "GPL-2.0-only", "GPL-2.0-or-later", "GPL-3.0",
        "GPL-3.0-only", "GPL-3.0-or-later", "LGPL-2.1", "LGPL-2.1-only", "LGPL-2.1-or-later", "LGPL-3.0", "LGPL-3.0-only", "LGPL-3.0-or-later",
        "AGPL-3.0", "AGPL-3.0-only", "AGPL-3.0-or-later", "Unlicense", "CC0-1.0", "0BSD", "Zlib", "BSL-1.0", "EPL-2.0", "PSF-2.0", "Python-2.0",
        "Artistic-2.0", "BlueOak-1.0.0", "CC-BY-4.0",
    };

    /// <summary>
    /// The name an integration found for a need that names no app is shown and installed under (step 116): the candidate's own name made readable (<c>io.github.acme/google-calendar-mcp</c> is
    /// <c>Google Calendar</c>): what follows the last slash, dashes, underscores and dots as spaces, the words MCP and server left off the end, each word in capitals. <paramref name="fallback"/>
    /// when nothing readable is left.
    /// </summary>
    public static string AppNameFrom(string? candidateName, string fallback)
    {
        var tail = (candidateName ?? string.Empty).Split('/').LastOrDefault() ?? string.Empty;
        var words = tail.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.All(character => char.IsLetterOrDigit(character)))
            .ToList();
        while (words.Count > 1 && words[^1].ToLowerInvariant() is "mcp" or "server")
        {
            words.RemoveAt(words.Count - 1);
        }

        if (words.Count > 0 && words[0].ToLowerInvariant() is "mcp" && words.Count > 1)
        {
            words.RemoveAt(0);
        }

        var name = string.Join(' ', words.Take(5).Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        return name.Length is >= 2 and <= 60 ? name : fallback;
    }

    /// <summary>Whether <paramref name="name"/> is an npm package name and not an address, a path, a version spec or an alias.</summary>
    public static bool IsNpmName(string? name) => name is { Length: > 0 and <= 214 } && NpmName().IsMatch(name);

    /// <summary>Whether <paramref name="name"/> is a PyPI project name.</summary>
    public static bool IsPyPiName(string? name) => name is { Length: > 0 and <= 100 } && PyPiName().IsMatch(name);

    /// <summary>Whether <paramref name="version"/> is one exact version.</summary>
    public static bool IsExactVersion(string? version) => version is { Length: > 0 and <= 64 } && ExactVersion().IsMatch(version);

    /// <summary>
    /// Whether the candidate's own description of itself is well formed. A candidate from the Integration Finder was cleaned already; the review
    /// does not rely on that. Adds a blocker for each thing wrong and returns whether there was none.
    /// </summary>
    public static bool WellFormed(IntegrationCandidate candidate, List<ReviewFinding> findings)
    {
        var before = findings.Count;
        if (candidate is null)
        {
            findings.Add(Block(ReviewCode.Malformed, "There is no candidate to review."));
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate.Name) || candidate.Name.Length > MaxNameLength || candidate.Name.Any(char.IsControl))
        {
            findings.Add(Block(ReviewCode.Malformed, "Its name is missing or is not a name."));
        }

        if (CandidateText.Https(candidate.SourceUrl) is null)
        {
            findings.Add(Block(ReviewCode.Malformed, "It does not say where it comes from with a secure web address."));
        }

        if (candidate.RepositoryUrl is not null && CandidateText.Https(candidate.RepositoryUrl) is null)
        {
            findings.Add(Block(ReviewCode.Malformed, "The address of its source code is not a secure web address."));
        }

        if (candidate.RemoteUrl is not null && CandidateText.Https(candidate.RemoteUrl) is null)
        {
            findings.Add(Block(ReviewCode.Malformed, "The address of its hosted server is not a secure web address."));
        }

        if (HasControlCharacters(candidate.Publisher) || HasControlCharacters(candidate.Description) || HasControlCharacters(candidate.License)
            || HasControlCharacters(candidate.Version) || candidate.ToolNames.Any(HasControlCharacters) || candidate.RequiredSecrets.Any(HasControlCharacters))
        {
            findings.Add(Block(ReviewCode.Malformed, "Something it says about itself has characters in it that text does not have."));
        }

        if (candidate.Packages is null || candidate.Packages.Any(package => package is null || string.IsNullOrWhiteSpace(package.Identifier)
                || package.Identifier.Length > 300 || package.Identifier.Any(char.IsControl) || !Enum.IsDefined(package.Method)))
        {
            findings.Add(Block(ReviewCode.Malformed, "A way of installing it is not described properly."));
        }

        return findings.Count == before;
    }

    private static bool HasControlCharacters(string? text) => text is not null && text.Any(character => char.IsControl(character));

    /// <summary>What shows that the candidate is an MCP server.</summary>
    public static McpServerEvidence McpEvidenceOf(IntegrationCandidate candidate)
    {
        var evidence = McpServerEvidence.None;
        if (candidate.FoundIn.Contains("mcp-registry", StringComparer.Ordinal))
        {
            evidence |= McpServerEvidence.ListedInRegistry;
        }

        if (candidate.ToolNames.Count > 0)
        {
            evidence |= McpServerEvidence.ToolsListed;
        }

        if (McpWords().IsMatch(candidate.Name ?? string.Empty) || McpWords().IsMatch(candidate.Description ?? string.Empty)
            || McpWords().IsMatch(candidate.RepositoryUrl ?? string.Empty))
        {
            evidence |= McpServerEvidence.Described;
        }

        return evidence;
    }

    /// <summary>
    /// Checks that the candidate offers what was asked: a blocker when it lists its tools and none fits, a caution when nothing says either way or only
    /// its description says it can. Returns the tools that fit.
    /// </summary>
    public static IReadOnlyList<string> CheckCapability(IntegrationCandidate candidate, IntegrationCapability capability, List<ReviewFinding> findings)
    {
        var tools = CapabilityMatcher.Match(capability, [.. candidate.ToolNames.Select(tool => new ToolFacts(tool))]);
        var wanted = IntegrationReplyWriter.Wanted(capability);
        if (tools.Count > 0)
        {
            findings.Add(Info(ReviewCode.Fact, $"It lists a tool for this: it can {wanted}."));
            return tools;
        }

        if (candidate.ToolNames.Count > 0)
        {
            findings.Add(Block(ReviewCode.CapabilityMissing, $"It lists {candidate.ToolNames.Count} tools and none of them can {wanted}."));
            return [];
        }

        var model = candidate.Assessment switch
        {
            CandidateAssessment.Supports => " My local model judged that it fits.",
            CandidateAssessment.DoesNotSupport => " My local model judged that it may not fit.",
            _ => string.Empty,
        };
        if (candidate.Evidence >= CapabilityEvidence.Described)
        {
            findings.Add(Caution(ReviewCode.CapabilityDescribed, $"Its description says it can {wanted}, but it does not list its tools, so I could not confirm it.{model}"));
        }
        else
        {
            findings.Add(Caution(ReviewCode.CapabilityUnconfirmed, $"Nothing it publishes says whether it can {wanted}. I will check its tools after it is installed.{model}"));
        }

        return [];
    }

    /// <summary>
    /// The ways the candidate can be installed that the Assistant supports, in the order they are tried: a bundle, an npm package, a PyPI package,
    /// then a hosted server. Adds a blocker for each way that is not supported and why, when there is no supported one.
    /// </summary>
    public static List<CandidatePackage> InstallOptions(IntegrationCandidate candidate, List<ReviewFinding> findings)
    {
        var supported = new List<CandidatePackage>();
        var problems = new List<ReviewFinding>();
        foreach (var package in candidate.Packages)
        {
            switch (package.Method)
            {
                case CandidateInstallMethod.Npm when !IsNpmName(package.Identifier):
                case CandidateInstallMethod.PyPi when !IsPyPiName(package.Identifier):
                    problems.Add(Block(ReviewCode.UnsafePackageSource, "A package is named in a way that is not a registry name (an address, a path or a version), so I will not install from it."));
                    break;
                case CandidateInstallMethod.Npm or CandidateInstallMethod.PyPi or CandidateInstallMethod.Bundle:
                    supported.Add(package);
                    break;
                case CandidateInstallMethod.Remote:
                    if (CandidateText.Https(package.Identifier) is not null)
                    {
                        supported.Add(package);
                    }
                    else
                    {
                        problems.Add(Block(ReviewCode.Malformed, "The address of its hosted server is not a secure web address."));
                    }

                    break;
                case CandidateInstallMethod.Container:
                    problems.Add(Block(ReviewCode.UnsupportedInstall, "It is only published as a container image, which needs Docker, and I cannot set that up safely for you."));
                    break;
                case CandidateInstallMethod.NuGet:
                    problems.Add(Block(ReviewCode.UnsupportedInstall, "It is only published as a .NET package, which I cannot install yet."));
                    break;
                default:
                    problems.Add(Block(ReviewCode.UnsupportedInstall, "Only its source code is published. I would have to build it first, and building code from the internet runs whatever it says, so I do not."));
                    break;
            }
        }

        if (candidate.RemoteUrl is not null && CandidateText.Https(candidate.RemoteUrl) is { } remote
            && !supported.Any(package => package.Method == CandidateInstallMethod.Remote))
        {
            supported.Add(new CandidatePackage(CandidateInstallMethod.Remote, remote, null));
        }

        if (candidate.Packages.Count == 0 && supported.Count == 0)
        {
            problems.Add(Block(ReviewCode.UnsupportedInstall, "It does not say how it is installed."));
        }

        if (supported.Count == 0)
        {
            findings.AddRange(problems.DistinctBy(problem => problem.Text));
        }

        return [.. supported.OrderBy(package => package.Method switch
        {
            CandidateInstallMethod.Bundle => 0,
            CandidateInstallMethod.Npm => 1,
            CandidateInstallMethod.PyPi => 2,
            _ => 3,
        })];
    }

    /// <summary>How a licence reads.</summary>
    public static LicenseStatus LicenseOf(string? license)
    {
        if (string.IsNullOrWhiteSpace(license))
        {
            return LicenseStatus.Missing;
        }

        if (license.Contains("unlicensed", StringComparison.OrdinalIgnoreCase) || license.Contains("see license", StringComparison.OrdinalIgnoreCase)
            || license.Contains("proprietary", StringComparison.OrdinalIgnoreCase))
        {
            return LicenseStatus.Unrecognized;
        }

        // An SPDX expression: identifiers joined by AND and OR, with brackets.
        var identifiers = license.Replace("(", " ", StringComparison.Ordinal).Replace(")", " ", StringComparison.Ordinal)
            .Split([' '], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !token.Equals("OR", StringComparison.OrdinalIgnoreCase) && !token.Equals("AND", StringComparison.OrdinalIgnoreCase)
                && !token.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return identifiers.Count > 0 && identifiers.All(OpenLicenses.Contains) ? LicenseStatus.Open : LicenseStatus.Unrecognized;
    }

    /// <summary>How recently the project was changed.</summary>
    public static UpkeepStatus ActivityOf(DateTimeOffset? lastActivity, DateTimeOffset now)
    {
        if (lastActivity is not { } at)
        {
            return UpkeepStatus.Unknown;
        }

        var age = now - at;
        return age <= TimeSpan.FromDays(365) ? UpkeepStatus.Recent : age <= TimeSpan.FromDays(730) ? UpkeepStatus.Aging : UpkeepStatus.Stale;
    }

    /// <summary>What the review says of who made the candidate.</summary>
    public static ReviewFinding TrustFinding(CandidateTrust trust, string app) => trust switch
    {
        CandidateTrust.VerifiedVendor => Info(ReviewCode.Fact, $"It is published under an account that is {app}'s own, so it is official."),
        CandidateTrust.ClaimsOfficial => Caution(ReviewCode.PublisherUnverified, $"It says it is official, but nothing I can check shows that {app}'s maker published it."),
        CandidateTrust.Community => Info(ReviewCode.Fact, $"It was made by someone other than {app}'s maker, as far as I can tell."),
        _ => Caution(ReviewCode.PublisherUnverified, "I could not tell who made it."),
    };

    /// <summary>A blocker.</summary>
    public static ReviewFinding Block(ReviewCode code, string text) => new(ReviewSeverity.Blocker, code, text);

    /// <summary>A caution.</summary>
    public static ReviewFinding Caution(ReviewCode code, string text) => new(ReviewSeverity.Caution, code, text);

    /// <summary>A fact.</summary>
    public static ReviewFinding Info(ReviewCode code, string text) => new(ReviewSeverity.Info, code, text);

    /// <summary>The id an integration for <paramref name="appKey"/> is installed under: the app's key, which is letters and digits, starting with a letter, and not one that is taken.</summary>
    public static string IdFor(string appKey, IReadOnlyCollection<string> taken)
    {
        var letters = new string([.. (appKey ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant)]);
        if (letters.Length == 0 || !char.IsAsciiLetter(letters[0]))
        {
            letters = "app" + letters;
        }

        var stem = letters.Length > IntegrationRules.MaxIdLength - 3 ? letters[..(IntegrationRules.MaxIdLength - 3)] : letters;
        var id = stem;
        for (var number = 2; taken.Contains(id, StringComparer.Ordinal); number++)
        {
            id = stem + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return id;
    }
}
