using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Files;
using Assistant.Core.Tools;

namespace Assistant.Tools.Files;

/// <summary>What the tools that open or show a file or folder share: finding the file by its id, the kinds of file that are never opened, and the results.</summary>
internal static class FileOpening
{
    /// <summary>The longest an id or a name given for a file or folder may be.</summary>
    public const int MaxReferenceLength = 260;

    // Kinds of file that run code (or run what they point to) when they are opened: programs, scripts, shortcuts, installers, packages,
    // settings that change Windows. Windows itself blocks most of these as e-mail attachments. A model's call opens none of them, whatever
    // the user's program for the kind: the user opens such a file from its row in the bar or in File Explorer, as they always could.
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "com", "scr", "pif", "bat", "cmd", "msi", "msp", "mst", "msc", "msix", "msixbundle", "appx", "appxbundle", "appinstaller",
        "ps1", "ps1xml", "ps2", "psc1", "psc2", "psd1", "psm1", "vbs", "vbe", "vb", "js", "jse", "mjs", "cjs", "wsf", "wsh", "ws", "wsc", "hta", "htc",
        "py", "pyw", "pyz", "pyzw", "pl", "rb", "sh", "bash", "zsh", "jar", "jnlp", "class",
        "lnk", "url", "website", "appref-ms", "application", "gadget", "xbap", "xll", "vsto", "vsix",
        "reg", "inf", "cpl", "scf", "sct", "theme", "themepack", "settingcontent-ms", "library-ms", "search-ms", "diagcab", "cab", "chm", "hlp",
        "dll", "ocx", "sys", "drv", "vhd", "vhdx", "iso", "img", "dmg",
        "ade", "adp", "mde", "mdb", "accde", "docm", "xlsm", "pptm", "dotm", "xltm", "potm", "ppam", "xlam", "sldm",
    };

    /// <summary>Whether opening the file at <paramref name="path"/> may run code: it is of a kind that does, or has no kind that says it does not.</summary>
    public static bool RunsCode(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        return extension.Length == 0 || CodeExtensions.Contains(extension);
    }

    /// <summary>The conversation's known file or folder that the call's argument names, or <see langword="null"/>.</summary>
    public static KnownFile? Resolve(IConversationFiles known, ToolContext context, JsonElement arguments, string argument = "file")
    {
        var reference = arguments.GetProperty(argument).GetString() ?? string.Empty;
        return known.Find(context.ConversationId, reference);
    }

    /// <summary>
    /// The question the user is asked before a file or a folder is opened or shown (step 115): which one exactly (its name and the folder it is in, as the PC has it),
    /// and what happens to it. The id the model gave is not shown: it means nothing to the user.
    /// </summary>
    public static ToolConfirmation Ask(
        string title, string approveLabel, ConfirmationKind kind, string noun, string name, string path, string? opensWith = null, bool wholePath = false)
    {
        // A folder of the user's own is shown by where it is, whole; anything else by its name and the folder it is in.
        var details = wholePath
            ? new List<ConfirmationDetail> { new(noun, path) }
            : new List<ConfirmationDetail>
            {
                new(noun, name),
                new("Location", Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : path),
            };
        if (opensWith is not null)
        {
            details.Add(new ConfirmationDetail("Opens with", opensWith));
        }

        return new ToolConfirmation(kind, title, details, approveLabel);
    }

    /// <summary>What a file is opened with, in words: the program the user chose for its kind.</summary>
    public static string OpensWith(string path) =>
        Path.GetExtension(path) is { Length: > 1 } extension
            ? $"The program set for {extension.ToLowerInvariant()} files"
            : "The program set for this kind of file";

    public static ToolResult NotKnown(ToolCall call) =>
        Failed(call, "No file with that id or name is known in this conversation. Use search_files to find it first.");

    public static ToolResult Done(ToolCall call, string message) =>
        new(call.Id, call.ToolName, ToolResultStatus.Succeeded, SystemToolResults.Done(message));

    public static ToolResult Failed(ToolCall call, string message) =>
        ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, message);
}
