using System.Globalization;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Assistant.Data.Settings;

/// <summary>
/// Keeps the user's settings in a versioned JSON file, <c>%LOCALAPPDATA%\Assistant\settings.json</c>
/// (PROJECT_SPEC §3.5, §5.10). The file holds no secret: tokens and passwords go to <see cref="ISecretStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reading never fails the app. A missing file is the defaults. A file that is not a settings document is set aside
/// (<c>settings.corrupt-…json</c>, the newest three kept, so what the user typed is not lost) and the last good copy
/// (<c>settings.json.bak</c>, made by every save) is used, or else the defaults. A value that cannot be read or is not
/// allowed is replaced by its default and the rest is kept. <see cref="Outcome"/> says which happened.
/// </para>
/// <para>
/// Saving is all or nothing: the new file is written and flushed beside the old one and then swapped in, keeping the old
/// one as the backup, so a crash or a full disk leaves the previous file whole. Settings that are not allowed are refused
/// before anything is written. A file written by a newer build is copied aside before this build first overwrites it.
/// Settings are kept in memory once read, so reading them for each request costs nothing. The file is read and written on
/// the thread pool, so a window that reads or saves settings never waits for the disk.
/// </para>
/// </remarks>
public sealed partial class JsonSettingsService : ISettingsService, ISettingsLoadReport, IDisposable
{
    private const int KeptCorruptCopies = 3;
    private const int SaveAttempts = 3;
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromDays(1);

    private readonly string _path;
    private readonly string _backupPath;
    private readonly IReadOnlyList<SettingsMigration> _migrations;
    private readonly TimeProvider _time;
    private readonly ILogger<JsonSettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings? _current;
    private int _outcome = (int)SettingsLoadOutcome.NotLoaded;
    private int? _newerVersionOnDisk;

    /// <summary>Creates the service over the settings file at <paramref name="settingsFilePath"/>.</summary>
    /// <param name="settingsFilePath">The file, fully qualified. Its folder is created when settings are first saved.</param>
    /// <param name="time">The clock the names of the copies it sets aside come from.</param>
    /// <param name="logger">Where it says, without values, what it found.</param>
    /// <param name="migrations">The steps that bring an older settings file up to date; none exist yet.</param>
    public JsonSettingsService(
        string settingsFilePath, TimeProvider time, ILogger<JsonSettingsService> logger,
        IReadOnlyList<SettingsMigration>? migrations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);
        if (!Path.IsPathFullyQualified(settingsFilePath))
        {
            throw new ArgumentException("The settings file path must be fully qualified.", nameof(settingsFilePath));
        }

        _path = Path.GetFullPath(settingsFilePath);
        _backupPath = _path + ".bak";
        _migrations = migrations ?? DefaultSettingsMigrations.All;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc/>
    public SettingsLoadOutcome Outcome => (SettingsLoadOutcome)Volatile.Read(ref _outcome);

    /// <inheritdoc/>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _current) is { } current)
        {
            return current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return UpdateAsync(_ => settings, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = change(await LoadCoreAsync(cancellationToken).ConfigureAwait(false));
            var issues = SettingsValidator.Validate(updated);
            if (issues.Count > 0)
            {
                LogRefused(_logger, issues.Count);
                throw new SettingsValidationException(issues);
            }

            updated = updated with { SchemaVersion = AppSettings.CurrentSchemaVersion };
            try
            {
                await Task.Run(() => WriteAsync(updated, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogSaveFailed(_logger, exception);
                throw;
            }

            Volatile.Write(ref _current, updated);
            Volatile.Write(ref _outcome, (int)SettingsLoadOutcome.Loaded);
            LogSaved(_logger);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    // What is in memory, or else what the file holds. Call it inside the gate.
    private async Task<AppSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_current is { } known)
        {
            return known;
        }

        var (settings, outcome, cacheable) = await Task.Run(() => ReadAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _outcome, (int)outcome);
        if (cacheable)
        {
            Volatile.Write(ref _current, settings);
        }

        return settings;
    }

    private async Task<(AppSettings Settings, SettingsLoadOutcome Outcome, bool Cacheable)> ReadAsync(CancellationToken cancellationToken)
    {
        DeleteStaleTemporaryFiles();
        byte[]? bytes;
        try
        {
            bytes = await ReadFileAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file may be perfectly good and only busy. Use the defaults for now without remembering them, so the
            // next read tries again, and touch nothing on the disk.
            LogUnreadable(_logger, exception);
            return (new AppSettings(), SettingsLoadOutcome.ResetToDefaults, false);
        }

        if (bytes is null)
        {
            // No settings file: a first run, or the user deleted it to start over, which also ends the old backup's use.
            FileCleanup.TryDelete(_backupPath);
            LogLoaded(_logger, SettingsLoadOutcome.NoFile, 0);
            return (new AppSettings(), SettingsLoadOutcome.NoFile, true);
        }

        if (SettingsFileFormat.TryParse(bytes, _migrations, out var parsed) && parsed is not null)
        {
            if (parsed.FileVersion > AppSettings.CurrentSchemaVersion)
            {
                _newerVersionOnDisk = parsed.FileVersion;
                LogNewerVersion(_logger, parsed.FileVersion);
            }

            var outcome = parsed.ReplacedValues > 0 ? SettingsLoadOutcome.Repaired : SettingsLoadOutcome.Loaded;
            LogLoaded(_logger, outcome, parsed.ReplacedValues);
            return (parsed.Settings, outcome, true);
        }

        return await RecoverAsync(cancellationToken).ConfigureAwait(false);
    }

    // The file is not a settings document. Keep it for the user, and go back to the last good copy or the defaults.
    private async Task<(AppSettings Settings, SettingsLoadOutcome Outcome, bool Cacheable)> RecoverAsync(CancellationToken cancellationToken)
    {
        SetAsideDamagedFile();
        byte[]? backup = null;
        try
        {
            backup = await ReadFileAsync(_backupPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(_logger, exception);
        }

        if (backup is not null && SettingsFileFormat.TryParse(backup, _migrations, out var parsed) && parsed is not null)
        {
            // The good copy becomes the settings file again, so the next start reads it directly.
            TryCopy(_backupPath, _path);
            LogLoaded(_logger, SettingsLoadOutcome.RestoredFromBackup, parsed.ReplacedValues);
            return (parsed.Settings, SettingsLoadOutcome.RestoredFromBackup, true);
        }

        LogLoaded(_logger, SettingsLoadOutcome.ResetToDefaults, 0);
        return (new AppSettings(), SettingsLoadOutcome.ResetToDefaults, true);
    }

    private async Task WriteAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var bytes = SettingsFileFormat.Serialize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = FileOptions.Asynchronous | FileOptions.WriteThrough;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, options))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            KeepFileFromNewerVersion();
            await SwapInAsync(temporary, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            FileCleanup.TryDelete(temporary);
            throw;
        }
    }

    // Puts the finished file in place, keeping the old one as the backup. A virus scanner or the search indexer may hold
    // either for a moment, so a busy file is tried again.
    private async Task SwapInAsync(string temporary, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Replace(temporary, _path, _backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, _path);
                }

                return;
            }
            catch (IOException) when (attempt < SaveAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // A file written by a newer build is copied aside, once, before this build replaces it, so going back to the newer
    // build does not lose what only it understood.
    private void KeepFileFromNewerVersion()
    {
        if (_newerVersionOnDisk is not { } version || !File.Exists(_path))
        {
            return;
        }

        var copy = Path.Combine(
            Path.GetDirectoryName(_path)!,
            $"{Path.GetFileNameWithoutExtension(_path)}.from-v{version.ToString(CultureInfo.InvariantCulture)}{Path.GetExtension(_path)}");
        if (!File.Exists(copy))
        {
            TryCopy(_path, copy);
        }

        _newerVersionOnDisk = null;
    }

    private void SetAsideDamagedFile()
    {
        var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture);
        var directory = Path.GetDirectoryName(_path)!;
        var name = Path.GetFileNameWithoutExtension(_path);
        try
        {
            File.Move(_path, Path.Combine(directory, $"{name}.corrupt-{stamp}{Path.GetExtension(_path)}"), overwrite: true);
            FileCleanup.DeleteAllButNewest(
                Directory.EnumerateFiles(directory, $"{name}.corrupt-*{Path.GetExtension(_path)}"), KeptCorruptCopies);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // It stays where it is; the next save replaces it, and that is all the user loses.
            LogUnreadable(_logger, exception);
        }
    }

    private void DeleteStaleTemporaryFiles()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(_path) + ".*.tmp"))
            {
                if (_time.GetUtcNow() - File.GetLastWriteTimeUtc(file) > StaleTemporaryFileAge)
                {
                    FileCleanup.TryDelete(file);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only litter; it is tried again next time.
        }
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            // One byte over the limit is enough to know the file is too big, without reading a huge one.
            var buffer = new byte[Math.Min(stream.Length, SettingsFileFormat.MaxFileBytes + 1)];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void TryCopy(string from, string to)
    {
        try
        {
            File.Copy(from, to, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy is a courtesy; the settings in memory are already right.
        }
    }

    [LoggerMessage(EventId = 7000, Level = LogLevel.Information, Message = "Settings read ({Outcome}); values replaced by defaults: {ReplacedValues}")]
    private static partial void LogLoaded(ILogger logger, SettingsLoadOutcome outcome, int replacedValues);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "The settings file could not be read, so the defaults are used for now")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Information, Message = "Settings saved")]
    private static partial void LogSaved(ILogger logger);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "Settings could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Warning, Message = "Settings were refused because {IssueCount} values are not allowed")]
    private static partial void LogRefused(ILogger logger, int issueCount);

    [LoggerMessage(EventId = 7005, Level = LogLevel.Warning, Message = "The settings file was written by a newer build (schema version {SchemaVersion}); a copy is kept before it is replaced")]
    private static partial void LogNewerVersion(ILogger logger, int schemaVersion);
}
