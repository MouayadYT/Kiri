using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Assistant.Core.Ipc;

/// <summary>
/// The name of the running app's own pipe (PROJECT_SPEC §5.7), where the File Explorer entry point and, later, the other
/// entry points hand it invocation requests. There is one for each user and Windows session, so two people signed in to one
/// PC, or one person in two sessions, each reach their own Assistant.
/// </summary>
public static class AppPipe
{
    /// <summary>What every app pipe's name starts with.</summary>
    public const string Prefix = "Assistant.App";

    /// <summary>
    /// The pipe name for <paramref name="user"/> (a <c>DOMAIN\name</c> account name, in any case) in Windows session
    /// <paramref name="sessionId"/>. The account name is hashed, so the name does not show it.
    /// </summary>
    public static string NameFor(string user, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        var key = Encoding.UTF8.GetBytes($"{user.ToUpperInvariant()}|{sessionId}");
        var hash = Convert.ToHexString(SHA256.HashData(key), 0, 8).ToLowerInvariant();
        return $"{Prefix}.{hash}";
    }

    /// <summary>The pipe name for the user and session this process runs in.</summary>
    public static string ForCurrentUser()
    {
        using var process = Process.GetCurrentProcess();
        return NameFor($"{Environment.UserDomainName}\\{Environment.UserName}", process.SessionId);
    }
}
