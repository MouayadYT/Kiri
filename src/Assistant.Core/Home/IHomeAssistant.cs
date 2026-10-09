namespace Assistant.Core.Home;

/// <summary>
/// A thing in the user's Home Assistant, as Home Assistant lists it: a light, a switch, a fan, a sensor. Its name is the user's own (private content,
/// PROJECT_SPEC §3.2), so it is shown and given to the model for the request it was asked for, and never logged.
/// </summary>
/// <param name="Id">Home Assistant's id for it, such as <c>switch.window_fan</c>: its kind, a dot, and a name of lower case letters, digits and underscores.</param>
/// <param name="Name">What the user calls it in Home Assistant, such as Window Fan.</param>
/// <param name="State">What it is now, as Home Assistant says it: <c>on</c>, <c>off</c>, a number, <c>unavailable</c>.</param>
/// <param name="Unit">The unit of a reading (°C, %), or <see langword="null"/>.</param>
public sealed record HomeDevice(string Id, string Name, string State, string? Unit = null)
{
    /// <summary>What kind of thing it is, as Home Assistant names it: <c>light</c>, <c>switch</c>, <c>fan</c>, <c>climate</c>, <c>sensor</c>.</summary>
    public string Kind => Id[..Id.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>Whether Home Assistant cannot reach it now.</summary>
    public bool IsUnavailable => State is "unavailable" or "unknown";

    /// <summary>Keeps the name out of logs.</summary>
    public override string ToString() => $"HomeDevice {{ Kind = {Kind} }}";
}

/// <summary>Why Home Assistant could not be used.</summary>
public enum HomeFailure
{
    /// <summary>It could be.</summary>
    None = 0,

    /// <summary>No Home Assistant is connected.</summary>
    NotConnected = 1,

    /// <summary>The address or the token is not one that can be used (not an address, a token with spaces, plain http to the internet).</summary>
    Invalid = 2,

    /// <summary>Home Assistant is outside the user's own network and Local Only mode is on.</summary>
    LocalOnly = 3,

    /// <summary>Nothing answered at the address, or what answered is not Home Assistant.</summary>
    Unreachable = 4,

    /// <summary>Home Assistant did not accept the token.</summary>
    Unauthorized = 5,

    /// <summary>Home Assistant answered that it could not do what was asked.</summary>
    Refused = 6,

    /// <summary>The address and the token could not be kept on this PC.</summary>
    NotKept = 7,
}

/// <summary>Whether a Home Assistant is connected, and what was found when it was last asked.</summary>
/// <param name="Failure"><see cref="HomeFailure.None"/> when it answered with the token.</param>
/// <param name="Address">Where it is, as it is kept (<c>http://192.168.1.20:8123</c>), or <see langword="null"/> when none is connected.</param>
/// <param name="Devices">How many things it lists, when it answered.</param>
public sealed record HomeStatus(HomeFailure Failure, string? Address, int Devices = 0)
{
    /// <summary>Whether it answered.</summary>
    public bool IsReady => Failure == HomeFailure.None;
}

/// <summary>What Home Assistant lists, or why it could not be asked.</summary>
public sealed record HomeDevices(HomeFailure Failure, IReadOnlyList<HomeDevice> Devices)
{
    /// <summary>A list that could not be read.</summary>
    public static HomeDevices Failed(HomeFailure failure) => new(failure, []);
}

/// <summary>What came of asking Home Assistant to do something.</summary>
/// <param name="Failure"><see cref="HomeFailure.None"/> when Home Assistant took the request.</param>
/// <param name="State">What the thing is afterwards, read back, or <see langword="null"/> when it could not be read.</param>
public sealed record HomeCallResult(HomeFailure Failure, string? State = null)
{
    /// <summary>Whether Home Assistant took the request.</summary>
    public bool Done => Failure == HomeFailure.None;
}

/// <summary>
/// The user's Home Assistant (PROJECT_SPEC §4.8), reached through its own REST API with a long-lived access token the user made: what it lists, and
/// its services (turn on, set the temperature). The address and the token are kept in Windows' credential store, never in a file or a log. A Home
/// Assistant on the user's own network is used while Local Only mode is on; one reached over the internet is not.
/// </summary>
public interface IHomeAssistant
{
    /// <summary>Raised after a Home Assistant was connected or disconnected.</summary>
    event EventHandler? Changed;

    /// <summary>Whether a Home Assistant is connected, as far as is known without asking (see <see cref="LoadAsync"/>).</summary>
    bool IsConnected { get; }

    /// <summary>Where the connected Home Assistant is, as it is kept (<c>http://192.168.1.20:8123</c>), or <see langword="null"/> when none is connected.</summary>
    string? Address { get; }

    /// <summary>What it listed when it was last asked; empty until then.</summary>
    IReadOnlyList<HomeDevice> Known { get; }

    /// <summary>Reads whether one is connected, the first time it is called, and after that brings <see cref="Known"/> up to date when it is old.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes over the connection an earlier version of the app kept for Home Assistant in another form, when there is one and none is connected. The app does
    /// it once, when it starts; nothing else does, so that only the running app ever changes what the user has connected.
    /// </summary>
    Task TakeOverEarlierAsync(CancellationToken cancellationToken = default);

    /// <summary>Connects the Home Assistant at <paramref name="address"/> with <paramref name="token"/>: it is asked first, and kept only when it answers.</summary>
    Task<HomeStatus> ConnectAsync(string address, string token, CancellationToken cancellationToken = default);

    /// <summary>Asks the connected Home Assistant whether it is there and takes the token.</summary>
    Task<HomeStatus> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets the address and the token.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Everything Home Assistant lists, with what each is now.</summary>
    Task<HomeDevices> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls one of Home Assistant's services for one thing, such as <c>light</c>.<c>turn_on</c> for <c>light.bedroom</c>, with the service's own
    /// <paramref name="data"/> (a brightness, a temperature), and reads back what the thing is afterwards.
    /// </summary>
    Task<HomeCallResult> CallAsync(
        string domain, string service, string deviceId, IReadOnlyDictionary<string, double>? data = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="CallAsync(string, string, string, IReadOnlyDictionary{string, double}?, CancellationToken)"/>, for a thing that is expected to be
    /// <paramref name="expected"/> afterwards ("on", "off"): while it is not, it is read again for up to about three seconds, so that a light that takes
    /// two seconds to say it is off is not said to have ignored the request. Without an expectation, or without an override, it is read back once.
    /// </summary>
    Task<HomeCallResult> CallAsync(
        string domain, string service, string deviceId, IReadOnlyDictionary<string, double>? data, string? expected, CancellationToken cancellationToken) =>
        CallAsync(domain, service, deviceId, data, cancellationToken);
}
