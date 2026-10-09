using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>Credential functions from advapi32.dll: Windows' credential store, which protects what it holds for the signed-in user.</summary>
internal static partial class Advapi32
{
    /// <summary>A credential that holds a name and a secret for an app's own use (<c>CRED_TYPE_GENERIC</c>).</summary>
    public const uint CredTypeGeneric = 1;

    /// <summary>Kept for this user on this PC, and not copied to other PCs with a roaming profile (<c>CRED_PERSIST_LOCAL_MACHINE</c>).</summary>
    public const uint CredPersistLocalMachine = 2;

    /// <summary>The most bytes a credential's secret may have (<c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c>).</summary>
    public const int MaxCredentialBlobSize = 2560;

    /// <summary><c>ERROR_NOT_FOUND</c>: there is no such credential.</summary>
    public const int ErrorNotFound = 1168;

    /// <summary>The native <c>CREDENTIALW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Credential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredWrite(in Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredRead(string targetName, uint type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredDelete(string targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    public static partial void CredFree(nint buffer);
}
