using System.Text.Json;

namespace Assistant.Tools.Integrations;

/// <summary>What the npm registry says about one version of a package (PROJECT_SPEC §4.8, step 107). Facts only: nothing here was downloaded or run.</summary>
internal sealed record NpmFacts
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public string? TarballUrl { get; init; }

    /// <summary>The package's SHA-512, as the registry publishes it, in hexadecimal.</summary>
    public ContentHash? Hash { get; init; }

    public long? UnpackedSize { get; init; }

    /// <summary>The programs the package provides (<c>bin</c>): name, then the file's path inside the package.</summary>
    public IReadOnlyDictionary<string, string> Bin { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether it has a <c>preinstall</c>, <c>install</c> or <c>postinstall</c> script, which npm would run when installing it.</summary>
    public bool HasInstallScripts { get; init; }

    public int DependencyCount { get; init; }

    public string? NodeConstraint { get; init; }

    public string? License { get; init; }

    public string? RepositoryUrl { get; init; }

    public string? GitHead { get; init; }

    /// <summary>The author's reason for deprecating this version, when they did.</summary>
    public string? Deprecated { get; init; }

    public string? Description { get; init; }
}

/// <summary>The file of a PyPI release that would be installed.</summary>
/// <param name="FileName">Its name.</param>
/// <param name="Url">Where it is downloaded from.</param>
/// <param name="Sha256">Its SHA-256 as PyPI publishes it.</param>
/// <param name="SizeBytes">Its size.</param>
internal sealed record PyPiWheel(string FileName, string Url, string Sha256, long SizeBytes);

/// <summary>What PyPI says about one release of a project (PROJECT_SPEC §4.8, step 107). Facts only: nothing here was downloaded or run.</summary>
internal sealed record PyPiFacts
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    /// <summary>The wheel the Assistant would install, or <see langword="null"/> when there is none it can (only source, or only for another system).</summary>
    public PyPiWheel? Wheel { get; init; }

    /// <summary>Whether the release has a source package that is not a wheel.</summary>
    public bool HasSourceOnly { get; init; }

    public bool Yanked { get; init; }

    public string? RequiresPython { get; init; }

    public int DependencyCount { get; init; }

    public string? License { get; init; }

    public string? RepositoryUrl { get; init; }

    public string? Summary { get; init; }

    public DateTimeOffset? UploadedAt { get; init; }
}

/// <summary>Reads what the package registries say about a package (PROJECT_SPEC §4.8, step 107).</summary>
internal interface IPackageMetadata
{
    /// <summary>One version of an npm package (<paramref name="version"/> is <see langword="null"/> for the latest), or <see langword="null"/> when there is none.</summary>
    /// <exception cref="DiscoveryException">The registry could not be read.</exception>
    Task<NpmFacts?> GetNpmAsync(string name, string? version, CancellationToken cancellationToken);

    /// <summary>One release of a PyPI project (<paramref name="version"/> is <see langword="null"/> for the latest), or <see langword="null"/> when there is none.</summary>
    /// <exception cref="DiscoveryException">PyPI could not be read.</exception>
    Task<PyPiFacts?> GetPyPiAsync(string name, string? version, CancellationToken cancellationToken);
}

/// <summary>
/// Reads package facts from the npm registry and PyPI through the finder's own fetcher (<see cref="IDiscoveryHttp"/>): the same fixed hosts, <c>https</c>,
/// GET only, no redirect, a byte limit. A text from the registry that is kept is cleaned; what is read is never followed, run or passed on unchecked.
/// </summary>
internal sealed class RegistryPackageMetadata(IDiscoveryHttp http) : IPackageMetadata
{
    private const int MaxBytes = 1024 * 1024;

    /// <inheritdoc/>
    public async Task<NpmFacts?> GetNpmAsync(string name, string? version, CancellationToken cancellationToken)
    {
        var uri = new Uri("https://registry.npmjs.org/" + EscapeNpmName(name) + "/" + Uri.EscapeDataString(version ?? "latest"));
        var response = await http.GetAsync(uri, "application/json", MaxBytes, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return null;
        }

        using var document = WebJson.Parse(response.Body);
        return ReadNpm(document.RootElement);
    }

    /// <inheritdoc/>
    public async Task<PyPiFacts?> GetPyPiAsync(string name, string? version, CancellationToken cancellationToken)
    {
        var path = version is null
            ? $"https://pypi.org/pypi/{Uri.EscapeDataString(name)}/json"
            : $"https://pypi.org/pypi/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}/json";
        var response = await http.GetAsync(new Uri(path), "application/json", MaxBytes, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return null;
        }

        using var document = WebJson.Parse(response.Body);
        return ReadPyPi(document.RootElement);
    }

    // A scoped name is @scope/name, which the registry takes with the slash escaped.
    internal static string EscapeNpmName(string name) => name.Replace("/", "%2F", StringComparison.Ordinal);

    internal static NpmFacts? ReadNpm(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || WebJson.Text(root, "name") is not { } name || WebJson.Text(root, "version") is not { } version)
        {
            return null;
        }

        var dist = WebJson.Member(root, "dist") ?? default;
        var scripts = WebJson.Member(root, "scripts") ?? default;
        var hasScripts = WebJson.Flag(root, "hasInstallScript") == true
            || scripts.ValueKind == JsonValueKind.Object && (scripts.TryGetProperty("preinstall", out _) || scripts.TryGetProperty("install", out _) || scripts.TryGetProperty("postinstall", out _));
        var engines = WebJson.Member(root, "engines") ?? default;
        var repository = WebJson.Member(root, "repository") ?? default;
        var repositoryText = repository.ValueKind == JsonValueKind.String ? repository.GetString() : WebJson.Text(repository, "url");
        var dependencies = WebJson.Member(root, "dependencies") ?? default;
        return new NpmFacts
        {
            Name = name,
            Version = version,
            TarballUrl = CandidateText.Https(WebJson.Text(dist, "tarball")),
            Hash = IntegrityHash(WebJson.Text(dist, "integrity")),
            UnpackedSize = WebJson.Member(dist, "unpackedSize") is { ValueKind: JsonValueKind.Number } size && size.TryGetInt64(out var bytes) ? bytes : null,
            Bin = ReadBin(root, name),
            HasInstallScripts = hasScripts,
            DependencyCount = dependencies.ValueKind == JsonValueKind.Object ? dependencies.EnumerateObject().Count() : 0,
            NodeConstraint = CandidateText.Line(WebJson.Text(engines, "node"), 80),
            License = ReadLicense(root),
            RepositoryUrl = CandidateText.GitHubRepositoryUrl(repositoryText),
            GitHead = CommitId(WebJson.Text(root, "gitHead")),
            Deprecated = WebJson.Member(root, "deprecated") is { ValueKind: JsonValueKind.String } deprecated ? CandidateText.Line(deprecated.GetString(), 120) ?? "deprecated" : null,
            Description = CandidateText.Line(WebJson.Text(root, "description"), 300),
        };
    }

    // The registry's "sha512-<base64>" as the hash it stands for.
    internal static ContentHash? IntegrityHash(string? integrity)
    {
        if (string.IsNullOrWhiteSpace(integrity))
        {
            return null;
        }

        // The value may list several hashes separated by blanks; only a SHA-512 one is accepted.
        foreach (var entry in integrity.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!entry.StartsWith("sha512-", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var bytes = Convert.FromBase64String(entry["sha512-".Length..]);
                if (bytes.Length == 64)
                {
                    return new ContentHash("sha512", Convert.ToHexString(bytes).ToLowerInvariant());
                }
            }
            catch (FormatException)
            {
                // Not base64: not a hash.
            }
        }

        return null;
    }

    private static Dictionary<string, string> ReadBin(JsonElement root, string packageName)
    {
        var bin = new Dictionary<string, string>(StringComparer.Ordinal);
        var element = WebJson.Member(root, "bin") ?? default;
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } single)
        {
            // A single file is the program named for the package, without its scope.
            var bare = packageName.Contains('/', StringComparison.Ordinal) ? packageName[(packageName.IndexOf('/', StringComparison.Ordinal) + 1)..] : packageName;
            bin[bare] = single;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject().Take(8))
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } path)
                {
                    bin[property.Name] = path;
                }
            }
        }

        return bin;
    }

    private static string? ReadLicense(JsonElement root)
    {
        var license = WebJson.Member(root, "license") ?? default;
        var text = license.ValueKind == JsonValueKind.String ? license.GetString() : WebJson.Text(license, "type");
        return CandidateText.Line(text, 60);
    }

    private static string? CommitId(string? text) =>
        text is { Length: 40 } && text.All(char.IsAsciiHexDigit) ? text.ToLowerInvariant() : null;

    internal static PyPiFacts? ReadPyPi(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || WebJson.Member(root, "info") is not { ValueKind: JsonValueKind.Object } info
            || WebJson.Text(info, "name") is not { } name || WebJson.Text(info, "version") is not { } version)
        {
            return null;
        }

        PyPiWheel? wheel = null;
        var sourceOnly = false;
        DateTimeOffset? uploaded = null;
        var allYanked = true;
        foreach (var file in WebJson.Items(root, "urls"))
        {
            var fileYanked = WebJson.Flag(file, "yanked") == true;
            allYanked &= fileYanked;
            var type = WebJson.Text(file, "packagetype");
            var fileName = WebJson.Text(file, "filename");
            uploaded ??= WebJson.Date(file, "upload_time_iso_8601");
            if (type == "sdist")
            {
                sourceOnly = true;
            }

            if (type != "bdist_wheel" || fileYanked || fileName is null || !WheelTags.IsInstallable(fileName)
                || CandidateText.Https(WebJson.Text(file, "url")) is not { } url)
            {
                continue;
            }

            var digests = WebJson.Member(file, "digests") ?? default;
            var sha = WebJson.Text(digests, "sha256")?.ToLowerInvariant();
            if (sha is { Length: 64 } && sha.All(char.IsAsciiHexDigit)
                && (wheel is null || WheelTags.Preference(fileName) > WheelTags.Preference(wheel.FileName)))
            {
                wheel = new PyPiWheel(fileName, url, sha, WebJson.Member(file, "size") is { ValueKind: JsonValueKind.Number } size && size.TryGetInt64(out var bytes) ? bytes : 0);
            }
        }

        var requires = WebJson.Member(info, "requires_dist") is { ValueKind: JsonValueKind.Array } list
            // Extras ("pkg; extra == 'dev'") are not dependencies of the package as it is installed.
            ? list.EnumerateArray().Count(entry => entry.ValueKind == JsonValueKind.String && entry.GetString() is { } text && !text.Contains("extra ==", StringComparison.Ordinal))
            : 0;
        string? repository = null;
        if (WebJson.Member(info, "project_urls") is { ValueKind: JsonValueKind.Object } urls)
        {
            repository = urls.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .Select(property => CandidateText.GitHubRepositoryUrl(property.Value.GetString()))
                .FirstOrDefault(url => url is not null);
        }

        var license = CandidateText.Line(WebJson.Text(info, "license_expression"), 60)
            ?? (WebJson.Text(info, "license") is { Length: > 0 and <= 60 } shortLicense ? CandidateText.Line(shortLicense, 60) : null);
        return new PyPiFacts
        {
            Name = name,
            Version = version,
            Wheel = wheel,
            HasSourceOnly = sourceOnly && wheel is null,
            Yanked = WebJson.Flag(info, "yanked") == true || allYanked && WebJson.Items(root, "urls").Any(),
            RequiresPython = CandidateText.Line(WebJson.Text(info, "requires_python"), 80),
            DependencyCount = requires,
            License = license,
            RepositoryUrl = repository,
            Summary = CandidateText.Line(WebJson.Text(info, "summary"), 300),
            UploadedAt = uploaded,
        };
    }
}

/// <summary>Which wheel files can be installed into the Python the Assistant sets up (CPython 3.12 for 64-bit Windows).</summary>
internal static class WheelTags
{
    /// <summary>Whether the wheel named <paramref name="fileName"/> runs there: built for any system, or for 64-bit Windows, for Python 3 or CPython 3.12 or earlier's stable interface.</summary>
    public static bool IsInstallable(string fileName)
    {
        if (!fileName.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = fileName[..^4].Split('-');
        if (parts.Length is not (5 or 6))
        {
            return false;
        }

        var python = parts[^3].Split('.');
        var abi = parts[^2].Split('.');
        var platform = parts[^1].Split('.');
        return python.Any(IsPython) && abi.Any(IsAbi) && platform.Any(tag => tag is "any" or "win_amd64");
    }

    /// <summary>How well a wheel fits (a pure-Python one is the most portable): higher is better.</summary>
    public static int Preference(string fileName) => fileName.Contains("-none-any", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private static bool IsPython(string tag) =>
        tag is "py3" or "cp3" or "py312" or "cp312" || tag.StartsWith("py3", StringComparison.Ordinal) && int.TryParse(tag.AsSpan(3), out var minor) && minor <= 12
        || tag.StartsWith("cp3", StringComparison.Ordinal) && int.TryParse(tag.AsSpan(3), out var cpython) && cpython <= 12;

    private static bool IsAbi(string tag) => tag is "none" or "abi3" or "cp312";
}
