using Assistant.Core.Home;
using Assistant.Tools.Home;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;

namespace Assistant.UI.Onboarding;

/// <summary>What the setup and Settings say about the user's Home Assistant: how connecting it went, and how it is now.</summary>
internal static class HomeAssistantWords
{
    /// <summary>What is said after the form's Connect.</summary>
    public static string Connected(HomeStatus status) => status.Failure switch
    {
        HomeFailure.None => status.Devices == 1
            ? "Home Assistant is connected: 1 device found. Try asking the Assistant to turn it on."
            : $"Home Assistant is connected: {status.Devices:N0} devices found. Try “turn on the …” with one of their names.",
        HomeFailure.Invalid =>
            "Enter your Home Assistant's address, such as http://homeassistant.local:8123 or http://192.168.1.20:8123, and the whole access token. Plain http works only on your own network.",
        HomeFailure.LocalOnly => "That Home Assistant is reached over the internet, and Local Only mode is on. Turn it off in Settings, under Privacy, then try again.",
        HomeFailure.Unauthorized => "Home Assistant did not accept that token. Make a new long-lived access token in Home Assistant and paste all of it.",
        HomeFailure.NotKept => "The address and the token could not be saved in Windows Credential Manager, so nothing was changed.",
        _ => "Nothing answered as Home Assistant at that address. Check the address and its port (usually 8123), and that this PC is on the same network.",
    };

    /// <summary>How the connected Home Assistant is, in a few words, for its line in Settings.</summary>
    public static string Health(HomeStatus status) => status.Failure switch
    {
        HomeFailure.None => status.Devices == 1 ? "Connected · 1 device" : $"Connected · {status.Devices:N0} devices",
        HomeFailure.LocalOnly => "Not used while Local Only mode is on: it is reached over the internet",
        HomeFailure.Unauthorized => "Home Assistant no longer accepts the token",
        HomeFailure.NotKept or HomeFailure.NotConnected => "The token could not be read; connect it again",
        _ => "Did not answer just now",
    };

    /// <summary>Whether <paramref name="status"/> is something the user has to look at.</summary>
    public static bool IsProblem(HomeStatus status) => status.Failure != HomeFailure.None;

    /// <summary>
    /// The Home Assistant at <paramref name="address"/> as a server that is not on this PC or the user's own network, which Local Only mode has to be turned
    /// off for; <see langword="null"/> when it is on their network, or is not an address at all.
    /// </summary>
    public static KnownEndpoint? NeedsInternet(string? address) =>
        HomeAssistantService.Normalize(address) is { } uri && !uri.IsLoopback && !McpNetwork.IsOwnNetwork(uri)
            ? new KnownEndpoint(ConnectionsSetupViewModel.HomeAssistant.AppKey, ConnectionsSetupViewModel.HomeAssistant.Name, uri.AbsoluteUri, uri.Host)
            : null;
}
