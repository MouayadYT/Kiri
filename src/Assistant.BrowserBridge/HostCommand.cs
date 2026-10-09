using Assistant.BrowserBridge.Registration;
using Assistant.Core.Ipc;

namespace Assistant.BrowserBridge;

/// <summary>What the executable was started to do.</summary>
internal enum HostCommandKind
{
    /// <summary>The arguments were not a command it knows.</summary>
    Unknown = 0,

    /// <summary>Serve the browser: read the extension's messages on the standard input (a browser starts it with the extension's origin and nothing it knows).</summary>
    Serve = 1,

    /// <summary>Register the host for the current user (<c>register</c>).</summary>
    Register = 2,

    /// <summary>Remove the host's registration for the current user (<c>unregister</c>).</summary>
    Unregister = 3,
}

/// <summary>The command line, read.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="ExtensionIds">For <see cref="HostCommandKind.Register"/>, the extra extension ids to allow, besides the Assistant's own.</param>
/// <param name="PipeName">For <see cref="HostCommandKind.Serve"/>, the app pipe to use instead of the current user's (<c>--pipe</c>), or <see langword="null"/>.</param>
internal sealed record HostCommand(HostCommandKind Kind, IReadOnlyList<string> ExtensionIds, string? PipeName)
{
    /// <summary>The command name that registers the host.</summary>
    public const string RegisterName = "register";

    /// <summary>The command name that removes the registration.</summary>
    public const string UnregisterName = "unregister";

    /// <summary>The option that names another extension that may start the host.</summary>
    public const string ExtensionIdOption = "--extension-id";

    /// <summary>The option that names the app pipe to forward to, for trying the host without the app (it is never given by a browser).</summary>
    public const string PipeOption = "--pipe";

    /// <summary>
    /// Reads <paramref name="args"/>. <c>register</c> and <c>unregister</c> are matched in any case; anything else is a browser starting the
    /// host, which passes the extension's origin and, on Windows, <c>--parent-window=&lt;handle&gt;</c>, and is served.
    /// </summary>
    public static HostCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var unknown = new HostCommand(HostCommandKind.Unknown, [], null);
        if (args.Count > 0 && args[0].Equals(RegisterName, StringComparison.OrdinalIgnoreCase))
        {
            var ids = new List<string>();
            for (var i = 1; i < args.Count; i += 2)
            {
                if (!args[i].Equals(ExtensionIdOption, StringComparison.OrdinalIgnoreCase)
                    || i + 1 >= args.Count || !NativeHostRegistration.IsExtensionId(args[i + 1]))
                {
                    return unknown;
                }

                ids.Add(args[i + 1]);
            }

            return new HostCommand(HostCommandKind.Register, ids, null);
        }

        if (args.Count > 0 && args[0].Equals(UnregisterName, StringComparison.OrdinalIgnoreCase))
        {
            return args.Count == 1 ? new HostCommand(HostCommandKind.Unregister, [], null) : unknown;
        }

        string? pipe = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals(PipeOption, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || !LocalPipe.IsValidName(args[i + 1]))
                {
                    return unknown;
                }

                pipe = args[++i];
            }
        }

        return new HostCommand(HostCommandKind.Serve, [], pipe);
    }
}
