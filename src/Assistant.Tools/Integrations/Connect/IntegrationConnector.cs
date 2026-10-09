using System.Net.Sockets;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.Permissions;
using Assistant.Tools.Mcp;
using Assistant.Tools.Mcp.Auth;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Integrations;

/// <summary>Connects apps the Assistant knows the server of, and signs the Assistant in to them (PROJECT_SPEC §4.8).</summary>
public interface IIntegrationConnector
{
    /// <summary>
    /// Records the app's server as an installed integration (when it is not one already) and signs in to it: the user's browser opens on the app's own page, where they say yes. It
    /// is done only for what the user approved. A sign-in that is not finished leaves the app recorded as needing one, so that it can be tried again.
    /// </summary>
    Task<InstallOutcome> ConnectAsync(
        KnownEndpoint endpoint, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default, OAuthSignInOptions? options = null);

    /// <summary>
    /// Signs in again to the installed integration <paramref name="integrationId"/>, which signs in with OAuth. <paramref name="options"/> says how the page is shown (in a private window,
    /// and what to do with its address, for a person whose browser is signed in to another account); the default is the default browser.
    /// </summary>
    Task<InstallOutcome> SignInAsync(
        string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default, OAuthSignInOptions? options = null);

    /// <summary>Forgets the integration's sign-in: its tokens are deleted and it is shown as needing a sign-in.</summary>
    Task SignOutAsync(string integrationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps one of the keys a program or server was installed with (<paramref name="name"/> is the key's name as Settings lists it), typed by the user. It goes to the secret store and nowhere else; when
    /// every key has a value the app is shown as signed in and its tools are read, so that the user sees at once whether the keys work.
    /// </summary>
    Task<InstallOutcome> SetKeyAsync(string integrationId, string name, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives the integration a key or token the user made in the app themselves and typed in Settings (for an app whose server cannot sign the Assistant in by itself). It is
    /// kept in the secret store and sent as a bearer token.
    /// </summary>
    Task<InstallOutcome> UseTokenAsync(string integrationId, string token, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default <see cref="IIntegrationConnector"/>. The OAuth sign-in is <see cref="McpOAuthClient"/>'s; this records the result where the rest of the Assistant looks for it (the
/// registry for the integration, the secret store for the tokens) and checks that the app can then be reached and lists its tools. Logs say outcomes, never an address or a token.
/// </summary>
internal sealed partial class IntegrationConnector : IIntegrationConnector
{
    private static readonly TimeSpan CatalogBudget = TimeSpan.FromSeconds(15);

    private readonly IInstalledIntegrationRegistry _registry;
    private readonly McpOAuthClient _oauth;
    private readonly McpOAuthSessions _sessions;
    private readonly McpConnectionManager _connections;
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _clock;
    private readonly ILogger<IntegrationConnector> _logger;
    private readonly Func<string, string?> _programPath;
    private readonly IAppEventBus? _events;

    public IntegrationConnector(
        IInstalledIntegrationRegistry registry,
        McpOAuthClient oauth,
        McpOAuthSessions sessions,
        McpConnectionManager connections,
        ISettingsService settings,
        ISecretStore secrets,
        TimeProvider clock,
        ILogger<IntegrationConnector> logger,
        Func<string, string?>? programPath = null,
        IAppEventBus? events = null)
    {
        _programPath = programPath ?? BundledPrograms.Find;
        _events = events;
        _registry = registry;
        _oauth = oauth;
        _sessions = sessions;
        _connections = connections;
        _settings = settings;
        _secrets = secrets;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<InstallOutcome> ConnectAsync(
        KnownEndpoint endpoint, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default, OAuthSignInOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!KnownEndpoints.IsValid(endpoint))
        {
            return InstallOutcome.Fail(InstallFailure.NotAllowed, $"I cannot connect {endpoint.Name}: its address is not one I accept.");
        }

        progress?.Report(new InstallProgress(InstallStep.Preparing, $"Getting ready to connect {endpoint.Name}"));
        if (!endpoint.RunsOnThisPc && (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Privacy.LocalOnly)
        {
            return InstallOutcome.Fail(
                InstallFailure.WebLocked, $"{endpoint.Name} is reached over the internet, and Local Only mode is on. You can turn it off in Settings, under Privacy, then try again.");
        }

        if (endpoint.IsBundled && _programPath(endpoint.Program!) is null)
        {
            LogOutcome(_logger, "program_missing");
            return InstallOutcome.Fail(InstallFailure.SetupFailed, $"The small program that connects {endpoint.Name} is missing from the Assistant's folder, so I cannot connect it. Reinstalling the Assistant puts it back.");
        }

        if (endpoint.RunsOnThisPc && !await IsListeningAsync(new Uri(endpoint.Endpoint), cancellationToken).ConfigureAwait(false))
        {
            LogOutcome(_logger, "app_not_running");
            return InstallOutcome.Fail(
                InstallFailure.AppNotRunning,
                endpoint.SignInHelp is { } help ? $"I cannot reach {endpoint.Name} on this PC. {help}" : $"I cannot reach {endpoint.Name} on this PC. Make sure it is running.");
        }

        try
        {
            var existing = await _registry.GetAsync(endpoint.IntegrationId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                existing = await _registry.AddAsync(NewRecord(endpoint, _programPath), cancellationToken).ConfigureAwait(false);
            }

            var outcome = await SignInCoreAsync(existing, endpoint, progress, cancellationToken, options).ConfigureAwait(false);
            return outcome.Status == InstallStatus.Installed ? await AllowWhatItNeedsAsync(endpoint, outcome, cancellationToken).ConfigureAwait(false) : outcome;
        }
        catch (IntegrationException)
        {
            LogOutcome(_logger, "registry_failed");
            return InstallOutcome.Fail(InstallFailure.DiskFailed, $"I could not save {endpoint.Name} as connected. Nothing was changed.");
        }
    }

    // An app whose tools need a permission that is off (Beeper needs Messaging) could do nothing once connected, and the user who has just connected it would be
    // told only that a permission is off. Connecting it is the user saying they want it used, so the permission is turned on, and they are told that it was and
    // where it is. What the app is asked to change is still confirmed, each time.
    private async Task<InstallOutcome> AllowWhatItNeedsAsync(KnownEndpoint endpoint, InstallOutcome outcome, CancellationToken cancellationToken)
    {
        if (endpoint.Capability is not { } capability || !PermissionCatalog.Get(capability).IsAvailable
            || (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Permissions.IsOn(capability))
        {
            return outcome;
        }

        var saved = await _settings.UpdateAsync(current => current with { Permissions = current.Permissions.With(capability, true) }, cancellationToken).ConfigureAwait(false);
        if (_events is not null)
        {
            await _events.PublishAsync(new SettingsSaved(saved), cancellationToken).ConfigureAwait(false);
        }

        LogOutcome(_logger, "permission_turned_on");
        var title = PermissionCatalog.Get(capability).Title;
        return outcome with
        {
            Message = $"{outcome.Message} {title} was turned on in Settings, under Permissions, so that the Assistant can use {endpoint.Name}; you are still asked before anything is sent or changed.",
        };
    }

    /// <inheritdoc/>
    public async Task<InstallOutcome> SignInAsync(
        string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default, OAuthSignInOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(integrationId);
        try
        {
            if (await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false) is not { } record)
            {
                return InstallOutcome.Fail(InstallFailure.NotAllowed, "That app is not connected.");
            }

            var endpoint = KnownEndpoints.All.FirstOrDefault(known => known.IntegrationId == record.Id);
            return await SignInCoreAsync(record, endpoint, progress, cancellationToken, options).ConfigureAwait(false);
        }
        catch (IntegrationException)
        {
            return InstallOutcome.Fail(InstallFailure.DiskFailed, "I could not save the sign-in. Nothing was changed.");
        }
    }

    /// <inheritdoc/>
    public async Task SignOutAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(integrationId);
        await _sessions.DeleteAsync(integrationId, cancellationToken).ConfigureAwait(false);
        await _registry.UpdateAsync(
            integrationId,
            record => record with
            {
                Authentication = record.Authentication with { State = IntegrationAuthState.NeedsSignIn, Secrets = [], ExpiresAt = null, CheckedAt = _clock.GetUtcNow() },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<InstallOutcome> UseTokenAsync(string integrationId, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(integrationId);
        var trimmed = (token ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > SecretNames.MaxSecretLength || trimmed.Any(char.IsControl) || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            return InstallOutcome.Fail(InstallFailure.NotAllowed, "That does not look like an access token.");
        }

        try
        {
            if (await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false) is not { } record || record.Transport.Kind == McpTransportKind.Stdio)
            {
                return InstallOutcome.Fail(InstallFailure.NotAllowed, "That app is not connected.");
            }

            var name = $"{record.Id}.token";
            await _secrets.SetAsync(name, trimmed, cancellationToken).ConfigureAwait(false);
            await _sessions.DeleteAsync(record.Id, cancellationToken).ConfigureAwait(false);
            record = await _registry.UpdateAsync(
                record.Id,
                current => current with
                {
                    Authentication = new IntegrationAuthentication
                    {
                        Kind = IntegrationAuthKind.BearerToken,
                        State = IntegrationAuthState.Ready,
                        Secrets = [new IntegrationSecretBinding("Authorization", name)],
                        CheckedAt = _clock.GetUtcNow(),
                    },
                    Health = IntegrationHealth.Unknown,
                },
                cancellationToken).ConfigureAwait(false);
            return await CheckAsync(record, KnownEndpoints.All.FirstOrDefault(known => known.IntegrationId == record.Id)?.Name ?? record.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IntegrationException or SecretStoreException)
        {
            return InstallOutcome.Fail(InstallFailure.DiskFailed, "I could not keep the token. Nothing was changed.");
        }
    }

    /// <inheritdoc/>
    public async Task<InstallOutcome> SetKeyAsync(string integrationId, string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(integrationId);
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > SecretNames.MaxSecretLength || trimmed.Any(char.IsControl))
        {
            return InstallOutcome.Fail(InstallFailure.NotAllowed, "That does not look like a key.");
        }

        try
        {
            if (await _registry.GetAsync(integrationId, cancellationToken).ConfigureAwait(false) is not { } record
                || record.Authentication.Kind is not (IntegrationAuthKind.EnvironmentSecret or IntegrationAuthKind.HeaderKey)
                || record.Authentication.Secrets.FirstOrDefault(binding => string.Equals(binding.Target, name, StringComparison.Ordinal)) is not { } binding)
            {
                return InstallOutcome.Fail(InstallFailure.NotAllowed, "That app does not ask for such a key.");
            }

            await _secrets.SetAsync(binding.SecretName, trimmed, cancellationToken).ConfigureAwait(false);
            var missing = false;
            foreach (var other in record.Authentication.Secrets)
            {
                if (string.IsNullOrEmpty(await _secrets.GetAsync(other.SecretName, cancellationToken).ConfigureAwait(false)))
                {
                    missing = true;
                }
            }

            if (missing)
            {
                return new InstallOutcome { Status = InstallStatus.Installed, Message = $"The key is kept. {record.Name} still needs its other keys.", Integration = record };
            }

            var updated = await _registry.UpdateAsync(
                record.Id,
                current => current with { Authentication = current.Authentication with { State = IntegrationAuthState.Ready, CheckedAt = _clock.GetUtcNow() }, Health = IntegrationHealth.Unknown },
                cancellationToken).ConfigureAwait(false);
            return await CheckAsync(updated, record.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IntegrationException or SecretStoreException)
        {
            return InstallOutcome.Fail(InstallFailure.DiskFailed, "I could not keep the key. Nothing was changed.");
        }
    }

    private async Task<InstallOutcome> SignInCoreAsync(
        InstalledIntegration record, KnownEndpoint? known, IProgress<InstallProgress>? progress, CancellationToken cancellationToken, OAuthSignInOptions? options = null)
    {
        var name = known?.Name ?? record.Name;
        var bundled = known is { IsBundled: true } && record.Transport.Kind == McpTransportKind.Stdio;
        if (!bundled && (record.Transport.Kind == McpTransportKind.Stdio || !Uri.TryCreate(record.Transport.Endpoint, UriKind.Absolute, out _)))
        {
            return InstallOutcome.Fail(InstallFailure.NotAllowed, $"{name} does not sign in this way.");
        }

        progress?.Report(new InstallProgress(
            InstallStep.Checking,
            options?.PrivateWindow == true ? $"A private browser window is opening so you can sign in to {name}. I will wait." : $"Your browser is opening so you can sign in to {name}. I will wait."));
        OAuthSignIn signIn;
        try
        {
            if (bundled)
            {
                // A bundled program's service has a sign-in that is known, with the client the user chose in Settings or the default one.
                var chosen = (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Integrations.MicrosoftClientId;
                signIn = await _oauth.SignInAsync(known!.FixedSignIn!.For(chosen), timeout: null, cancellationToken, options).ConfigureAwait(false);
            }
            else
            {
                signIn = await _oauth.SignInAsync(new Uri(record.Transport.Endpoint!), "Assistant", known?.Scope, timeout: null, cancellationToken, options).ConfigureAwait(false);
            }
        }
        catch (OAuthException exception)
        {
            LogOutcome(_logger, "sign_in_" + exception.Failure.ToString().ToLowerInvariant());
            return InstallOutcome.Fail(InstallFailure.SignInFailed, SignInFailureText(name, exception.Failure, known));
        }

        progress?.Report(new InstallProgress(InstallStep.Finishing, $"Saving your sign-in to {name}"));
        IReadOnlyList<IntegrationSecretBinding> bindings;
        try
        {
            bindings = await _sessions.SaveAsync(record.Id, signIn, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SecretStoreException or ArgumentException or OAuthException)
        {
            LogOutcome(_logger, "keeping_failed");
            return InstallOutcome.Fail(InstallFailure.DiskFailed, $"You signed in to {name}, but I could not keep the sign-in safely, so I did not use it.");
        }

        var updated = await _registry.UpdateAsync(
            record.Id,
            current => current with
            {
                Enabled = true,
                Authentication = new IntegrationAuthentication
                {
                    Kind = IntegrationAuthKind.OAuth,
                    State = IntegrationAuthState.Ready,
                    Secrets = bindings,
                    ExpiresAt = signIn.Tokens.ExpiresAt,
                    CheckedAt = _clock.GetUtcNow(),
                    TokenVariable = bundled ? known!.TokenVariable : null,
                },
                Health = IntegrationHealth.Unknown,
            },
            cancellationToken).ConfigureAwait(false);
        LogOutcome(_logger, "signed_in");
        return await CheckAsync(updated, name, cancellationToken).ConfigureAwait(false);
    }

    // Connects once with the new sign-in, which lists the app's tools and records their names for the requests that follow.
    private async Task<InstallOutcome> CheckAsync(InstalledIntegration record, string name, CancellationToken cancellationToken)
    {
        McpToolCatalog? catalog = null;
        try
        {
            catalog = await _connections.GetCatalogAsync(record.Id, CatalogBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (McpException)
        {
            // Signed in, but the app did not answer: it stays connected, and the next request tries again.
        }

        var current = await _registry.GetAsync(record.Id, cancellationToken).ConfigureAwait(false) ?? record;
        var tools = catalog is null ? [] : catalog.Tools.Select(tool => tool.Descriptor.Name).Take(IntegrationRules.MaxToolNames).ToList();
        return new InstallOutcome
        {
            Status = InstallStatus.Installed,
            Message = catalog is null
                ? $"You are signed in to {name}, but I could not read what it offers just now. I will try again when you ask."
                : $"{name} is connected.",
            Integration = current,
            ToolNames = tools,
        };
    }

    private static InstalledIntegration NewRecord(KnownEndpoint endpoint, Func<string, string?> programPath) => new()
    {
        Id = endpoint.IntegrationId,
        Name = endpoint.Name,
        Source = new IntegrationSource(KnownEndpoints.For(endpoint.AppKey) is not null ? IntegrationSourceKind.Bundled : IntegrationSourceKind.UserAdded,
            endpoint.IsBundled ? endpoint.Program : new Uri(endpoint.Endpoint).Host),
        Transport = endpoint.IsBundled
            ? new IntegrationTransport { Kind = McpTransportKind.Stdio, Command = programPath(endpoint.Program!) }
            : new IntegrationTransport { Kind = McpTransportKind.StreamableHttp, Endpoint = endpoint.Endpoint },
        Enabled = true,
        Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.OAuth, State = IntegrationAuthState.NeedsSignIn, TokenVariable = endpoint.TokenVariable },
        Permissions = new IntegrationPermissions { LeavesThisPc = !endpoint.RunsOnThisPc, RequiredCapability = endpoint.Capability },
        Managed = null,
    };

    private static string SignInFailureText(string name, OAuthFailure failure, KnownEndpoint? known) => failure switch
    {
        OAuthFailure.TimedOut => $"Nobody finished signing in to {name} in time, so nothing was connected. Ask me again when you are ready.",
        OAuthFailure.Denied => known is { IsBundled: true }
            ? $"Signing in to {name} was not allowed, so nothing was connected. A school or work account may need its organization's approval: choose 'Sign in in a private window' and pick another account, such as a personal one."
            : $"Signing in to {name} was not allowed, so nothing was connected.",
        OAuthFailure.Cancelled => $"I stopped before you signed in to {name}, so nothing was connected.",
        OAuthFailure.BrowserFailed => $"I could not open your browser to sign in to {name}.",
        OAuthFailure.PrivateBrowserFailed => $"I could not open a private browser window to sign in to {name}. Choose 'Sign in' instead, or copy the sign-in link and open it in the browser you want.",
        OAuthFailure.NoRegistration => known is { RunsOnThisPc: true }
            ? $"{name} does not let the Assistant sign itself in. In {name}, create an access token for it (Settings, then Developers), and paste it in the Assistant's Settings, under Integrations."
            : $"{name} does not let the Assistant sign itself in, so I cannot connect it.",
        OAuthFailure.NotSupported => $"{name} does not offer a sign-in I can use, so I cannot connect it.",
        OAuthFailure.TokenRefused => $"{name} did not accept the sign-in, so nothing was connected.",
        _ => $"I could not reach {name} to sign in. Check your connection, or that it is running, and try again.",
    };

    // Whether anything is listening where a program on this PC keeps its server: a connection that is accepted or answered is enough.
    private static async Task<bool> IsListeningAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(endpoint.Host == "localhost" ? "127.0.0.1" : endpoint.Host, endpoint.Port, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 3270, Level = LogLevel.Information, Message = "Connecting a known app: {Outcome}")]
    private static partial void LogOutcome(ILogger logger, string outcome);
}
