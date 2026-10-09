using System.Text;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>A place where another program keeps the MCP servers it has been set up with.</summary>
/// <param name="Client">The program, in words the user knows.</param>
/// <param name="Path">The full path of its configuration file.</param>
/// <param name="ServerProperties">The names of the JSON members that hold the servers (<c>mcpServers</c>, <c>servers</c>).</param>
public sealed record LocalMcpConfigLocation(string Client, string Path, IReadOnlyList<string> ServerProperties);

/// <summary>
/// Finds the servers the user has already set up in other programs that use MCP on this PC (PROJECT_SPEC §4.8, step 105): Claude Desktop, VS Code, Cursor and
/// Windsurf each keep a small JSON file of them. Of each server it reads the name and whether it is a program or an address, and nothing else:
/// not the program's path, its arguments, its environment (where keys and tokens are usually put), an address, a header. It runs and
/// connects to nothing and imports nothing; what it finds only tells the Assistant that the user has such a server already, so that it does
/// not go and look for another. It looks only when the Files permission is on, only for the app a request is about, and only in the fixed
/// files named here (not in the large state files of those programs). A file that is too big, is not JSON or cannot be read is skipped. It
/// logs counts only.
/// </summary>
public sealed partial class LocalMcpConfigSource : IAvailableIntegrationSource
{
    private const long MaxFileLength = 512 * 1024;
    private const int MaxServersPerFile = 200;
    private const int MaxNameLength = 80;

    private readonly IReadOnlyList<LocalMcpConfigLocation> _locations;
    private readonly IPermissionPolicy? _permissions;
    private readonly ILogger<LocalMcpConfigSource> _logger;

    /// <summary>Creates the source over the files of the programs it knows.</summary>
    /// <param name="logger">Where counts are logged.</param>
    /// <param name="permissions">Asked for the Files permission before any file is read; without it nothing is read.</param>
    /// <param name="locations">Where to look; the usual places for the current user when omitted.</param>
    public LocalMcpConfigSource(
        ILogger<LocalMcpConfigSource> logger, IPermissionPolicy? permissions = null, IReadOnlyList<LocalMcpConfigLocation>? locations = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _permissions = permissions;
        _locations = locations ?? DefaultLocations();
    }

    /// <summary>The files of the programs that are known to keep MCP servers, for the current user.</summary>
    public static IReadOnlyList<LocalMcpConfigLocation> DefaultLocations()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new List<LocalMcpConfigLocation>();
        if (appData.Length > 0)
        {
            places.Add(new("Claude Desktop", Path.Combine(appData, "Claude", "claude_desktop_config.json"), ["mcpServers"]));
            places.Add(new("VS Code", Path.Combine(appData, "Code", "User", "mcp.json"), ["servers"]));
        }

        if (home.Length > 0)
        {
            places.Add(new("Cursor", Path.Combine(home, ".cursor", "mcp.json"), ["mcpServers"]));
            places.Add(new("Windsurf", Path.Combine(home, ".codeium", "windsurf", "mcp_config.json"), ["mcpServers"]));
        }

        return places;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AvailableIntegration>> FindAsync(string appKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appKey);
        if (_permissions is null || !(await _permissions.CheckAsync(PermissionCapability.Files, cancellationToken).ConfigureAwait(false)).IsAllowed)
        {
            return [];
        }

        var found = new List<AvailableIntegration>();
        var looked = 0;
        var unreadable = 0;
        foreach (var location in _locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await ReadAsync(location.Path, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                continue;
            }

            looked++;
            var servers = Servers(content, location, appKey);
            if (servers is null)
            {
                unreadable++;
                continue;
            }

            found.AddRange(servers);
        }

        LogLooked(_logger, looked, unreadable, found.Count);
        return found;
    }

    // The file's bytes, or null when there is no such file, it is too big, or it cannot be read.
    private static async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileLength)
            {
                return null;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.Length > MaxFileLength ? null : buffer.ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // The servers of one file that are about the app; null when the file is not JSON of the expected shape.
    private static List<AvailableIntegration>? Servers(byte[] content, LocalMcpConfigLocation location, string appKey)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var found = new List<AvailableIntegration>();
            foreach (var propertyName in location.ServerProperties)
            {
                if (!document.RootElement.TryGetProperty(propertyName, out var servers) || servers.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var count = 0;
                foreach (var server in servers.EnumerateObject())
                {
                    if (++count > MaxServersPerFile)
                    {
                        break;
                    }

                    if (CleanName(server.Name) is { } name && AppIdentity.IsAbout(name, appKey) && server.Value.ValueKind == JsonValueKind.Object)
                    {
                        found.Add(new AvailableIntegration(name, location.Client, KindOf(server.Value)));
                    }
                }
            }

            return found;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A program that is started has a "command"; a server that is connected to has an address and perhaps a type that says which transport.
    private static McpTransportKind KindOf(JsonElement server)
    {
        if (server.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
        {
            return McpTransportKind.Stdio;
        }

        return server.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "sse", StringComparison.OrdinalIgnoreCase)
            ? McpTransportKind.LegacySse
            : McpTransportKind.StreamableHttp;
    }

    // The name as it can be shown: one line of printable characters, not too long.
    private static string? CleanName(string name)
    {
        var builder = new StringBuilder(Math.Min(name.Length, MaxNameLength));
        foreach (var character in name)
        {
            if (builder.Length >= MaxNameLength)
            {
                break;
            }

            builder.Append(char.IsControl(character) || char.IsSurrogate(character) ? ' ' : character);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    [LoggerMessage(EventId = 3130, Level = LogLevel.Information, Message = "Looked for a server set up in other programs: {Files} files read, {Unreadable} unreadable, {Found} found")]
    private static partial void LogLooked(ILogger logger, int files, int unreadable, int found);
}
