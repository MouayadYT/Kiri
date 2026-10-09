using Assistant.Core.QuickSearch;

namespace Assistant.Search.Applications;

/// <summary>
/// Reads the applications from several places as one list (PROJECT_SPEC §4.1): what Start lists, and what PowerToys Run would also find (the
/// Desktop's shortcuts, programs and game launchers' links). The first source is the list's backbone: when it
/// cannot be read, the reading fails and is tried again; another that cannot be read only adds nothing. An application a later source finds
/// that an earlier one has already (the same identity, the same program file, or the same name) is listed once, as the earlier one gave it,
/// so a shortcut on the Desktop to a program that is also in Start does not show twice. It is listed with the shortcut's icon, though
/// (<see cref="InstalledApplication.IconPath"/>): an icon the user gave the shortcut on their Desktop is the one they know the application by.
/// Names and identities are never logged.
/// </summary>
public sealed class CombinedApplicationSource : IApplicationSource
{
    private readonly IReadOnlyList<IApplicationSource> _sources;

    /// <summary>Creates the source over <paramref name="sources"/>, the most trusted first.</summary>
    public CombinedApplicationSource(params IApplicationSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Length == 0 || sources.Any(source => source is null))
        {
            throw new ArgumentException("At least one source, and none of them null.", nameof(sources));
        }

        _sources = [.. sources];
        foreach (var source in _sources)
        {
            source.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken)
    {
        var all = new List<InstalledApplication>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Which application is listed under a program file and under a name, by its identity, and the shortcut whose icon each takes.
        var programs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _sources.Count; index++)
        {
            IReadOnlyList<InstalledApplication> found;
            try
            {
                found = await _sources[index].GetApplicationsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (index > 0 && exception is not OperationCanceledException)
            {
                // What it said is not logged: it can hold a path.
                continue;
            }

            // Within one source two entries may share a name (Start lists them so); across sources the earlier one stands for both.
            var first = index == 0;
            var added = new List<InstalledApplication>();
            foreach (var application in found)
            {
                if (!ids.Add(application.Id))
                {
                    continue;
                }

                if (!first && (names.TryGetValue(application.DisplayName, out var listed)
                    || (application.ExecutablePath is { } program && programs.TryGetValue(program, out listed))))
                {
                    // Listed once, as the earlier source gave it, with the icon of the first shortcut that stands for it.
                    if (IsShortcut(application.Id))
                    {
                        icons.TryAdd(listed, application.Id);
                    }

                    continue;
                }

                added.Add(application);
            }

            foreach (var application in added)
            {
                names.TryAdd(application.DisplayName, application.Id);
                if (application.ExecutablePath is { } program)
                {
                    programs.TryAdd(program, application.Id);
                }
            }

            all.AddRange(added);
        }

        if (icons.Count > 0)
        {
            for (var index = 0; index < all.Count; index++)
            {
                if (all[index].IconPath is null && icons.TryGetValue(all[index].Id, out var shortcut))
                {
                    all[index] = all[index] with { IconPath = shortcut };
                }
            }
        }

        all.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return all;
    }

    // A shortcut file: what a person can give an icon of their own.
    private static bool IsShortcut(string id) =>
        Path.IsPathFullyQualified(id) && Path.GetExtension(id) is var extension
        && (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase));
}
