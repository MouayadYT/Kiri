using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Tools.Integrations;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Mcp;

/// <summary>
/// Keeps the connections to the connected apps and the lists of their tools (PROJECT_SPEC §4.8, step 104). Nothing is connected to until a request
/// needs it: the first request that mentions an app connects, lists its tools and keeps the list for a while; a call connects again if the
/// connection was closed. A connection that is not used is closed after a few minutes (a program started for it ends). An app that cannot be reached
/// is left alone for a minute so that every request does not wait for it. Every use looks at the integration's record as it is now: one that was
/// disabled, or whose address, program, sign-in or permissions changed, is not used with what was set up before. An app that would send something off
/// this PC is not connected to while Local Only mode is on. What it records about each app is the state of its health, its sign-in and its tool names,
/// never what the server said. Logs name the app by its id and say counts and codes only.
/// </summary>
internal sealed partial class McpConnectionManager : IMcpToolInvoker, IMcpCatalogProvider, IIntegrationConnections, IAsyncDisposable, IDisposable
{
    private readonly IInstalledIntegrationRegistry _registry;
    private readonly IMcpClientFactory _clients;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly McpLoadingOptions _options;
    private readonly ILogger<McpConnectionManager> _logger;
    private readonly IMcpToolCache? _cache;
    private readonly Auth.IMcpAccessTokens? _tokens;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private ITimer? _reaper;
    private int _disposed;

    /// <summary>Creates the manager.</summary>
    public McpConnectionManager(
        IInstalledIntegrationRegistry registry,
        IMcpClientFactory clients,
        ISettingsService settings,
        TimeProvider clock,
        McpLoadingOptions options,
        ILogger<McpConnectionManager> logger,
        IMcpToolCache? cache = null,
        Auth.IMcpAccessTokens? tokens = null)
    {
        _tokens = tokens;
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _clients = clients;
        _settings = settings;
        _clock = clock;
        _options = options;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// The tools of the integration <paramref name="integrationId"/>, connecting and listing them if they are not at hand. It waits at most
    /// <paramref name="budget"/>; loading that takes longer goes on in the background. <see langword="null"/> when the integration is not installed or
    /// not enabled, may not be connected to now, could not be reached, or was too slow.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<McpToolCatalog?> GetCatalogAsync(string integrationId, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        InstalledIntegration? integration;
        try
        {
            integration = await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        }
        catch (IntegrationException)
        {
            // What is installed cannot be read just now: nothing is loaded.
            return null;
        }

        if (integration is not { Enabled: true })
        {
            return null;
        }

        var entry = _entries.GetOrAdd(integrationId, id => new Entry { IntegrationId = id });
        var now = _clock.GetUtcNow();
        Task<McpToolCatalog?> loading;
        lock (entry)
        {
            SyncWithRecord(entry, integration);
            if (entry.Catalog is { } catalog && !entry.Stale && IsFresh(catalog, now))
            {
                entry.LastUsed = now;
                return catalog;
            }

            // A local program's tools that were read before are used without starting it: it is started when one of its tools is called.
            if (entry.Catalog is null && !entry.Stale && FromCache(integration, entry, now) is { } kept)
            {
                entry.Catalog = kept;
                entry.LastUsed = now;
                return kept;
            }

            if (now < entry.BlockedUntil)
            {
                return null;
            }

            if (entry.Loading is { IsCompleted: true })
            {
                entry.Loading = null;
            }

            loading = entry.Loading ??= Task.Run(() => LoadAsync(integration, entry), CancellationToken.None);
        }

        // The loading is not the caller's: it goes on after a caller that cannot wait has given up, and the next request finds its result.
        var finished = await Task.WhenAny(loading, Task.Delay(budget, _clock, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return finished == loading ? await loading.ConfigureAwait(false) : null;
    }

    /// <inheritdoc/>
    public async Task<McpToolResult> CallAsync(string integrationId, McpToolDescriptor tool, JsonElement arguments, bool safeToRepeat, CancellationToken cancellationToken)
    {
        // What is installed has to be known to be allowed: when it cannot be read, nothing is called.
        InstalledIntegration? integration;
        try
        {
            integration = await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        }
        catch (IntegrationException)
        {
            throw new McpException(McpFailure.Blocked);
        }

        if (integration is not { Enabled: true } || await BlockReasonAsync(integration, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new McpException(McpFailure.Blocked);
        }

        var entry = _entries.GetOrAdd(integrationId, id => new Entry { IntegrationId = id });
        lock (entry)
        {
            SyncWithRecord(entry, integration);
        }

        for (var attempt = 0; ; attempt++)
        {
            IMcpClient client;
            try
            {
                client = await ConnectedClientAsync(integration, entry, cancellationToken).ConfigureAwait(false);
            }
            catch (McpException exception)
            {
                await RecordFailureAsync(integration, exception).ConfigureAwait(false);
                throw;
            }

            try
            {
                var result = await client.CallToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
                lock (entry)
                {
                    entry.LastUsed = _clock.GetUtcNow();
                }

                return result;
            }
            catch (McpException exception) when (exception.Failure == McpFailure.SessionExpired && attempt == 0)
            {
                // The server forgot the session, so the request was not served: a new connection is made and the call is made again, once.
                await DropClientAsync(entry, client).ConfigureAwait(false);
            }
            catch (McpException exception) when (exception.Failure is McpFailure.Closed && safeToRepeat && attempt == 0)
            {
                // The program stopped (it crashed, or was ended) while a call that only reads was being made: it is started again and the call is made once more.
                await DropClientAsync(entry, client).ConfigureAwait(false);
            }
            catch (McpException exception) when (exception.Failure == McpFailure.AuthRequired && attempt == 0 && integration.Authentication.Kind == IntegrationAuthKind.OAuth && _tokens is not null)
            {
                // The server refused the token, so it did not serve the request: a renewed token is tried once, and the call is made again. When nothing can be renewed it is a sign-in the user has to make.
                await DropClientAsync(entry, client).ConfigureAwait(false);
                if (!await _tokens.ExpireAsync(integration.Id, cancellationToken).ConfigureAwait(false))
                {
                    await RecordFailureAsync(integration, exception).ConfigureAwait(false);
                    throw;
                }
            }
            catch (McpException exception) when (exception.Failure is McpFailure.Closed or McpFailure.ConnectFailed)
            {
                // A call that may have changed something is never made twice. The program is started again by the next call.
                await DropClientAsync(entry, client).ConfigureAwait(false);
                await RecordFailureAsync(integration, exception).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public async Task ForgetAsync(string integrationId, CancellationToken cancellationToken)
    {
        if (!_entries.TryRemove(integrationId, out var entry))
        {
            return;
        }

        IMcpClient? client;
        lock (entry)
        {
            client = entry.Client;
            entry.Client = null;
            entry.Catalog = null;
        }

        if (client is not null)
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<ConnectionCheck> ReconnectAsync(string integrationId, CancellationToken cancellationToken)
    {
        var integration = await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        if (integration is null || await BlockReasonAsync(integration, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new ConnectionCheck(false, true, McpFailure.Blocked, 0, false);
        }

        // Everything held for it is let go of, what was kept of its tools included: a program that was updated or repaired is read afresh.
        await ForgetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        _cache?.Remove(integrationId);
        var catalog = await GetCatalogAsync(integrationId, _options.ReconnectTimeout, cancellationToken).ConfigureAwait(false);
        if (catalog is not null)
        {
            return new ConnectionCheck(true, false, null, catalog.Tools.Count, true);
        }

        var after = await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        return new ConnectionCheck(false, false, after?.Health.Failure ?? McpFailure.TimedOut, 0, false);
    }

    /// <inheritdoc/>
    public async Task<ConnectionCheck> CheckHealthAsync(string integrationId, CancellationToken cancellationToken)
    {
        var integration = await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false);
        if (integration is null || !_entries.TryGetValue(integrationId, out var entry))
        {
            return new ConnectionCheck(false, integration is null, null, 0, false);
        }

        IMcpClient? client;
        lock (entry)
        {
            client = entry.Client;
        }

        if (client is null)
        {
            // Not connected: nothing is started to find out.
            return new ConnectionCheck(false, false, null, 0, false);
        }

        if (await PingAsync(client, cancellationToken).ConfigureAwait(false))
        {
            await RecordHealthyAsync(integration).ConfigureAwait(false);
            return new ConnectionCheck(true, false, null, 0, false);
        }

        // It does not answer: it is let go of (its program ended), and the next use starts it again.
        await DropClientAsync(entry, client).ConfigureAwait(false);
        await RecordFailureAsync(integration, new McpException(McpFailure.Closed)).ConfigureAwait(false);
        return new ConnectionCheck(false, false, McpFailure.Closed, 0, false);
    }

    // Whether the server answers a ping within the time it is given.
    private async Task<bool> PingAsync(IMcpClient client, CancellationToken cancellationToken)
    {
        if (!client.IsConnected)
        {
            return false;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.PingTimeout);
        try
        {
            await client.PingAsync(limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (McpException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Ends every connection, for a container that is disposed synchronously (it refuses a service that can only be disposed asynchronously).
    /// It waits on a thread of its own, so a caller on the UI thread cannot deadlock it.
    /// </summary>
    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _reaper?.Dispose();
        foreach (var entry in _entries.Values)
        {
            IMcpClient? client;
            lock (entry)
            {
                client = entry.Client;
                entry.Client = null;
                entry.Catalog = null;
            }

            if (client is not null)
            {
                await DisposeClientAsync(client).ConfigureAwait(false);
            }
        }

        _shutdown.Dispose();
    }

    // Whether a catalog is still fresh: one read from the program lasts a few minutes, one kept from before (not read now) a day.
    private bool IsFresh(McpToolCatalog catalog, DateTimeOffset now) =>
        now - catalog.LoadedAt < (catalog.FromCache ? _options.CachedCatalogLifetime : _options.CatalogLifetime);

    // A program the Assistant starts on this PC: its tools can be kept, since they cannot change without its files changing.
    private static bool IsLocalProgram(InstalledIntegration integration) => integration.Transport.Kind == McpTransportKind.Stdio;

    // The tools kept for the integration, made into a catalog under the rules that apply to it now; null when none are kept for exactly this program.
    private McpToolCatalog? FromCache(InstalledIntegration integration, Entry entry, DateTimeOffset now)
    {
        if (_cache is null || !IsLocalProgram(integration) || _cache.TryGet(integration.Id, JsonMcpToolCache.KeyOf(integration)) is not { } kept
            || now - kept.SavedAt >= _options.CachedCatalogLifetime || kept.SavedAt > now)
        {
            return null;
        }

        // A local program may not be connected to while it is blocked, whatever is kept; the caller checks that before it gets here.
        return McpToolCatalogBuilder.Build(integration, kept.Tools, this, kept.SavedAt) is { Tools.Count: > 0 } catalog
            ? new McpToolCatalog(catalog.IntegrationId, catalog.AppName, catalog.Tools, catalog.Listed, catalog.LoadedAt) { FromCache = true }
            : null;
    }

    /// <summary>Whether using the integration sends something off this PC: it is reached over a network, or its permissions say it reaches out itself.</summary>
    internal static bool LeavesThisPc(InstalledIntegration integration) =>
        integration.Permissions.LeavesThisPc
        || integration.Transport.Kind != McpTransportKind.Stdio
            && !(McpEndpointRules.Problem(integration.Transport.Endpoint, out var endpoint) is null && endpoint is not null && McpEndpointRules.IsLoopback(endpoint));

    // What a connection depends on: if any of it changes, what was connected or loaded for the old record is not used for the new one. What the
    // Assistant itself records (health, sign-in state, tool names, the transport it found the server to speak) is not part of it.
    private static string ConnectionKey(InstalledIntegration integration) =>
        JsonSerializer.Serialize(
            new
            {
                integration.Id,
                integration.Enabled,
                integration.Transport.Endpoint,
                integration.Transport.Command,
                integration.Transport.Arguments,
                integration.Transport.WorkingDirectory,
                integration.Transport.Environment,
                integration.Transport.Headers,
                AuthenticationKind = integration.Authentication.Kind,
                integration.Authentication.Secrets,
                integration.Permissions,
            },
            IntegrationJson.Options);

    // Forgets what was connected or loaded if the record changed in a way that matters. Called with the entry locked.
    private void SyncWithRecord(Entry entry, InstalledIntegration integration)
    {
        var key = ConnectionKey(integration);
        if (entry.Key == key)
        {
            return;
        }

        entry.Key = key;
        entry.Catalog = null;
        entry.Stale = false;
        entry.BlockedUntil = default;
        if (entry.Client is { } client)
        {
            entry.Client = null;
            _ = Task.Run(() => DisposeClientAsync(client));
        }
    }

    // Why the integration may not be connected to now, or null when it may.
    private async Task<McpFailure?> BlockReasonAsync(InstalledIntegration integration, CancellationToken cancellationToken)
    {
        if (!integration.Enabled)
        {
            return McpFailure.Blocked;
        }

        if (LeavesThisPc(integration))
        {
            // The user's choice for this app comes before the general one: network access turned off for it is refused whatever Local Only says (step 119).
            if (!integration.Permissions.AllowNetwork || (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Privacy.LocalOnly)
            {
                return McpFailure.Blocked;
            }
        }

        // An integration that signs in is not connected to while the user has taken its account away from it, so none of its secrets is read (step 119).
        if (!integration.Permissions.AllowAccountAccess && integration.Authentication.Kind != IntegrationAuthKind.None)
        {
            return McpFailure.Blocked;
        }

        return null;
    }

    // Connects, lists the tools, makes the catalog and records how it went. Nothing it does is allowed to fail the caller: a failure is recorded and a null result.
    private async Task<McpToolCatalog?> LoadAsync(InstalledIntegration integration, Entry entry)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (await BlockReasonAsync(integration, _shutdown.Token).ConfigureAwait(false) is not null)
            {
                return null;
            }

            var client = await ConnectedClientAsync(integration, entry, _shutdown.Token).ConfigureAwait(false);
            var listed = await client.ListToolsAsync(_shutdown.Token).ConfigureAwait(false);
            var catalog = McpToolCatalogBuilder.Build(integration, listed, this, _clock.GetUtcNow());
            if (IsLocalProgram(integration))
            {
                _cache?.Put(integration.Id, JsonMcpToolCache.KeyOf(integration), listed, _clock.GetUtcNow());
            }

            lock (entry)
            {
                // A record that changed while this was loading is not the one the catalog was made for.
                if (entry.Key == ConnectionKey(integration))
                {
                    entry.Catalog = catalog;
                    entry.Stale = false;
                    entry.BlockedUntil = default;
                }
            }

            await RecordSuccessAsync(integration, client, catalog).ConfigureAwait(false);
            LogLoaded(_logger, integration.Id, client.TransportKind, catalog.Tools.Count, catalog.Skipped, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return catalog;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (McpException exception)
        {
            lock (entry)
            {
                entry.BlockedUntil = _clock.GetUtcNow() + _options.FailureBackoff;
            }

            await RecordFailureAsync(integration, exception).ConfigureAwait(false);
            LogFailed(_logger, integration.Id, exception.Failure, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing that goes wrong while loading is the request's concern; it is a failure of the app and is treated as one.
            lock (entry)
            {
                entry.BlockedUntil = _clock.GetUtcNow() + _options.FailureBackoff;
            }

            LogFailed(_logger, integration.Id, McpFailure.Protocol, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return null;
        }
    }

    // The connection of the integration, made if there is none that works. One connection at a time is made.
    private async Task<IMcpClient> ConnectedClientAsync(InstalledIntegration integration, Entry entry, CancellationToken cancellationToken)
    {
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Client is { IsConnected: true } existing)
            {
                return existing;
            }

            if (entry.Client is { } dead)
            {
                entry.Client = null;
                await DisposeClientAsync(dead).ConfigureAwait(false);
            }

            IMcpClient client;
            for (var renewed = false; ; renewed = true)
            {
                client = _clients.Create(integration);
                var made = client;
                made.ToolsChanged += (_, _) =>
                {
                    lock (entry)
                    {
                        entry.Stale = true;
                    }
                };
                made.Disconnected += (_, _) => _ = Task.Run(() => OnDisconnectedAsync(integration, entry, made));
                try
                {
                    await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (Exception exception)
                {
                    await DisposeClientAsync(client).ConfigureAwait(false);

                    // A server that refuses the access token of a sign-in that can be renewed gets a renewed one, once, before the user is asked to sign in again.
                    if (!renewed && await CanRenewAsync(integration, exception, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    throw;
                }
            }

            lock (entry)
            {
                entry.Client = client;
                entry.LastUsed = _clock.GetUtcNow();
            }

            EnsureReaper();
            await RecordHealthyAsync(integration).ConfigureAwait(false);
            return client;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    // Whether a refusal of the access token is one that renewing the token can put right: the app signs in with OAuth and has a refresh token. The token is marked as run out.
    private async Task<bool> CanRenewAsync(InstalledIntegration integration, Exception exception, CancellationToken cancellationToken) =>
        exception is McpException { Failure: McpFailure.AuthRequired } && integration.Authentication.Kind == IntegrationAuthKind.OAuth && _tokens is not null
        && await _tokens.ExpireAsync(integration.Id, cancellationToken).ConfigureAwait(false);

    private async Task DropClientAsync(Entry entry, IMcpClient client)
    {
        lock (entry)
        {
            if (ReferenceEquals(entry.Client, client))
            {
                entry.Client = null;
            }
        }

        await DisposeClientAsync(client).ConfigureAwait(false);
    }

    private static async Task DisposeClientAsync(IMcpClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A connection that cannot be closed cleanly is gone all the same.
        }
    }

    // What a connection that worked says about the app is kept: how it fared, the sign-in, what it offers, the transport it speaks.
    private async Task RecordSuccessAsync(InstalledIntegration integration, IMcpClient client, McpToolCatalog catalog)
    {
        var now = _clock.GetUtcNow();
        try
        {
            if (client.Server is { } server)
            {
                await _registry.RecordCapabilitiesAsync(
                    integration.Id,
                    new IntegrationCapabilities
                    {
                        Tools = server.Capabilities.Tools,
                        Resources = server.Capabilities.Resources,
                        Prompts = server.Capabilities.Prompts,
                        ProtocolVersion = server.ProtocolVersion,
                        ToolNames = [.. catalog.Tools.Select(tool => tool.Descriptor.Name).Take(IntegrationRules.MaxToolNames)],
                        RefreshedAt = now,
                    }).ConfigureAwait(false);
            }

            await _registry.RecordTransportKindAsync(integration.Id, client.TransportKind).ConfigureAwait(false);
            await _registry.RecordAuthenticationStateAsync(
                integration.Id, integration.Authentication.Kind == IntegrationAuthKind.None ? IntegrationAuthState.NotRequired : IntegrationAuthState.Ready, now)
                .ConfigureAwait(false);
            await _registry.RecordHealthAsync(integration.Id, IntegrationHealthStatus.Healthy, null, now).ConfigureAwait(false);
        }
        catch (IntegrationException)
        {
            // The integration was removed, or the list could not be written; the connection is good either way.
        }
    }

    // A connection that ended by itself (its program exited or crashed): it is forgotten, so that the next use starts the program again, and the health says so.
    // A connection the Assistant ended itself (idle, replaced, removed) was taken out of the entry first and so is not one that crashed.
    private async Task OnDisconnectedAsync(InstalledIntegration integration, Entry entry, IMcpClient client)
    {
        lock (entry)
        {
            if (!ReferenceEquals(entry.Client, client))
            {
                return;
            }

            entry.Client = null;
        }

        await DisposeClientAsync(client).ConfigureAwait(false);
        await RecordFailureAsync(integration, new McpException(McpFailure.Closed)).ConfigureAwait(false);
        LogCrashed(_logger, integration.Id);
    }

    // The health of an integration whose connection was found not to answer.
    private async Task RecordClosedAsync(Entry entry)
    {
        var id = entry.IntegrationId;
        if (id is null)
        {
            return;
        }

        try
        {
            if (await _registry.GetAsync(id).ConfigureAwait(false) is { } integration)
            {
                await RecordFailureAsync(integration, new McpException(McpFailure.Closed)).ConfigureAwait(false);
            }
        }
        catch (IntegrationException)
        {
            // The list could not be read; the connection is let go of all the same.
        }
    }

    // A connection that was made says the app is reachable (and, where it needs a sign-in, that the one it has works).
    private async Task RecordHealthyAsync(InstalledIntegration integration)
    {
        var now = _clock.GetUtcNow();
        try
        {
            await _registry.RecordAuthenticationStateAsync(
                integration.Id, integration.Authentication.Kind == IntegrationAuthKind.None ? IntegrationAuthState.NotRequired : IntegrationAuthState.Ready, now)
                .ConfigureAwait(false);
            await _registry.RecordHealthAsync(integration.Id, IntegrationHealthStatus.Healthy, null, now).ConfigureAwait(false);
        }
        catch (IntegrationException)
        {
            // The integration was removed, or the list could not be written; the connection is good either way.
        }
    }

    private async Task RecordFailureAsync(InstalledIntegration integration, McpException exception)
    {
        var now = _clock.GetUtcNow();
        var status = exception.Failure switch
        {
            McpFailure.AuthRequired or McpFailure.Forbidden => IntegrationHealthStatus.AuthRequired,
            McpFailure.Unsupported => IntegrationHealthStatus.Incompatible,
            McpFailure.Blocked => IntegrationHealthStatus.Unknown,
            McpFailure.ConnectFailed or McpFailure.LaunchFailed or McpFailure.Closed or McpFailure.TimedOut or McpFailure.HttpError or McpFailure.SessionExpired =>
                IntegrationHealthStatus.Unreachable,
            _ => IntegrationHealthStatus.Failed,
        };
        if (status == IntegrationHealthStatus.Unknown)
        {
            return;
        }

        try
        {
            if (status == IntegrationHealthStatus.AuthRequired)
            {
                await _registry.RecordAuthenticationStateAsync(
                    integration.Id, exception.Failure == McpFailure.Forbidden ? IntegrationAuthState.Rejected : IntegrationAuthState.NeedsSignIn, now)
                    .ConfigureAwait(false);
            }

            await _registry.RecordHealthAsync(integration.Id, status, exception.Failure, now).ConfigureAwait(false);
        }
        catch (IntegrationException)
        {
            // The integration was removed, or the list could not be written; the failure is reported to the caller all the same.
        }
    }

    private void EnsureReaper()
    {
        if (_reaper is null && Volatile.Read(ref _disposed) == 0)
        {
            _reaper = _clock.CreateTimer(state => _ = Task.Run(ReapAsync), null, _options.ReapInterval, _options.ReapInterval);
        }
    }

    // Closes the connections that have not been used for a while; the lists of tools are kept, and a call connects again.
    private async Task ReapAsync()
    {
        var now = _clock.GetUtcNow();
        foreach (var entry in _entries.Values)
        {
            IMcpClient? idle = null;
            IMcpClient? quiet = null;
            lock (entry)
            {
                if (entry.Client is { } client && now - entry.LastUsed > _options.IdleTimeout && entry.Loading is null or { IsCompleted: true })
                {
                    idle = client;
                    entry.Client = null;
                }
                else if (entry.Client is { } connected && now - entry.LastUsed > _options.HealthCheckInterval && now - entry.LastChecked > _options.HealthCheckInterval)
                {
                    quiet = connected;
                    entry.LastChecked = now;
                }
            }

            if (idle is not null)
            {
                await DisposeClientAsync(idle).ConfigureAwait(false);
            }
            else if (quiet is not null && !await PingAsync(quiet, _shutdown.Token).ConfigureAwait(false))
            {
                // A connection that is quiet and no longer answers (its program hung or died): it is let go of and the next use starts it again.
                await DropClientAsync(entry, quiet).ConfigureAwait(false);
                await RecordClosedAsync(entry).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(EventId = 3112, Level = LogLevel.Warning, Message = "Integration {IntegrationId} stopped by itself; it is started again by the next use")]
    private static partial void LogCrashed(ILogger logger, string integrationId);

    [LoggerMessage(EventId = 3110, Level = LogLevel.Information, Message = "Integration {IntegrationId} connected over {TransportKind}: {OfferedTools} tools usable, {SkippedTools} left out, in {ElapsedMs} ms")]
    private static partial void LogLoaded(ILogger logger, string integrationId, McpTransportKind transportKind, int offeredTools, int skippedTools, long elapsedMs);

    [LoggerMessage(EventId = 3111, Level = LogLevel.Warning, Message = "Integration {IntegrationId} could not be loaded: {Failure} after {ElapsedMs} ms")]
    private static partial void LogFailed(ILogger logger, string integrationId, McpFailure failure, long elapsedMs);

    // What is known about one integration. Its members are used with the entry locked, except the gate, which makes one connection at a time.
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public string? Key { get; set; }

        public IMcpClient? Client { get; set; }

        public McpToolCatalog? Catalog { get; set; }

        public Task<McpToolCatalog?>? Loading { get; set; }

        public bool Stale { get; set; }

        public DateTimeOffset BlockedUntil { get; set; }

        public DateTimeOffset LastUsed { get; set; }

        public DateTimeOffset LastChecked { get; set; }

        public string? IntegrationId { get; set; }
    }
}
