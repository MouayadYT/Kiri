namespace Assistant.Core.Settings;

public static class SpeechApiAddress
{
    public static bool IsValid(string? address) =>
        address is { Length: <= 2048 } && Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
}
