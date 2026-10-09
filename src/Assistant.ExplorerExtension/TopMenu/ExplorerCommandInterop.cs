using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Assistant.ExplorerExtension.TopMenu;

/// <summary><c>IShellItem</c>, as far as the command reads it.</summary>
[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);

    [PreserveSig] int GetParent(out nint parent);

    [PreserveSig] int GetDisplayName(uint sigdn, out nint name);

    [PreserveSig] int GetAttributes(uint mask, out uint attributes);

    [PreserveSig] int Compare(nint other, uint hint, out int order);
}

/// <summary><c>IShellItemArray</c>, as far as the command reads it.</summary>
[GeneratedComInterface]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
internal partial interface IShellItemArray
{
    [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);

    [PreserveSig] int GetPropertyStore(int flags, in Guid riid, out nint result);

    [PreserveSig] int GetPropertyDescriptionList(nint key, in Guid riid, out nint result);

    [PreserveSig] int GetAttributes(int flags, uint mask, out uint attributes);

    [PreserveSig] int GetCount(out uint count);

    [PreserveSig] int GetItemAt(uint index, out IShellItem item);
}

/// <summary><c>IExplorerCommand</c>: what File Explorer's Windows 11 first menu asks of a command.</summary>
[GeneratedComInterface]
[Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
internal partial interface IExplorerCommand
{
    [PreserveSig] int GetTitle(nint items, out nint name);

    [PreserveSig] int GetIcon(nint items, out nint icon);

    [PreserveSig] int GetToolTip(nint items, out nint tip);

    [PreserveSig] int GetCanonicalName(out Guid name);

    [PreserveSig] int GetState(IShellItemArray? items, int okToBeSlow, out int state);

    [PreserveSig] int Invoke(IShellItemArray? items, nint bindContext);

    [PreserveSig] int GetFlags(out int flags);

    [PreserveSig] int EnumSubCommands(out nint commands);
}

/// <summary><c>IClassFactory</c>, which File Explorer asks for the command.</summary>
[GeneratedComInterface]
[Guid("00000001-0000-0000-c000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, in Guid riid, out nint instance);

    [PreserveSig] int LockServer(int lockServer);
}

internal static partial class ComNative
{
    public const int SOk = 0;
    public const int EFail = unchecked((int)0x80004005);
    public const int ENoInterface = unchecked((int)0x80004002);
    public const int ENotImpl = unchecked((int)0x80004001);
    public const int EClassNotAvailable = unchecked((int)0x80040111);
    public const int ClassNoAggregation = unchecked((int)0x80040110);

    /// <summary>The ID of <c>SIGDN_FILESYSPATH</c>.</summary>
    public const uint FileSystemPath = 0x80058000;

    [LibraryImport("ole32.dll")]
    public static partial int CoRegisterClassObject(in Guid clsid, nint unknown, uint context, uint flags, out uint cookie);

    [LibraryImport("ole32.dll")]
    public static partial int CoRevokeClassObject(uint cookie);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint memory);

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CoInitializeEx(nint reserved, uint options);
}
