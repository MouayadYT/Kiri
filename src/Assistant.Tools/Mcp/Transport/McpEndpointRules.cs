namespace Assistant.Tools.Mcp;

/// <summary>
/// What an MCP server's address may be (PROJECT_SPEC §3.4, step 104): an absolute <c>https</c> address, or <c>http</c> only when it is on this PC
/// (<c>localhost</c>, <c>127.0.0.1</c>, <c>[::1]</c>), so that nothing the Assistant sends crosses a network in the clear. No user name or password
/// in it (a sign-in is a secret kept apart), no fragment, and no control characters or spaces. It is checked when an integration is stored and
/// again when it is connected to.
/// </summary>
internal static class McpEndpointRules
{
    /// <summary>The longest address that is accepted, in characters.</summary>
    public const int MaxLength = 2048;

    /// <summary>What is wrong with <paramref name="endpoint"/> as an MCP server's address, in words with no value in them, or <see langword="null"/> when it is fine.</summary>
    public static string? Problem(string? endpoint) => Problem(endpoint, out _);

    /// <summary>As <see cref="Problem(string?)"/>, giving the address as a <see cref="Uri"/> when it is fine.</summary>
    public static string? Problem(string? endpoint, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "The address is missing.";
        }

        if (endpoint.Length > MaxLength || endpoint.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return "The address is too long or has spaces or control characters in it.";
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) || !parsed.IsAbsoluteUri)
        {
            return "The address is not a web address.";
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return "The address must be an https (or local http) address.";
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return "The address has no host.";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return "The address must not hold a user name or a password.";
        }

        if (!string.IsNullOrEmpty(parsed.Fragment))
        {
            return "The address must not have a fragment.";
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && !IsLoopback(parsed) && !McpNetwork.IsOwnNetwork(parsed))
        {
            return "An http address is allowed only on this PC or your own network; use https.";
        }

        uri = parsed;
        return null;
    }

    /// <summary>Whether <paramref name="uri"/> is on this PC: <c>localhost</c> (or a name under it), <c>127.0.0.1</c> or <c>[::1]</c>.</summary>
    public static bool IsLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
    }
}

/// <summary>
/// Which addresses are on the user's own network and not on the internet: a server in the home, such as Home Assistant at <c>http://homeassistant.local:8123</c>, which
/// has no certificate to be reached over <c>https</c> with. Such a server may be an <c>http</c> one; anything else that is not on this PC must be <c>https</c>.
/// </summary>
public static class McpNetwork
{
    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home", ".home.arpa", ".internal"];

    /// <summary>
    /// Whether <paramref name="uri"/> names a host of the user's own network: a private or link-local address (10.x, 172.16-31.x, 192.168.x, 169.254.x, fc00::/7, fe80::/10),
    /// a name with no dot in it (<c>homeassistant</c>), or a name under <c>.local</c>, <c>.lan</c>, <c>.home</c>, <c>.home.arpa</c> or <c>.internal</c>, none of which the
    /// internet resolves.
    /// </summary>
    public static bool IsOwnNetwork(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']');
        if (host.Length == 0)
        {
            return false;
        }

        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            var bytes = address.GetAddressBytes();
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254)
                : (bytes[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal;
        }

        return !host.Contains('.', StringComparison.Ordinal) || LocalSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether an MCP server may be at <paramref name="uri"/>: <c>https</c> anywhere, or <c>http</c> on this PC or the user's own network.</summary>
    public static bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && (McpEndpointRules.IsLoopback(uri) || IsOwnNetwork(uri))));
    }
}
