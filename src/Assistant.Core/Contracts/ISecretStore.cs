using System.Text.RegularExpressions;

namespace Assistant.Core.Contracts;

/// <summary>
/// Keeps the Assistant's secrets: access tokens, passwords, keys. This is the only place they may be kept (PROJECT_SPEC
/// §3.5, §5.10): never in <c>AppSettings</c>, a settings file, the database, a log or an exception message. The
/// implementation hands them to the protection Windows gives the signed-in user, so no other account and no copy of the
/// app's folder can read them.
/// </summary>
public interface ISecretStore
{
    /// <summary>Stores <paramref name="secret"/> under <paramref name="name"/>, replacing what was stored under it.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not a valid secret name (<see cref="SecretNames.IsValid"/>), or <paramref name="secret"/>
    /// is empty or longer than <see cref="SecretNames.MaxSecretLength"/>.
    /// </exception>
    /// <exception cref="SecretStoreException">Windows could not store it.</exception>
    Task SetAsync(string name, string secret, CancellationToken cancellationToken = default);

    /// <summary>Returns the secret stored under <paramref name="name"/>, or <see langword="null"/> when there is none.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid secret name.</exception>
    /// <exception cref="SecretStoreException">Windows could not read it.</exception>
    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes the secret stored under <paramref name="name"/>; returns whether there was one.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid secret name.</exception>
    /// <exception cref="SecretStoreException">Windows could not delete it.</exception>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>What a secret's name may be, and how long a secret may be.</summary>
public static partial class SecretNames
{
    /// <summary>The most characters a secret may have (Windows' credential store holds 2,560 bytes).</summary>
    public const int MaxSecretLength = 1024;

    /// <summary>Whether <paramref name="name"/> can name a secret: 1 to 64 lower case letters, digits, dots, hyphens and underscores.</summary>
    public static bool IsValid(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex("^[a-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>The secret store could not do what it was asked. The message never holds a secret or its name.</summary>
public sealed class SecretStoreException : Exception
{
    /// <summary>Creates the exception, with the Windows error code when there is one.</summary>
    public SecretStoreException(string message, int errorCode = 0)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The Windows error code, or 0.</summary>
    public int ErrorCode { get; }
}
