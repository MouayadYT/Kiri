using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Contracts;
using Assistant.Windows.Interop;

namespace Assistant.Windows.Credentials;

/// <summary>
/// Keeps the Assistant's secrets in Windows Credential Manager (PROJECT_SPEC §3.5, §5.10), which encrypts them for the
/// signed-in user. Each secret is a generic credential named <c>Assistant/&lt;name&gt;</c>, kept on this PC only, so no
/// other account, no copy of the app's folder and no roaming profile carries it. Nothing here logs, and no exception
/// message holds a secret or a name.
/// </summary>
public sealed class WindowsCredentialSecretStore : ISecretStore
{
    /// <summary>The start of every credential's target name.</summary>
    public const string DefaultTargetPrefix = "Assistant";

    private const string UserName = "Assistant";

    private readonly string _prefix;

    /// <summary>Creates the store with the default target prefix.</summary>
    public WindowsCredentialSecretStore()
        : this(DefaultTargetPrefix)
    {
    }

    /// <summary>Creates the store with its credentials named <c>{targetPrefix}/{name}</c>, so a test's are kept apart from the app's.</summary>
    public WindowsCredentialSecretStore(string targetPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPrefix);
        _prefix = targetPrefix;
    }

    /// <inheritdoc/>
    public Task SetAsync(string name, string secret, CancellationToken cancellationToken = default)
    {
        RequireName(name);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (secret.Length > SecretNames.MaxSecretLength)
        {
            throw new ArgumentException("The secret is too long.", nameof(secret));
        }

        return Task.Run(() => Write(Target(name), secret), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        RequireName(name);
        return Task.Run(() => Read(Target(name)), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        RequireName(name);
        return Task.Run(() => Delete(Target(name)), cancellationToken);
    }

    private static void RequireName(string name)
    {
        if (!SecretNames.IsValid(name))
        {
            throw new ArgumentException("The name is not a valid secret name.", nameof(name));
        }
    }

    private string Target(string name) => $"{_prefix}/{name}";

    private static void Write(string target, string secret)
    {
        // Windows shows a credential's secret as UTF-16 text, so it is stored as that.
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobMemory = Marshal.AllocHGlobal(blob.Length);
        var targetMemory = Marshal.StringToHGlobalUni(target);
        var userMemory = Marshal.StringToHGlobalUni(UserName);
        try
        {
            Marshal.Copy(blob, 0, blobMemory, blob.Length);
            var credential = new Advapi32.Credential
            {
                Type = Advapi32.CredTypeGeneric,
                TargetName = targetMemory,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobMemory,
                Persist = Advapi32.CredPersistLocalMachine,
                UserName = userMemory,
            };
            if (!Advapi32.CredWrite(in credential, 0))
            {
                throw new SecretStoreException("Windows could not store the secret.", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
            Marshal.Copy(new byte[blob.Length], 0, blobMemory, blob.Length);
            Marshal.FreeHGlobal(blobMemory);
            Marshal.FreeHGlobal(targetMemory);
            Marshal.FreeHGlobal(userMemory);
        }
    }

    private static string? Read(string target)
    {
        if (!Advapi32.CredRead(target, Advapi32.CredTypeGeneric, 0, out var memory))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == Advapi32.ErrorNotFound
                ? null
                : throw new SecretStoreException("Windows could not read the secret.", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Advapi32.Credential>(memory);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            try
            {
                return Encoding.Unicode.GetString(blob);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            Advapi32.CredFree(memory);
        }
    }

    private static bool Delete(string target)
    {
        if (Advapi32.CredDelete(target, Advapi32.CredTypeGeneric, 0))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        return error == Advapi32.ErrorNotFound
            ? false
            : throw new SecretStoreException("Windows could not delete the secret.", error);
    }
}
