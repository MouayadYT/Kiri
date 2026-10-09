namespace Assistant.ExplorerExtension;

/// <summary>What the entry point was started to do.</summary>
internal enum ExtensionCommandKind
{
    /// <summary>The arguments were not a command it knows.</summary>
    Unknown = 0,

    /// <summary>File Explorer's Ask Assistant: forward the files to the app (<c>ask "C:\…\file.pdf"</c>).</summary>
    Ask = 1,

    /// <summary>Add the menu entry for the current user (<c>register</c>).</summary>
    Register = 2,

    /// <summary>Remove the menu entry for the current user (<c>unregister</c>).</summary>
    Unregister = 3,
}

/// <summary>The command line, read: a command and, for <see cref="ExtensionCommandKind.Ask"/>, the files.</summary>
internal sealed record ExtensionCommand(ExtensionCommandKind Kind, IReadOnlyList<string> Paths)
{
    /// <summary>The command name File Explorer's menu entry runs.</summary>
    public const string AskName = "ask";

    /// <summary>The command name that adds the menu entry.</summary>
    public const string RegisterName = "register";

    /// <summary>The command name that removes the menu entry.</summary>
    public const string UnregisterName = "unregister";

    /// <summary>Reads <paramref name="args"/>. Command names are matched in any case; a path is kept as it is.</summary>
    public static ExtensionCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return new(ExtensionCommandKind.Unknown, []);
        }

        var name = args[0];
        var rest = args.Skip(1).ToArray();
        if (name.Equals(AskName, StringComparison.OrdinalIgnoreCase))
        {
            var paths = rest.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            return new(paths.Length > 0 ? ExtensionCommandKind.Ask : ExtensionCommandKind.Unknown, paths);
        }

        var kind = name.Equals(RegisterName, StringComparison.OrdinalIgnoreCase) ? ExtensionCommandKind.Register
            : name.Equals(UnregisterName, StringComparison.OrdinalIgnoreCase) ? ExtensionCommandKind.Unregister
            : ExtensionCommandKind.Unknown;
        return new(rest.Length == 0 ? kind : ExtensionCommandKind.Unknown, []);
    }
}
