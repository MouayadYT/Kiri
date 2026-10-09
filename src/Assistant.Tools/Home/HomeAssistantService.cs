using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Contracts;
using Assistant.Core.Home;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Home;

/// <summary>
/// The default <see cref="IHomeAssistant"/>: Home Assistant's REST API (<c>/api/states</c>, <c>/api/services</c>), which every Home Assistant has, with
/// the long-lived access token the user pasted. Nothing has to be added to Home Assistant for it. The address and the token are kept in the secret
/// store; logs say which step ended how, never an address, a token or a device's name. An address must be <c>https</c>, or <c>http</c> on this PC
/// or the user's own network, so the token never crosses the internet in the clear.
/// </summary>
public sealed partial class HomeAssistantService : IHomeAssistant, IDisposable
{
    /// <summary>The secret the address is kept under.</summary>
    public const string AddressSecret = "homeassistant.address";

    /// <summary>The secret the access token is kept under.</summary>
    public const string TokenSecret = "homeassistant.access";

    /// <summary>The port Home Assistant listens on unless it was changed, tried when an address gives none.</summary>
    public const int DefaultPort = 8123;

    // The connection an earlier version made for Home Assistant as an MCP server, which most Home Assistants are not: its address and token are taken over.
    internal const string EarlierIntegrationId = "homeassistant";

    private const int MaxAddressLength = 300;
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxDevices = 4000;
    private const int MaxNameLength = 80;

    // A list read this recently is the list; Known is read again when it is older than KnownFor.
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KnownFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KnownBudget = TimeSpan.FromSeconds(3);

    // How long a thing is given to become what it was asked to be, before it is read back; and, when it is known what it should become and it has not
    // yet, how long it is read again for (some lights take two seconds to say so).
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);
    internal static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(3);

    private readonly ISettingsService _settings;
    private readonly ISecretStore? _secrets;
    private readonly IInstalledIntegrationRegistry? _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger<HomeAssistantService> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Uri? _address;
    private bool _loaded;
    private IReadOnlyList<HomeDevice> _known = [];
    private DateTimeOffset? _readAt;

    public HomeAssistantService(
        ISettingsService settings, ISecretStore? secrets, TimeProvider clock, ILogger<HomeAssistantService> logger,
        IInstalledIntegrationRegistry? registry = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _secrets = secrets;
        _clock = clock;
        _logger = logger;
        _registry = registry;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public bool IsConnected => _address is not null;

    /// <inheritdoc/>
    public string? Address => _address is { } address ? Text(address) : null;

    /// <inheritdoc/>
    public IReadOnlyList<HomeDevice> Known => _known;

    /// <summary>
    /// The address as it is kept: the scheme, the host and the port, with <c>http://</c> put before one that has no scheme. <see langword="null"/> for
    /// what is not an address, has a user name in it, or is plain <c>http</c> to somewhere that is not this PC or the user's own network.
    /// </summary>
    public static Uri? Normalize(string? address)
    {
        var text = (address ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > MaxAddressLength || text.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.UserInfo.Length > 0
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var root = new Uri(uri.GetLeftPart(UriPartial.Authority));
        return McpNetwork.IsAllowed(root) ? root : null;
    }

    /// <inheritdoc/>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!_loaded)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_loaded)
                {
                    await ReadKeptAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        if (_address is null || (_readAt is { } read && _clock.GetUtcNow() - read < KnownFor))
        {
            return;
        }

        // Before a request, so that the tools know the devices' names; a Home Assistant that is slow or away is not waited for.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(KnownBudget);
        try
        {
            await GetDevicesAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <inheritdoc/>
    public async Task<HomeStatus> ConnectAsync(string address, string token, CancellationToken cancellationToken = default)
    {
        var tidy = TidyToken(token);
        if (Normalize(address) is not { } uri || tidy is null)
        {
            return new HomeStatus(HomeFailure.Invalid, null);
        }

        if (await IsKeptLocalAsync(uri, cancellationToken).ConfigureAwait(false))
        {
            return new HomeStatus(HomeFailure.LocalOnly, null);
        }

        var failure = await ProbeAsync(uri, tidy, cancellationToken).ConfigureAwait(false);
        if (failure == HomeFailure.Unreachable && uri.IsDefaultPort && uri.Scheme == Uri.UriSchemeHttp)
        {
            // An address with no port is tried at Home Assistant's own before it is given up on.
            var usual = new UriBuilder(uri) { Port = DefaultPort }.Uri;
            if (await ProbeAsync(usual, tidy, cancellationToken).ConfigureAwait(false) == HomeFailure.None)
            {
                (uri, failure) = (usual, HomeFailure.None);
            }
        }

        if (failure != HomeFailure.None)
        {
            LogOutcome(_logger, "connect", failure);
            return new HomeStatus(failure, null);
        }

        if (_secrets is null)
        {
            return new HomeStatus(HomeFailure.NotKept, null);
        }

        try
        {
            await _secrets.SetAsync(AddressSecret, Text(uri), cancellationToken).ConfigureAwait(false);
            await _secrets.SetAsync(TokenSecret, tidy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SecretStoreException or ArgumentException)
        {
            LogOutcome(_logger, "connect", HomeFailure.NotKept);
            return new HomeStatus(HomeFailure.NotKept, null);
        }

        _address = uri;
        _loaded = true;
        _known = [];
        _readAt = null;
        await ForgetEarlierAsync(cancellationToken).ConfigureAwait(false);
        var devices = await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        LogOutcome(_logger, "connect", HomeFailure.None);
        Changed?.Invoke(this, EventArgs.Empty);
        return new HomeStatus(HomeFailure.None, Text(uri), devices.Devices.Count);
    }

    /// <inheritdoc/>
    public async Task<HomeStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        var (uri, token, failure) = await ReadyAsync(cancellationToken).ConfigureAwait(false);
        if (failure != HomeFailure.None)
        {
            return new HomeStatus(failure, uri is null ? null : Text(uri));
        }

        failure = await ProbeAsync(uri!, token!, cancellationToken).ConfigureAwait(false);
        if (failure != HomeFailure.None)
        {
            LogOutcome(_logger, "check", failure);
            return new HomeStatus(failure, Text(uri!));
        }

        var devices = await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        return new HomeStatus(devices.Failure, Text(uri!), devices.Devices.Count);
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _address = null;
        _loaded = true;
        _known = [];
        _readAt = null;
        if (_secrets is not null)
        {
            try
            {
                await _secrets.DeleteAsync(TokenSecret, cancellationToken).ConfigureAwait(false);
                await _secrets.DeleteAsync(AddressSecret, cancellationToken).ConfigureAwait(false);
            }
            catch (SecretStoreException)
            {
                // What could not be deleted is not used: nothing is connected any more.
            }
        }

        LogOutcome(_logger, "disconnect", HomeFailure.None);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public async Task<HomeDevices> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var (uri, token, failure) = await ReadyAsync(cancellationToken).ConfigureAwait(false);
        if (failure != HomeFailure.None)
        {
            return HomeDevices.Failed(failure);
        }

        if (_readAt is { } read && _clock.GetUtcNow() - read < Fresh)
        {
            return new HomeDevices(HomeFailure.None, _known);
        }

        var (status, body) = await SendAsync(HttpMethod.Get, new Uri(uri!, "/api/states"), token!, null, cancellationToken).ConfigureAwait(false);
        if (status != HomeFailure.None || body is null)
        {
            LogOutcome(_logger, "list", status == HomeFailure.None ? HomeFailure.Unreachable : status);
            return HomeDevices.Failed(status == HomeFailure.None ? HomeFailure.Unreachable : status);
        }

        var devices = ReadDevices(body);
        if (devices is null)
        {
            LogOutcome(_logger, "list", HomeFailure.Unreachable);
            return HomeDevices.Failed(HomeFailure.Unreachable);
        }

        _known = devices;
        _readAt = _clock.GetUtcNow();
        return new HomeDevices(HomeFailure.None, devices);
    }

    /// <inheritdoc/>
    public Task<HomeCallResult> CallAsync(
        string domain, string service, string deviceId, IReadOnlyDictionary<string, double>? data = null, CancellationToken cancellationToken = default) =>
        CallAsync(domain, service, deviceId, data, null, cancellationToken);

    /// <inheritdoc/>
    public async Task<HomeCallResult> CallAsync(
        string domain, string service, string deviceId, IReadOnlyDictionary<string, double>? data, string? expected, CancellationToken cancellationToken)
    {
        if (!NamePattern().IsMatch(domain ?? string.Empty) || !NamePattern().IsMatch(service ?? string.Empty) || !DevicePattern().IsMatch(deviceId ?? string.Empty)
            || data?.Keys.Any(key => !NamePattern().IsMatch(key)) == true)
        {
            return new HomeCallResult(HomeFailure.Invalid);
        }

        var (uri, token, failure) = await ReadyAsync(cancellationToken).ConfigureAwait(false);
        if (failure != HomeFailure.None)
        {
            return new HomeCallResult(failure);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("entity_id", deviceId);
            foreach (var (key, value) in data ?? new Dictionary<string, double>())
            {
                writer.WriteNumber(key, value);
            }

            writer.WriteEndObject();
        }

        var (status, _) = await SendAsync(
            HttpMethod.Post, new Uri(uri!, $"/api/services/{domain}/{service}"), token!, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        LogOutcome(_logger, "call", status);
        if (status != HomeFailure.None)
        {
            return new HomeCallResult(status);
        }

        // What was listed is out of date now; the thing itself is read back once it has had a moment to change.
        _readAt = null;
        var started = _clock.GetTimestamp();
        while (true)
        {
            await Task.Delay(Settle, _clock, cancellationToken).ConfigureAwait(false);
            var (read, body) = await SendAsync(HttpMethod.Get, new Uri(uri!, "/api/states/" + deviceId), token!, null, cancellationToken).ConfigureAwait(false);
            var state = read == HomeFailure.None && body is not null ? ReadState(body) : null;

            // It is what it was asked to be, or nothing says what that is, or it is not a thing that is on or off (a vacuum on its way back is
            // "returning"), or it has had its three seconds: that is the answer.
            if (expected is null || state is not ("on" or "off") || string.Equals(state, expected, StringComparison.Ordinal)
                || _clock.GetElapsedTime(started) + Settle > SettleLimit)
            {
                return new HomeCallResult(HomeFailure.None, state);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    // The address as text, with no slash after it.
    private static string Text(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    // A token as it is sent: one run of characters, with a pasted "Bearer " taken off.
    private static string? TidyToken(string? token)
    {
        var text = (token ?? string.Empty).Trim();
        if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..].Trim();
        }

        return text.Length == 0 || text.Length > SecretNames.MaxSecretLength || text.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ? null : text;
    }

    // What is kept is read once.
    private async Task ReadKeptAsync(CancellationToken cancellationToken)
    {
        if (_secrets is null)
        {
            _loaded = true;
            return;
        }

        try
        {
            if (Normalize(await _secrets.GetAsync(AddressSecret, cancellationToken).ConfigureAwait(false)) is { } kept)
            {
                _address = kept;
            }

            _loaded = true;
        }
        catch (SecretStoreException)
        {
            // Not read this time: nothing is connected for now, and the next request tries again.
            LogOutcome(_logger, "load", HomeFailure.NotKept);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The earlier version connected Home Assistant as an MCP server at its address with a pasted token. Both are what this needs, so they are kept as its own,
    /// and the MCP connection, which could reach nothing on a Home Assistant without that add-on, is removed.
    /// </remarks>
    public async Task TakeOverEarlierAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (_address is not null || !_loaded || _registry is null || _secrets is null)
        {
            return;
        }

        try
        {
            if (await _registry.GetAsync(EarlierIntegrationId, cancellationToken).ConfigureAwait(false) is not { } earlier
                || earlier.Authentication.Kind != IntegrationAuthKind.BearerToken
                || earlier.Authentication.Secrets.FirstOrDefault() is not { } binding
                || Normalize(earlier.Transport.Endpoint) is not { } uri
                || TidyToken(await _secrets.GetAsync(binding.SecretName, cancellationToken).ConfigureAwait(false)) is not { } token)
            {
                return;
            }

            await _secrets.SetAsync(AddressSecret, Text(uri), cancellationToken).ConfigureAwait(false);
            await _secrets.SetAsync(TokenSecret, token, cancellationToken).ConfigureAwait(false);
            _address = uri;
            await _registry.RemoveAsync(EarlierIntegrationId, cancellationToken).ConfigureAwait(false);
            LogOutcome(_logger, "takeover", HomeFailure.None);
        }
        catch (Exception exception) when (exception is SecretStoreException or IntegrationException or ArgumentException)
        {
            // Left as it was: the user can connect Home Assistant in Settings.
            LogOutcome(_logger, "takeover", HomeFailure.NotKept);
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ForgetEarlierAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_registry is not null)
            {
                await _registry.RemoveAsync(EarlierIntegrationId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IntegrationException)
        {
            // It stays listed, and can be removed in Settings.
        }
    }

    // The address and the token of the connected Home Assistant, or why it cannot be used now.
    private async Task<(Uri? Address, string? Token, HomeFailure Failure)> ReadyAsync(CancellationToken cancellationToken)
    {
        if (_address is not { } uri || _secrets is null)
        {
            return (null, null, HomeFailure.NotConnected);
        }

        if (await IsKeptLocalAsync(uri, cancellationToken).ConfigureAwait(false))
        {
            return (uri, null, HomeFailure.LocalOnly);
        }

        try
        {
            return TidyToken(await _secrets.GetAsync(TokenSecret, cancellationToken).ConfigureAwait(false)) is { } token
                ? (uri, token, HomeFailure.None)
                : (uri, null, HomeFailure.NotConnected);
        }
        catch (SecretStoreException)
        {
            return (uri, null, HomeFailure.NotKept);
        }
    }

    // Whether Local Only mode keeps the Assistant from this address: one that is not on this PC or the user's own network.
    private async Task<bool> IsKeptLocalAsync(Uri uri, CancellationToken cancellationToken) =>
        !uri.IsLoopback && !McpNetwork.IsOwnNetwork(uri) && (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Privacy.LocalOnly;

    // Home Assistant's API answers "API running." to its own token, 401 to another, and anything else is not it.
    private async Task<HomeFailure> ProbeAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        var (failure, body) = await SendAsync(HttpMethod.Get, new Uri(uri, "/api/"), token, null, cancellationToken).ConfigureAwait(false);
        if (failure != HomeFailure.None)
        {
            return failure == HomeFailure.Refused ? HomeFailure.Unreachable : failure;
        }

        try
        {
            using var document = JsonDocument.Parse(body ?? []);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out _) ? HomeFailure.None : HomeFailure.Unreachable;
        }
        catch (JsonException)
        {
            return HomeFailure.Unreachable;
        }
    }

    private async Task<(HomeFailure Failure, byte[]? Body)> SendAsync(HttpMethod method, Uri address, string token, byte[]? json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json is not null)
        {
            request.Content = new ByteArrayContent(json);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = Encoding.UTF8.WebName };
        }

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (HomeFailure.Unauthorized, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Home Assistant says 400 or 404 to a service it does not have or a value it will not take; anything else is not an answer of its API.
                return (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound ? HomeFailure.Refused : HomeFailure.Unreachable, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > MaxResponseBytes)
                {
                    return (HomeFailure.Unreachable, null);
                }

                body.Write(buffer, 0, read);
            }

            return (HomeFailure.None, body.ToArray());
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (HomeFailure.Unreachable, null);
        }
    }

    // The list of /api/states, or null when it is not one.
    private static List<HomeDevice>? ReadDevices(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var devices = new List<HomeDevice>();
            foreach (var entity in document.RootElement.EnumerateArray())
            {
                if (devices.Count >= MaxDevices)
                {
                    break;
                }

                if (entity.ValueKind != JsonValueKind.Object || String(entity, "entity_id") is not { } id || !DevicePattern().IsMatch(id))
                {
                    continue;
                }

                var attributes = entity.TryGetProperty("attributes", out var found) && found.ValueKind == JsonValueKind.Object ? found : default;
                var name = Tidy(attributes.ValueKind == JsonValueKind.Object ? String(attributes, "friendly_name") : null);
                var unit = Tidy(attributes.ValueKind == JsonValueKind.Object ? String(attributes, "unit_of_measurement") : null);
                devices.Add(new HomeDevice(
                    id,
                    name.Length > 0 ? name : id[(id.IndexOf('.', StringComparison.Ordinal) + 1)..].Replace('_', ' '),
                    Tidy(String(entity, "state")),
                    unit.Length > 0 ? unit : null));
            }

            return devices;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadState(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && Tidy(String(document.RootElement, "state")) is { Length: > 0 } state ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // One line of text, of a length that can be shown: what a server says is never trusted to be either.
    private static string Tidy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var line = string.Join(' ', new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= MaxNameLength ? line : line[..MaxNameLength];
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,63}\.[a-z0-9_]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();

    [LoggerMessage(EventId = 6410, Level = LogLevel.Information, Message = "Home Assistant: {Step} ended with {Outcome}")]
    private static partial void LogOutcome(ILogger logger, string step, HomeFailure outcome);
}
