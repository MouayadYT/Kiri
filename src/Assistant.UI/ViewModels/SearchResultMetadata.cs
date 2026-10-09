using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;

namespace Assistant.UI.ViewModels;

/// <summary>
/// The note at the right of a message or contact result: the date it belongs to, written the short way the Messages
/// reference writes it (<c>4/25/26</c>), or a status word such as <c>Draft</c> or <c>Missed</c>, which stands in for the
/// date.
/// </summary>
public static partial class SearchResultMetadata
{
    /// <summary>
    /// Writes <paramref name="when"/> as a person would beside a message: the time of day today, <c>Yesterday</c>, the
    /// weekday for the rest of the past week, and otherwise the culture's short date with a two-digit year.
    /// </summary>
    /// <param name="when">When the message was sent or received.</param>
    /// <param name="now">The moment it is being read at; days are counted in its offset.</param>
    /// <param name="culture">The culture to write it in, or the current UI culture.</param>
    public static string FormatDate(DateTimeOffset when, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var local = when.ToOffset(now.Offset);
        var days = (now.Date - local.Date).Days;
        if (days == 0)
        {
            return local.ToString("t", culture);
        }

        if (days == 1)
        {
            return "Yesterday";
        }

        if (days is >= 2 and <= 6)
        {
            return local.ToString("dddd", culture);
        }

        var pattern = TwoDigitYear().Replace(culture.DateTimeFormat.ShortDatePattern, "yy");
        return local.ToString(pattern, culture);
    }

    [GeneratedRegex("y{3,4}")]
    private static partial Regex TwoDigitYear();
}

/// <summary>Builds the results of the messages and contacts kinds, so every one of them is drawn the same way.</summary>
public static class SearchResultFactory
{
    /// <summary>
    /// A conversation that matched: the people's avatar with the Messages mark on its corner, the conversation's name
    /// and, under it, the snippet that matched. At the right is the <paramref name="status"/> when there is one, and
    /// otherwise the date of the message.
    /// </summary>
    public static SearchResultViewModel Message(
        string displayName, string snippet, IEnumerable<AvatarParticipant> participants, ICommand command,
        DateTimeOffset when, DateTimeOffset now, string? status = null, CultureInfo? culture = null) =>
        new(SearchResultKind.Message, displayName, command, snippet,
            string.IsNullOrEmpty(status) ? SearchResultMetadata.FormatDate(when, now, culture) : status,
            SearchResultIcon.FromParticipants(participants), [SearchResultBadge.Messages]);

    /// <summary>
    /// A person: their avatar, their name and, under it, a line such as the number or address that matched. A status,
    /// such as <c>Missed</c>, is shown at the right.
    /// </summary>
    public static SearchResultViewModel Contact(
        string displayName, AvatarParticipant avatar, ICommand command, string? detailLine = null, string? status = null) =>
        new(SearchResultKind.Contact, displayName, command, detailLine, status, SearchResultIcon.FromParticipants(avatar));
}
