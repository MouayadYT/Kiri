using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assistant.Tools.Integrations;

/// <summary>Keeps what the Integration Finder found for a while, so that asking again for the same app and capability is quick and sends nothing.</summary>
internal interface IDiscoveryCache
{
    /// <summary>The answer kept for <paramref name="key"/> that has not expired by <paramref name="now"/>, or <see langword="null"/>.</summary>
    IntegrationDiscoveryResult? TryGet(string key, DateTimeOffset now);

    /// <summary>Keeps <paramref name="result"/> for <paramref name="key"/> until <paramref name="expiresAt"/>.</summary>
    void Put(string key, IntegrationDiscoveryResult result, DateTimeOffset expiresAt);
}

/// <summary>
/// The finder's cache (PROJECT_SPEC §3.5, §4.8, step 106): in memory, and in <c>integration-discovery.json</c> in the app's cache folder so that it survives
/// a restart. It holds the app, the two words of the capability and the public facts found about candidates (names, addresses, licences, versions),
/// never the user's request, and at most a hundred answers, each until it expires (a day when something was found, two hours when nothing was).
/// What is read from the file is checked like anything from the web (<see cref="CandidateSanitizer"/>): the file is a cache and a stale or edited
/// one costs a new search, never more. A failure to read or write it is a cache that does not keep.
/// </summary>
internal sealed class DiscoveryCache : IDiscoveryCache
{
    /// <summary>The version of the file's layout.</summary>
    public const int SchemaVersion = 1;

    private const long MaxFileLength = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? _path;
    private readonly int _maxEntries;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;

    /// <summary>Creates the cache.</summary>
    /// <param name="path">The file to keep it in; <see langword="null"/> for a cache that lives only in memory.</param>
    /// <param name="maxEntries">The most answers kept.</param>
    public DiscoveryCache(string? path, int maxEntries)
    {
        _path = path;
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <inheritdoc/>
    public IntegrationDiscoveryResult? TryGet(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            var entries = Loaded();
            if (entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > now)
                {
                    return entry.Result;
                }

                entries.Remove(key);
            }

            return null;
        }
    }

    /// <inheritdoc/>
    public void Put(string key, IntegrationDiscoveryResult result, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            var entries = Loaded();
            entries[key] = new Entry(key, expiresAt, result with { FromCache = false });
            while (entries.Count > _maxEntries)
            {
                // The one that expires first goes first.
                entries.Remove(entries.Values.MinBy(entry => entry.ExpiresAt)!.Key);
            }

            Save(entries);
        }
    }

    // The entries, read from the file the first time they are needed.
    private Dictionary<string, Entry> Loaded()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (_path is null)
        {
            return _entries;
        }

        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length > MaxFileLength)
            {
                return _entries;
            }

            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllBytes(_path), Json);
            if (file is not { Entries: { } stored } || file.SchemaVersion != SchemaVersion)
            {
                return _entries;
            }

            foreach (var entry in stored.Take(_maxEntries))
            {
                if (!string.IsNullOrEmpty(entry.Key) && entry.Result is { } result)
                {
                    _entries[entry.Key] = entry with { Result = CandidateSanitizer.Clean(result) };
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _entries.Clear();
        }

        return _entries;
    }

    // Written whole through a temporary file; a failure only means the answers are not kept for the next start.
    private void Save(Dictionary<string, Entry> entries)
    {
        if (_path is null)
        {
            return;
        }

        var temporary = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new CacheFile(SchemaVersion, [.. entries.Values]), Json));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                // A temporary file that cannot be removed is overwritten by the next write.
            }
        }
    }

    private sealed record Entry(string Key, DateTimeOffset ExpiresAt, IntegrationDiscoveryResult Result);

    private sealed record CacheFile(int SchemaVersion, IReadOnlyList<Entry> Entries);
}

/// <summary>Checks and cleans candidates that were read back from a file, as <see cref="CandidateText"/> does for what comes from the web.</summary>
internal static class CandidateSanitizer
{
    /// <summary>The result with every candidate cleaned; one that cannot be (no name, or an address that is not <c>https</c>) is left out, and the list is cut to a few.</summary>
    public static IntegrationDiscoveryResult Clean(IntegrationDiscoveryResult result) =>
        result with
        {
            Candidates = [.. result.Candidates.Take(10).Select(Clean).OfType<IntegrationCandidate>()],
            SourcesAnswered = [.. result.SourcesAnswered.Take(8).Select(source => CandidateText.Line(source, 20)).OfType<string>()],
            SourcesFailed = [.. result.SourcesFailed.Take(8).Select(source => CandidateText.Line(source, 20)).OfType<string>()],
        };

    /// <summary>The candidate with every text cleaned and every address checked; <see langword="null"/> when it has no name or no <c>https</c> address of its own.</summary>
    public static IntegrationCandidate? Clean(IntegrationCandidate? candidate)
    {
        if (candidate is null || CandidateText.Line(candidate.Name, 120) is not { } name || CandidateText.Https(candidate.SourceUrl) is not { } sourceUrl)
        {
            return null;
        }

        return candidate with
        {
            Name = name,
            SourceUrl = sourceUrl,
            RepositoryUrl = CandidateText.Https(candidate.RepositoryUrl),
            Publisher = CandidateText.Line(candidate.Publisher, 60),
            License = CandidateText.Line(candidate.License, 40),
            Description = CandidateText.Line(candidate.Description, 300),
            Packages = [.. (candidate.Packages ?? []).Take(8).Select(Clean).OfType<CandidatePackage>()],
            RemoteUrl = CandidateText.Https(candidate.RemoteUrl),
            RequiredSecrets = [.. (candidate.RequiredSecrets ?? []).Take(8).Select(secret => CandidateText.Line(secret, 64)).OfType<string>()],
            ToolNames = [.. (candidate.ToolNames ?? []).Take(60).Select(tool => CandidateText.Line(tool, 64)).OfType<string>()],
            Version = CandidateText.Line(candidate.Version, 40),
            CommitSha = candidate.CommitSha is { Length: 40 } sha && sha.All(char.IsAsciiHexDigit) ? sha.ToLowerInvariant() : null,
            FoundIn = [.. (candidate.FoundIn ?? []).Take(6).Select(source => CandidateText.Line(source, 20)).OfType<string>()],
            Stars = candidate.Stars is { } stars && stars >= 0 ? stars : null,
            Trust = Enum.IsDefined(candidate.Trust) ? candidate.Trust : CandidateTrust.Unknown,
            Runtime = Enum.IsDefined(candidate.Runtime) ? candidate.Runtime : CandidateRuntime.Unknown,
            Evidence = Enum.IsDefined(candidate.Evidence) ? candidate.Evidence : CapabilityEvidence.None,
            Assessment = Enum.IsDefined(candidate.Assessment) ? candidate.Assessment : CandidateAssessment.NotJudged,
        };
    }

    private static CandidatePackage? Clean(CandidatePackage? package) =>
        package is not null && Enum.IsDefined(package.Method) && CandidateText.Identifier(package.Identifier) is { } identifier
            ? new CandidatePackage(package.Method, identifier, CandidateText.Line(package.Version, 40))
            : null;
}
