using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Core.Updates;

/// <summary>A newer release than the one that is running.</summary>
/// <param name="CurrentVersion">The version that is running ("0.1.148").</param>
/// <param name="NewVersion">The newer release's tag ("v0.2.0").</param>
/// <param name="ReleaseUrl">The release's page on GitHub, which is what the user is sent to.</param>
public sealed record AvailableUpdate(string CurrentVersion, string NewVersion, string ReleaseUrl);

/// <summary>How a check the user asked for ended.</summary>
/// <param name="Update">The newer release, or <see langword="null"/> when there is none or it could not be told.</param>
/// <param name="Failed">Whether GitHub could not be asked or did not answer as expected, so that "up to date" is not known.</param>
public sealed record UpdateCheckResult(AvailableUpdate? Update, bool Failed = false);

/// <summary>Looks for a newer release of the Assistant.</summary>
public interface IUpdateChecker
{
    /// <summary>The version that is running.</summary>
    string CurrentVersion { get; }

    /// <summary>Raised, on a background thread, when a check that was due found a newer release the user has not chosen to ignore.</summary>
    event EventHandler<AvailableUpdate>? UpdateAvailable;

    /// <summary>
    /// Looks for a newer release if that is due: called when the Assistant starts and whenever the user opens it (the bar or the full window), never on a
    /// timer. It returns at once; a check that is due runs in the background and raises <see cref="UpdateAvailable"/> when it finds one.
    /// </summary>
    void TriggerCheckIfDue();

    /// <summary>Looks now, whenever the last look was and whatever was ignored: the Check for updates button.</summary>
    Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Keeps that the user chose to ignore <paramref name="version"/>, so that a check that is due does not tell them of it again.</summary>
    Task DismissAsync(string version, CancellationToken cancellationToken = default);
}

/// <summary>
/// The update check, after AirMirror's (github.com/MuhannadYT/AirMirror, <c>UpdateCheckService</c>), with the same rules:
/// <list type="bullet">
/// <item>It never runs on a timer. It is started when the Assistant starts and by what the user does with it (opening the bar or the full window), so it
/// looks while the Assistant is running and in use, whatever the local model is doing.</item>
/// <item>It asks at most once in <see cref="CheckInterval"/> (thirty days), kept in <see cref="UpdateSettings.LastCheckedAt"/>, which is written after
/// every answer GitHub gives, also one that says no (404, rate limited), so that nothing is asked again and again when something is off.</item>
/// <item>A release the user chose to ignore (<see cref="UpdateSettings.DismissedVersion"/>) is not announced again; an even newer one is.</item>
/// <item>It asks for <c>/releases/latest</c>, which leaves out drafts and pre-releases, and checks for them all the same.</item>
/// <item>Different published releases are ordered by their publication dates, so restarting the version numbering does not hide an update.</item>
/// <item>The button looks at once, whatever the last look and whatever was ignored.</item>
/// </list>
/// It looks with Local Only mode on as well (the user's choice, 0.1.149: with Local Only on from the first start, a check that waited for it to be off
/// would hardly ever run). All that is sent is the request for the public release list, with the Assistant's name and version as the user agent GitHub
/// requires; nothing about the user, their questions or this PC goes with it.
/// </summary>
public sealed class GitHubUpdateChecker : IUpdateChecker, IDisposable
{
    /// <summary>Whose repository the releases are in.</summary>
    public const string Owner = "MouayadYT";

    /// <summary>The repository the releases are in.</summary>
    public const string Repository = "Kiri";

    /// <summary>The project's page.</summary>
    public const string ProjectUrl = "https://github.com/" + Owner + "/" + Repository;

    /// <summary>How long after one look the next is due.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(30);

    private readonly ISettingsService _settings;
    private readonly IAppEventBus? _events;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private int _checking;

    /// <summary>Creates the checker.</summary>
    /// <param name="settings">Where the time of the last look and the ignored version are kept.</param>
    /// <param name="events">Told when the settings were written, so that whoever holds a copy of them takes the new one.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">Where outcomes are logged: status codes and versions, nothing else.</param>
    /// <param name="handler">What sends the request, for a test; the real one when omitted.</param>
    /// <param name="currentVersion">The version that is running, for a test; the running program's own when omitted.</param>
    public GitHubUpdateChecker(
        ISettingsService settings, IAppEventBus? events = null, TimeProvider? clock = null, ILogger<GitHubUpdateChecker>? logger = null,
        HttpMessageHandler? handler = null, string? currentVersion = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _events = events;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GitHubUpdateChecker>.Instance;
        CurrentVersion = currentVersion ?? ResolveCurrentVersion();

        // GitHub's API requires a User-Agent and recommends an explicit Accept header.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false }, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Repository, TryParse(CurrentVersion, out _) ? CurrentVersion : "0.0.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <inheritdoc/>
    public event EventHandler<AvailableUpdate>? UpdateAvailable;

    /// <inheritdoc/>
    public string CurrentVersion { get; }

    /// <summary>The check that <see cref="TriggerCheckIfDue"/> started last, or a finished task; for tests.</summary>
    public Task Checking { get; private set; } = Task.CompletedTask;

    /// <inheritdoc/>
    public void TriggerCheckIfDue()
    {
        // Only one check at a time, however often the window is opened.
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
        {
            return;
        }

        Checking = Task.Run(async () =>
        {
            try
            {
                var settings = await _settings.LoadAsync().ConfigureAwait(false);
                if (settings.Updates.LastCheckedAt is { } last && _clock.GetUtcNow() - last < CheckInterval)
                {
                    return;
                }

                var result = await AskAsync(CancellationToken.None).ConfigureAwait(false);
                if (result.Update is { } update
                    && !string.Equals((await _settings.LoadAsync().ConfigureAwait(false)).Updates.DismissedVersion, update.NewVersion, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateLog.Found(_logger, update.NewVersion, update.CurrentVersion);
                    UpdateAvailable?.Invoke(this, update);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A check must never take the app down: it is tried again when it is next due.
                UpdateLog.Failed(_logger, exception.GetType().Name);
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
        });
    }

    /// <inheritdoc/>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await AskAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            UpdateLog.Failed(_logger, exception.GetType().Name);
            return new UpdateCheckResult(null, Failed: true);
        }
    }

    /// <inheritdoc/>
    public Task DismissAsync(string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return SaveAsync(updates => updates with { DismissedVersion = version.Trim() }, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();

    // Asks GitHub for the latest release and says whether it is newer than what is running.
    private async Task<UpdateCheckResult> AskAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            new Uri($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest"), cancellationToken).ConfigureAwait(false);

        // The time is kept after every answer, also one that says no, so that GitHub is not asked again and again when something is off.
        await SaveAsync(updates => updates with { LastCheckedAt = _clock.GetUtcNow() }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            UpdateLog.Refused(_logger, (int)response.StatusCode);
            return new UpdateCheckResult(null, Failed: true);
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new UpdateCheckResult(null, Failed: true);
        }

        // "Latest" already leaves out drafts and pre-releases; this holds if that ever changes.
        if (IsTrue(root, "prerelease") || IsTrue(root, "draft"))
        {
            return new UpdateCheckResult(null);
        }

        var tag = Text(root, "tag_name");
        var page = Text(root, "html_url");
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(page) || !IsReleasePage(page)
            || !TryParse(tag, out var latest) || !TryParse(CurrentVersion, out var current))
        {
            UpdateLog.NotComparable(_logger);
            return new UpdateCheckResult(null, Failed: true);
        }

        if (latest == current)
        {
            return new UpdateCheckResult(null);
        }

        // Kiri reset its numbering from 0.1.149 to 0.1.2. Numeric ordering alone both hides that update
        // and can offer the older build in the reverse direction. GitHub's publication dates establish
        // the order of released builds; a local file's timestamp or the release-list order cannot.
        if (!TryPublishedAt(root, out var publishedAt)
            || await FindCurrentPublicationAsync(current, cancellationToken).ConfigureAwait(false) is not { } currentPublishedAt)
        {
            UpdateLog.NotComparable(_logger);
            return new UpdateCheckResult(null, Failed: true);
        }

        return publishedAt > currentPublishedAt
            ? new UpdateCheckResult(new AvailableUpdate(CurrentVersion, tag.Trim(), page))
            : new UpdateCheckResult(null);
    }

    // Resolve the installed release rather than trusting an unrelated release or guessing from its number.
    // Older tags may omit the conventional "v". Only a 404 warrants trying the other spelling.
    private async Task<DateTimeOffset?> FindCurrentPublicationAsync(Version current, CancellationToken cancellationToken)
    {
        var name = CurrentVersion.Trim().TrimStart('v', 'V').Split('+')[0];
        foreach (var tag in new[] { "v" + name, name })
        {
            using var response = await _http.GetAsync(
                new Uri($"https://api.github.com/repos/{Owner}/{Repository}/releases/tags/{Uri.EscapeDataString(tag)}"),
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                UpdateLog.Refused(_logger, (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var release = document.RootElement;
            return release.ValueKind == JsonValueKind.Object && !IsTrue(release, "draft") && !IsTrue(release, "prerelease")
                && TryParse(Text(release, "tag_name"), out var version) && version == current
                && Text(release, "html_url") is { } page && IsReleasePage(page)
                && TryPublishedAt(release, out var publishedAt) ? publishedAt : null;
        }

        return null;
    }

    private static bool TryPublishedAt(JsonElement root, out DateTimeOffset publishedAt)
    {
        publishedAt = default;
        return root.TryGetProperty("published_at", out var value) && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out publishedAt);
    }

    private async Task SaveAsync(Func<UpdateSettings, UpdateSettings> change, CancellationToken cancellationToken)
    {
        var saved = await _settings.UpdateAsync(settings => settings with { Updates = change(settings.Updates) }, cancellationToken).ConfigureAwait(false);
        if (_events is not null)
        {
            await _events.PublishAsync(new SettingsSaved(saved), cancellationToken).ConfigureAwait(false);
        }
    }

    // The page a release sends the user to is opened in their browser: only one of this project's own pages on github.com is.
    private static bool IsReleasePage(string page) =>
        Uri.TryCreate(page, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith($"/{Owner}/{Repository}/", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ResolveCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(GitHubUpdateChecker).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Without the "+commit" a build may add.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Reads a version that may begin with "v" ("v1.2.3", "1.2.3") and end with "-pre.1" or "+meta", which are left out. False when it is not
    /// numbers with dots between them.
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var cut = trimmed.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        if (Version.TryParse(trimmed, out var parsed))
        {
            // "1.2" and "1.2.0" are the same release.
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
            return true;
        }

        version = new Version(0, 0);
        return false;
    }
}

internal static partial class UpdateLog
{
    [LoggerMessage(EventId = 9300, Level = LogLevel.Information, Message = "Update check: version {NewVersion} is available (running {CurrentVersion})")]
    public static partial void Found(ILogger logger, string newVersion, string currentVersion);

    [LoggerMessage(EventId = 9301, Level = LogLevel.Information, Message = "Update check: GitHub answered {StatusCode}")]
    public static partial void Refused(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 9302, Level = LogLevel.Information, Message = "Update check: the versions could not be compared")]
    public static partial void NotComparable(ILogger logger);

    [LoggerMessage(EventId = 9303, Level = LogLevel.Information, Message = "Update check failed: {ExceptionType}")]
    public static partial void Failed(ILogger logger, string exceptionType);
}
