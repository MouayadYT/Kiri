using System.Globalization;
using Assistant.Core.Domain;

namespace Assistant.UI.Messages;

/// <summary>One row of a <see cref="FileCollection"/>: a file, folder or app, described as the row shows it.</summary>
public sealed class FileItem
{
    // A type label longer than this would not fit its icon, so such files get a plain document icon instead.
    private const int MaxTypeLength = 4;

    /// <summary>Creates a row for the item at <paramref name="path"/>.</summary>
    /// <param name="kind">Whether it is a file, folder or app.</param>
    /// <param name="name">Its name as shown.</param>
    /// <param name="path">Its full path; an app's shell parsing name.</param>
    /// <param name="modifiedAt">When it last changed, if known.</param>
    /// <param name="snippet">Text in it that matched the request, if any.</param>
    /// <param name="clock">What "today" is when describing <paramref name="modifiedAt"/>; the system clock by default.</param>
    public FileItem(
        SearchResultItemType kind, string name, string path, DateTimeOffset? modifiedAt = null, string? snippet = null,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(path);
        Kind = kind;
        Name = name;
        Path = path;
        ModifiedAt = modifiedAt;
        Snippet = string.IsNullOrWhiteSpace(snippet) ? null : snippet.Trim();
        Location = kind == SearchResultItemType.App ? null : FolderOf(path);
        TypeLabel = kind == SearchResultItemType.File ? TypeOf(name) : null;
        Details = Describe(Location, modifiedAt, clock ?? TimeProvider.System);
    }

    /// <summary>Whether it is a file, folder or app, which decides its icon.</summary>
    public SearchResultItemType Kind { get; }

    /// <summary>Its name, the row's first line.</summary>
    public string Name { get; }

    /// <summary>Its full path; an app's shell parsing name.</summary>
    public string Path { get; }

    /// <summary>When it last changed, if known.</summary>
    public DateTimeOffset? ModifiedAt { get; }

    /// <summary>Text in it that matched the request, shown under its details, or <see langword="null"/>.</summary>
    public string? Snippet { get; }

    /// <summary>The name of the folder it is in, or <see langword="null"/> for an app or a drive.</summary>
    public string? Location { get; }

    /// <summary>
    /// A file's type as its icon shows it, such as "PDF", or <see langword="null"/> when it has none short enough, and
    /// for folders and apps.
    /// </summary>
    public string? TypeLabel { get; }

    /// <summary>The row's second line: where it is and when it changed, such as "Documents · Yesterday, 4:05 PM".</summary>
    public string Details { get; }

    /// <summary>Creates a row for a search result.</summary>
    public static FileItem From(SearchResultItem result, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new FileItem(result.Type, result.DisplayName, result.Path, result.ModifiedAt, result.Snippet, clock);
    }

    private static string? FolderOf(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        var name = System.IO.Path.GetFileName(folder);
        return name.Length > 0 ? name : folder;
    }

    private static string? TypeOf(string name)
    {
        var extension = System.IO.Path.GetExtension(name).TrimStart('.');
        return extension.Length is > 0 and <= MaxTypeLength ? extension.ToUpperInvariant() : null;
    }

    private static string Describe(string? location, DateTimeOffset? modifiedAt, TimeProvider clock)
    {
        var parts = new List<string>(2);
        if (location is not null)
        {
            parts.Add(location);
        }

        if (modifiedAt is { } modified)
        {
            parts.Add(DescribeDate(modified, clock));
        }

        return string.Join(" · ", parts);
    }

    // Recent changes are told by day and time, older ones by date, as a file list in Windows does.
    private static string DescribeDate(DateTimeOffset modified, TimeProvider clock)
    {
        var culture = CultureInfo.CurrentCulture;
        var zone = clock.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(modified, zone);
        var today = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).Date;
        var time = local.ToString("t", culture);
        if (local.Date == today) return $"Today, {time}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday, {time}";

        // The culture's month and day, with the month abbreviated, such as "Sep 28".
        var monthDay = culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal);
        return local.Year == today.Year ? local.ToString(monthDay, culture) : local.ToString("d", culture);
    }
}
