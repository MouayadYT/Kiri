using System.Runtime.InteropServices;

namespace Assistant.Windows.Interop;

/// <summary>How a shell item's name is asked for (SIGDN).</summary>
internal enum ShellItemNameKind : uint
{
    /// <summary>The name the shell shows.</summary>
    NormalDisplay = 0,

    /// <summary>The item's own parsing name inside its folder: for the apps folder, the application's identity.</summary>
    ParentRelativeParsing = 0x80018001,
}

/// <summary>What <see cref="IShellItemImageFactory.GetImage"/> is asked for (SIIGBF).</summary>
[Flags]
internal enum ShellImageFlags : uint
{
    /// <summary>The picture may be larger than the size asked for.</summary>
    BiggerSizeOk = 0x1,

    /// <summary>The item's icon, never a thumbnail of its contents.</summary>
    IconOnly = 0x4,
}

/// <summary>A width and a height, in pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize
{
    public int Width;
    public int Height;
}

/// <summary>A property of a shell item: the group it belongs to and its number in it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShellPropertyKey(Guid format, uint id)
{
    public Guid Format = format;
    public uint Id = id;
}

/// <summary>An item of the shell: a file, a folder, an application in the apps folder.</summary>
[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig]
    int BindToHandler(nint bindContext, in Guid handler, in Guid interfaceId, out nint result);

    [PreserveSig]
    int GetParent(out IShellItem parent);

    [PreserveSig]
    int GetDisplayName(ShellItemNameKind kind, out nint name);

    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);

    [PreserveSig]
    int Compare(IShellItem other, uint hint, out int order);
}

/// <summary>
/// A shell item that can also say what its properties are. It repeats the methods of <see cref="IShellItem"/> instead of
/// inheriting them: the runtime does not put an inherited COM interface's methods first in the table, so inheriting them would call
/// the wrong method (an access violation).
/// </summary>
[ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem2
{
    [PreserveSig]
    int BindToHandler(nint bindContext, in Guid handler, in Guid interfaceId, out nint result);

    [PreserveSig]
    int GetParent(out IShellItem parent);

    [PreserveSig]
    int GetDisplayName(ShellItemNameKind kind, out nint name);

    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);

    [PreserveSig]
    int Compare(IShellItem other, uint hint, out int order);

    // The methods between here and GetString are never called here; their places in the interface are what matters.
    [PreserveSig]
    int GetPropertyStore(uint flags, in Guid interfaceId, out nint store);

    [PreserveSig]
    int GetPropertyStoreWithCreateObject(uint flags, nint createObject, in Guid interfaceId, out nint store);

    [PreserveSig]
    int GetPropertyStoreForKeys(nint keys, uint count, uint flags, in Guid interfaceId, out nint store);

    [PreserveSig]
    int GetPropertyDescriptionList(in ShellPropertyKey key, in Guid interfaceId, out nint list);

    [PreserveSig]
    int Update(nint bindContext);

    [PreserveSig]
    int GetProperty(in ShellPropertyKey key, nint value);

    [PreserveSig]
    int GetClassId(in ShellPropertyKey key, out Guid classId);

    [PreserveSig]
    int GetFileTime(in ShellPropertyKey key, nint time);

    [PreserveSig]
    int GetInt32(in ShellPropertyKey key, out int value);

    [PreserveSig]
    int GetString(in ShellPropertyKey key, out nint value);
}

/// <summary>Walks the items of a shell folder.</summary>
[ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumShellItems
{
    [PreserveSig]
    int Next(uint count, out IShellItem item, out uint fetched);

    [PreserveSig]
    int Skip(uint count);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out IEnumShellItems copy);
}

/// <summary>Draws a shell item's icon or thumbnail.</summary>
[ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(NativeSize size, ShellImageFlags flags, out nint bitmap);
}

/// <summary>The functions that find shell items by name.</summary>
internal static partial class Shell32
{
    /// <summary>The handler that lists the items inside a folder item (BHID_EnumItems).</summary>
    public static readonly Guid EnumItemsHandler = new("94F60519-2850-4924-AA5A-D15E84868039");

    /// <summary>The identity of <see cref="IShellItem"/>.</summary>
    public static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>The identity of <see cref="IEnumShellItems"/>.</summary>
    public static readonly Guid EnumShellItemsId = new("70629033-E363-4A28-A567-0DB78006E6D7");

    /// <summary>System.Link.TargetParsingPath: the file a shortcut points to.</summary>
    public static readonly ShellPropertyKey LinkTargetParsingPath = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceId, out nint item);

    /// <summary>The shell item named by <paramref name="parsingName"/> (<c>shell:AppsFolder</c>), or <see langword="null"/>.</summary>
    public static IShellItem? CreateItem(string parsingName)
    {
        var result = SHCreateItemFromParsingName(parsingName, 0, ShellItemId, out var pointer);
        if (result < 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return (IShellItem)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>The name of <paramref name="item"/> in the form asked for, or <see langword="null"/>.</summary>
    public static string? NameOf(IShellItem item, ShellItemNameKind kind)
    {
        if (item.GetDisplayName(kind, out var pointer) < 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    /// <summary>A string property of <paramref name="item"/>, or <see langword="null"/> when it has none.</summary>
    public static string? StringProperty(IShellItem item, ShellPropertyKey key)
    {
        if (item is not IShellItem2 detailed || detailed.GetString(key, out var pointer) < 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }
}
