using Microsoft.Win32;

namespace Assistant.ExplorerExtension.Registration;

/// <summary>
/// Adds and removes File Explorer's Ask Assistant entry for the current user (PROJECT_SPEC §4.4, D3). It is a static verb under
/// <c>Software\Classes\SystemFileAssociations\.ext\shell</c> in the user's own registry, for each supported extension: File
/// Explorer reads it from there whichever app opens the type, no administrator is needed, and nothing is loaded into
/// <c>explorer.exe</c>. Choosing it starts this entry point with <c>ask "&lt;path&gt;"</c>, once for each selected file. The entry
/// is at the top of the menu, with the Assistant's mark in the menu's colour, and has the <c>Player</c> selection model: with the default one File
/// Explorer shows a verb only for 15 files or fewer, and a selection of 20 pictures would have no Ask Assistant. Windows 11 lists
/// such an entry in the classic menu, under "Show more options"; its first menu would need package identity.
/// </summary>
internal sealed class ExplorerMenuRegistration
{
    /// <summary>The verb's key name, the same for every extension, so the entry can be found and removed again.</summary>
    public const string VerbName = "Assistant.AskAssistant";

    /// <summary>What the menu shows.</summary>
    public const string Title = "Ask Assistant";

    /// <summary>The selection model that keeps the verb for any number of files (File Explorer starts the command once for each).</summary>
    public const string SelectionModel = "Player";

    /// <summary>Where the verb goes in the menu.</summary>
    public const string MenuPosition = "Top";

    /// <summary>The Assistant's mark in white, for menus drawn dark, beside the entry point.</summary>
    public const string DarkMenuIconName = "AskAssistant.dark.ico";

    /// <summary>The Assistant's mark in black, for menus drawn light, beside the entry point.</summary>
    public const string LightMenuIconName = "AskAssistant.light.ico";

    /// <summary>
    /// The <c>Icon</c> value: the mark in white or black to match the menu's theme, from the file beside the entry point, or the
    /// entry point's own icon when that file is missing. <paramref name="lightTheme"/> is <see langword="null"/> to follow Windows.
    /// </summary>
    public static string IconFor(string executable, bool? lightTheme = null)
    {
        var name = lightTheme ?? SystemUsesLightTheme() ? LightMenuIconName : DarkMenuIconName;
        var path = Path.Combine(Path.GetDirectoryName(executable) ?? "", name);
        return File.Exists(path) ? $"\"{path}\"" : $"\"{executable}\",0";
    }

    /// <summary>Whether Windows draws its menus light. A missing setting means light, as Windows does.</summary>
    public static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is not int value || value != 0;
    }

    /// <summary>Where the verbs live, under <c>Software\Classes</c>.</summary>
    public const string AssociationsKey = "SystemFileAssociations";

    private readonly RegistryKey _classes;

    /// <summary>Registers under <paramref name="classes"/>: the user's <c>Software\Classes</c>, or a test's own key.</summary>
    public ExplorerMenuRegistration(RegistryKey classes) => _classes = classes ?? throw new ArgumentNullException(nameof(classes));

    /// <summary>The command a verb runs: <paramref name="executable"/> with the <c>ask</c> command and the file, quoted.</summary>
    public static string CommandLine(string executable) => $"\"{executable}\" {ExtensionCommand.AskName} \"%1\"";

    /// <summary>
    /// Adds the entry for each of <paramref name="extensions"/>, running <paramref name="executable"/>, and removes it from any
    /// other extension it was added to before. Running it again with the same arguments changes nothing.
    /// </summary>
    /// <param name="executable">The full path of this entry point.</param>
    /// <param name="extensions">The extensions, with their dot.</param>
    /// <param name="lightTheme">Whether the menu is light; <see langword="null"/> asks Windows.</param>
    public void Register(string executable, IEnumerable<string> extensions, bool? lightTheme = null)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || executable.Contains('"'))
        {
            throw new ArgumentException("The entry point must be a full path.", nameof(executable));
        }

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in extensions)
        {
            if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.' || extension.IndexOfAny(['\\', '/', '"']) >= 0)
            {
                throw new ArgumentException("Each extension must start with a dot and name no path.", nameof(extensions));
            }

            wanted.Add(extension.ToLowerInvariant());
        }

        var command = CommandLine(executable);
        var icon = IconFor(executable, lightTheme);
        foreach (var extension in wanted)
        {
            using var verb = _classes.CreateSubKey($@"{AssociationsKey}\{extension}\shell\{VerbName}", writable: true);
            verb.SetValue("", Title);
            verb.SetValue("MultiSelectModel", SelectionModel);
            verb.SetValue("Position", MenuPosition);
            verb.SetValue("Icon", icon);
            using var commandKey = verb.CreateSubKey("command", writable: true);
            commandKey.SetValue("", command);
        }

        foreach (var stale in RegisteredExtensions().Where(extension => !wanted.Contains(extension)))
        {
            Remove(stale);
        }
    }

    /// <summary>
    /// Removes the entry from every extension it is on, and the keys that held only it. Other apps' verbs are left alone.
    /// </summary>
    public void Unregister()
    {
        foreach (var extension in RegisteredExtensions())
        {
            Remove(extension);
        }
    }

    /// <summary>The extensions the entry is on now, in the case the registry holds them.</summary>
    public IReadOnlyList<string> RegisteredExtensions()
    {
        using var associations = _classes.OpenSubKey(AssociationsKey);
        if (associations is null)
        {
            return [];
        }

        var registered = new List<string>();
        foreach (var name in associations.GetSubKeyNames())
        {
            using var verb = associations.OpenSubKey($@"{name}\shell\{VerbName}");
            if (verb is not null)
            {
                registered.Add(name);
            }
        }

        return registered;
    }

    /// <summary>The command the entry runs for <paramref name="extension"/>, or <see langword="null"/> when it is not there.</summary>
    public string? CommandFor(string extension)
    {
        using var command = _classes.OpenSubKey($@"{AssociationsKey}\{extension}\shell\{VerbName}\command");
        return command?.GetValue("") as string;
    }

    private void Remove(string extension)
    {
        using var associations = _classes.OpenSubKey(AssociationsKey, writable: true);
        if (associations is null)
        {
            return;
        }

        var shellPath = $@"{extension}\shell";
        using (var shell = associations.OpenSubKey(shellPath, writable: true))
        {
            shell?.DeleteSubKeyTree(VerbName, throwOnMissingSubKey: false);
        }

        // The keys this entry needed are removed with it once nothing else is in them.
        DeleteIfEmpty(associations, shellPath);
        DeleteIfEmpty(associations, extension);
    }

    private static void DeleteIfEmpty(RegistryKey parent, string path)
    {
        bool empty;
        using (var key = parent.OpenSubKey(path))
        {
            empty = key is { SubKeyCount: 0, ValueCount: 0 };
        }

        if (empty)
        {
            parent.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
    }
}
