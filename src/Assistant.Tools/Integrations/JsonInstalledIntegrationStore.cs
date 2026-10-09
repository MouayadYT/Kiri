using System.Text.Json;

namespace Assistant.Tools.Integrations;

/// <summary>
/// Keeps the installed integrations in one JSON file (<c>integrations.json</c> in the app's folder, PROJECT_SPEC §3.5): <c>schemaVersion</c> and
/// the list. It is read tolerantly (an entry that cannot be read, breaks a rule or repeats an id is left out, and the file as it was is copied
/// to <c>integrations.skipped.json</c> first so nothing is lost; a file that is not JSON is copied the same way and read as empty; a file from
/// a newer build is read as far as it is understood and copied to <c>integrations.from-v&lt;n&gt;.json</c>) and written all or nothing (a
/// temporary file, then a replace). It holds no secret: the sign-in's secrets are named, not written.
/// </summary>
public sealed class JsonInstalledIntegrationStore : IInstalledIntegrationStore
{
    /// <summary>The version of the file's layout this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    // A file bigger than this is not one the Assistant wrote.
    private const long MaxFileLength = 4 * 1024 * 1024;

    private readonly string _path;

    /// <summary>Creates the store over the file at <paramref name="path"/> (<c>AppPaths.IntegrationsFilePath</c>).</summary>
    public JsonInstalledIntegrationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc/>
    public async Task<IntegrationStoreContents> LoadAsync(CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(_path))
            {
                return IntegrationStoreContents.Empty;
            }

            if (new FileInfo(_path).Length > MaxFileLength)
            {
                KeepCopy("skipped");
                return new IntegrationStoreContents([], Unreadable: true);
            }

            bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IntegrationException(IntegrationFailure.StoreFailed, inner: exception);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                KeepCopy("skipped");
                return new IntegrationStoreContents([], Unreadable: true);
            }

            var newer = root.TryGetProperty("schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var number) && number > CurrentSchemaVersion
                ? number
                : 0;
            if (newer > 0)
            {
                KeepCopy("from-v" + newer);
            }

            var integrations = new List<InstalledIntegration>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skipped = 0;
            if (root.TryGetProperty("integrations", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (TryRead(entry) is { } integration && seen.Add(integration.Id))
                    {
                        integrations.Add(integration);
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }

            if (skipped > 0)
            {
                KeepCopy("skipped");
            }

            return new IntegrationStoreContents(integrations, skipped);
        }
        catch (JsonException)
        {
            KeepCopy("skipped");
            return new IntegrationStoreContents([], Unreadable: true);
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(IReadOnlyList<InstalledIntegration> integrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrations);
        var temporary = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(new IntegrationsFile(CurrentSchemaVersion, integrations), IntegrationJson.Options);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temporary);
            throw new IntegrationException(IntegrationFailure.StoreFailed, inner: exception);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
    }

    // One entry, or null when it cannot be read or breaks a rule.
    private static InstalledIntegration? TryRead(JsonElement entry)
    {
        try
        {
            var integration = entry.Deserialize<InstalledIntegration>(IntegrationJson.Options);
            return integration is not null && IntegrationRules.Problems(integration).Count == 0 ? integration : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The file as it is, kept beside it under another name, before anything is written over it.
    private void KeepCopy(string suffix)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(_path) + "." + suffix + Path.GetExtension(_path);
            File.Copy(_path, Path.Combine(directory, name), overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy that cannot be made is not a reason to refuse to read.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temporary file that cannot be removed is overwritten by the next write.
        }
    }

    private sealed record IntegrationsFile(int SchemaVersion, IReadOnlyList<InstalledIntegration> Integrations);
}
