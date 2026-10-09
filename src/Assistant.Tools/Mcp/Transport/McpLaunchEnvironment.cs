namespace Assistant.Tools.Mcp;

/// <summary>
/// The environment a local MCP server's program is started with (PROJECT_SPEC §3.3, step 104): the few variables Windows programs need to find
/// themselves and their folders, taken from the Assistant's own, and what the integration was configured to add. Nothing else of the
/// Assistant's environment is passed on, so a program the user installed never sees a token, a key or a proxy login that happened to be set
/// for another reason.
/// </summary>
internal static class McpLaunchEnvironment
{
    // Variables a Windows program needs to run and to find the user's folders.
    private static readonly string[] Inherited =
    [
        "PATH", "PATHEXT", "SystemRoot", "windir", "SystemDrive", "ComSpec", "TEMP", "TMP", "USERPROFILE", "USERNAME", "USERDOMAIN",
        "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "PUBLIC", "ALLUSERSPROFILE", "OS", "PROCESSOR_ARCHITECTURE",
        "NUMBER_OF_PROCESSORS", "COMPUTERNAME",
    ];

    /// <summary>The environment to start a program with.</summary>
    /// <param name="read">Reads a variable of the Assistant's own environment, or gives <see langword="null"/>.</param>
    /// <param name="configured">What the integration adds (or sets in place of an inherited variable of the same name).</param>
    public static IReadOnlyDictionary<string, string> Build(Func<string, string?> read, IReadOnlyDictionary<string, string> configured)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(configured);

        // Windows names its variables without regard to case.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Inherited)
        {
            if (read(name) is { Length: > 0 } value)
            {
                environment[name] = value;
            }
        }

        foreach (var (name, value) in configured)
        {
            environment[name] = value;
        }

        return environment;
    }
}
