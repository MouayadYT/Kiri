namespace Assistant.Windows.Shell;

/// <summary>
/// Reads what an application's identity in the shell's apps folder says about it. Start gives a desktop program an identity that is
/// its file's path, often with the folder it is under written as a known-folder id (<c>{6D809377-...}\7-Zip\7zFM.exe</c> for
/// <c>C:\Program Files\7-Zip\7zFM.exe</c>), and a packaged app an application user model id (<c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c>),
/// which has no file of its own. Nothing here is logged: an identity can hold a path.
/// </summary>
internal static class ApplicationLaunchId
{
    // The known folders that Start writes in front of a path, by their ids.
    private static readonly Dictionary<Guid, Func<string>> KnownFolders = new()
    {
        [new Guid("6D809377-6AF0-444B-8957-A3773F02200E")] = () => Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        [new Guid("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E")] = () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        [new Guid("905E63B6-C1BF-494E-B29C-65B732D3D21A")] = () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        [new Guid("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7")] = () => Environment.GetFolderPath(Environment.SpecialFolder.System),
        [new Guid("D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27")] = () => Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        [new Guid("F38BF404-1D43-42F2-9305-67DE0B28FC23")] = () => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        [new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091")] = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        [new Guid("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D")] = () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        [new Guid("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97")] = () => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    };

    // What a Start entry may point to that is not an application: help files, documents and pages. Start lists them beside the programs.
    private static readonly HashSet<string> NotApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        ".chm", ".hlp", ".txt", ".pdf", ".htm", ".html", ".url", ".rtf", ".doc", ".docx", ".md", ".xml", ".ini", ".log", ".xps", ".mht", ".mhtml",
    };

    // The files that start an application when they are opened, as PowerToys Run lists them: shortcuts, internet shortcuts that start an
    // application (a game launcher's), ClickOnce applications and programs.
    private static readonly HashSet<string> StartableFiles = new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".appref-ms", ".exe" };

    /// <summary>
    /// Whether the entry with identity <paramref name="id"/> is a document or a page rather than an application. An internet shortcut is a page,
    /// unless it starts an application, as a game launcher's does (<c>com.epicgames.launcher://</c>, <c>steam://</c>).
    /// </summary>
    public static bool IsDocument(string id)
    {
        if (!LooksLikePath(id))
        {
            return false;
        }

        var extension = Path.GetExtension(id);
        if (string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(id) && InternetShortcut.StartsAnApplication(id))
        {
            return false;
        }

        return extension.Length > 0 && NotApplications.Contains(extension);
    }

    /// <summary>
    /// Whether <paramref name="id"/> is a file that starts an application when it is opened (a shortcut, an internet shortcut, a ClickOnce
    /// application or a program) and exists, so that it is opened itself rather than through the shell's apps folder.
    /// </summary>
    public static bool IsStartableFile(string? id) =>
        !string.IsNullOrWhiteSpace(id) && Path.IsPathFullyQualified(id) && StartableFiles.Contains(Path.GetExtension(id)) && File.Exists(id);

    /// <summary>
    /// The program file an identity names, when it names one that exists: <c>C:\...\app.exe</c> as it is, or a known-folder id in front
    /// of a path made into the folder's own. <see langword="null"/> for a packaged app, a URL and anything else.
    /// </summary>
    public static string? ExecutablePath(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        string? candidate = null;
        if (id.Length > 38 && id[0] == '{' && id[37] == '}' && id[38] == '\\' && Guid.TryParse(id.AsSpan(0, 38), out var folderId)
            && KnownFolders.TryGetValue(folderId, out var folder))
        {
            var root = folder();
            if (!string.IsNullOrEmpty(root))
            {
                candidate = Path.Combine(root, id[39..]);
            }
        }
        else if (Path.IsPathFullyQualified(id))
        {
            candidate = id;
        }

        return candidate is not null && IsProgram(candidate) ? Path.GetFullPath(candidate) : null;
    }

    /// <summary>Whether <paramref name="path"/> is the file of a program that exists.</summary>
    public static bool IsProgram(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    private static bool LooksLikePath(string id) =>
        id.Contains('\\', StringComparison.Ordinal) || (id.Length > 2 && id[1] == ':' && char.IsLetter(id[0]));
}
