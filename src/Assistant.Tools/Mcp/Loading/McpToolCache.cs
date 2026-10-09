using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Tools.Integrations;

namespace Assistant.Tools.Mcp;

/// <summary>A local integration's tools as they were last read from it, and when.</summary>
/// <param name="Tools">What the server listed then.</param>
/// <param name="SavedAt">When.</param>
internal sealed record CachedTools(IReadOnlyList<McpToolDescriptor> Tools, DateTimeOffset SavedAt);

/// <summary>
/// Keeps the tools a local integration listed, so that a request about it can be answered, and its tools offered to the model, without starting its
/// program (PROJECT_SPEC §4.8, step 109): the program is started only when one of its tools is actually called. What is kept is the server's own list,
/// as data, and it is cleaned and checked every time it is used exactly as a list that was just read is.
/// </summary>
internal interface IMcpToolCache
{
    /// <summary>The tools kept for the integration if they were read for exactly <paramref name="key"/> (the program, its arguments and version); otherwise <see langword="null"/>.</summary>
    CachedTools? TryGet(string integrationId, string key);

    /// <summary>Keeps <paramref name="tools"/> for the integration, replacing what was kept.</summary>
    void Put(string integrationId, string key, IReadOnlyList<McpToolDescriptor> tools, DateTimeOffset savedAt);

    /// <summary>Forgets what is kept for the integration.</summary>
    void Remove(string integrationId);
}

/// <summary>
/// The tool cache in one JSON file in the app's cache folder (<c>integration-tools.json</c>; PROJECT_SPEC §3.5). It is data the app can rebuild at any time, so a file
/// that cannot be read is simply empty and one that cannot be written is ignored; it is written all or nothing, and bounded (a few entries, a few
/// hundred tools each, a few megabytes). It holds the names, descriptions and argument schemas servers gave their tools, never a result, a secret or
/// anything of a conversation.
/// </summary>
internal sealed class JsonMcpToolCache : IMcpToolCache
{
    private const int SchemaVersion = 1;
    private const int MaxEntries = 40;
    private const int MaxToolsPerEntry = IntegrationRules.MaxToolNames;
    private const int MaxEntryBytes = 768 * 1024;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;

    /// <summary>Creates the cache over the file at <paramref name="path"/>.</summary>
    public JsonMcpToolCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>The key a list of tools was read for: the program, how it is started, and the version, hashed, so that nothing of them is kept.</summary>
    public static string KeyOf(InstalledIntegration integration)
    {
        ArgumentNullException.ThrowIfNull(integration);
        var text = JsonSerializer.Serialize(
            new
            {
                integration.Id,
                integration.Transport.Kind,
                integration.Transport.Endpoint,
                integration.Transport.Command,
                integration.Transport.Arguments,
                integration.Transport.WorkingDirectory,
                integration.Transport.Environment,
                integration.InstalledVersion,
                Fingerprint = integration.Managed?.Fingerprint,
            },
            IntegrationJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <inheritdoc/>
    public CachedTools? TryGet(string integrationId, string key)
    {
        lock (_gate)
        {
            if (!Loaded().TryGetValue(integrationId, out var entry) || entry.Key != key)
            {
                return null;
            }

            var tools = new List<McpToolDescriptor>();
            foreach (var tool in entry.Tools)
            {
                if (!IntegrationRules.IsValidToolName(tool.Name) || tool.InputSchema.ValueKind != JsonValueKind.Object)
                {
                    // One bad entry is a list that cannot be trusted: it is read again from the program.
                    return null;
                }

                tools.Add(new McpToolDescriptor(tool.Name, tool.Title, tool.Description, tool.InputSchema, tool.Annotations ?? McpToolAnnotations.None));
            }

            return new CachedTools(tools, entry.SavedAt);
        }
    }

    /// <inheritdoc/>
    public void Put(string integrationId, string key, IReadOnlyList<McpToolDescriptor> tools, DateTimeOffset savedAt)
    {
        if (tools.Count > MaxToolsPerEntry)
        {
            return;
        }

        var entry = new Entry(key, savedAt, [.. tools.Select(tool => new ToolEntry(tool.Name, tool.Title, tool.Description, tool.InputSchema.Clone(), tool.Annotations))]);
        if (JsonSerializer.SerializeToUtf8Bytes(entry, Options).Length > MaxEntryBytes)
        {
            return;
        }

        lock (_gate)
        {
            var entries = Loaded();
            entries[integrationId] = entry;
            while (entries.Count > MaxEntries)
            {
                entries.Remove(entries.MinBy(pair => pair.Value.SavedAt).Key);
            }

            Save(entries);
        }
    }

    /// <inheritdoc/>
    public void Remove(string integrationId)
    {
        lock (_gate)
        {
            var entries = Loaded();
            if (entries.Remove(integrationId))
            {
                Save(entries);
            }
        }
    }

    // Called with the gate held.
    private Dictionary<string, Entry> Loaded()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= MaxFileBytes
                && JsonSerializer.Deserialize<FileContents>(File.ReadAllBytes(_path), Options) is { SchemaVersion: SchemaVersion, Entries: { } read })
            {
                foreach (var (id, entry) in read)
                {
                    if (IntegrationRules.IsValidId(id) && entry is { Tools: not null, Key: not null } && entry.Tools.Count <= MaxToolsPerEntry)
                    {
                        _entries[id] = entry;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // What cannot be read is read again from the programs.
        }

        return _entries;
    }

    // Called with the gate held. A cache that cannot be written is only slower.
    private void Save(Dictionary<string, Entry> entries)
    {
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new FileContents(SchemaVersion, entries), Options);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                // Overwritten by the next write.
            }
        }
    }

    private sealed record FileContents(int SchemaVersion, Dictionary<string, Entry> Entries);

    private sealed record Entry(string Key, DateTimeOffset SavedAt, List<ToolEntry> Tools);

    private sealed record ToolEntry(string Name, string? Title, string? Description, JsonElement InputSchema, McpToolAnnotations? Annotations);
}
