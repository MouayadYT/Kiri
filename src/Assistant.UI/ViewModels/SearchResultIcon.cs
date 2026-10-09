using System.Windows.Media;

namespace Assistant.UI.ViewModels;

/// <summary>What a search result is, which decides its default icon and how a row of it is laid out.</summary>
public enum SearchResultKind
{
    /// <summary>An installed application.</summary>
    App,

    /// <summary>A file or a folder.</summary>
    File,

    /// <summary>Something the Assistant can do, such as a command or a setting.</summary>
    Action,

    /// <summary>A person.</summary>
    Contact,

    /// <summary>A message or a conversation.</summary>
    Message,

    /// <summary>A short definition of something, drawn with a larger icon.</summary>
    Knowledge,

    /// <summary>Text the user copied, from the clipboard history.</summary>
    Clipboard,
}

/// <summary>How much room a result's row gives its icon.</summary>
public enum SearchResultRowSize
{
    /// <summary>A 36-DIP icon in a 62-DIP row.</summary>
    Standard,

    /// <summary>A 50-DIP icon in an 82-DIP row.</summary>
    Large,
}

/// <summary>How a result's picture is cut.</summary>
public enum SearchResultIconShape
{
    /// <summary>As it comes, such as an application's icon, which is a rounded tile already.</summary>
    Tile,

    /// <summary>Cropped to a disc, such as a contact's photo.</summary>
    Circle,
}

/// <summary>
/// One person in an avatar: a photo, or the initials to draw on a colored disc, or, with neither, a plain silhouette.
/// </summary>
/// <param name="Initials">One or two letters, such as <c>J</c> or <c>AB</c>.</param>
/// <param name="Photo">A picture, which is drawn instead of the initials. It should be frozen.</param>
public sealed record AvatarParticipant(string? Initials = null, ImageSource? Photo = null);

/// <summary>
/// The picture at the left of a search result: a glyph the theme draws, named by its resource key; an image that came
/// with the result, such as an application's icon or a contact's photo; or the avatar of the people in a conversation,
/// which is a single disc for one person and a cluster of small ones on a larger disc for several. Only one is needed.
/// </summary>
/// <param name="GlyphKey">
/// The key, in the theme's resources, of the data template that draws the icon (for example <c>Result.Icon.File</c> in
/// <c>Themes/Controls/SearchResults.xaml</c>).
/// </param>
/// <param name="Image">A picture, which is drawn instead of the glyph. It should be frozen.</param>
/// <param name="Shape">How the image is cut.</param>
/// <param name="Participants">The people of an avatar, most important first, which is drawn instead of the rest.</param>
public sealed record SearchResultIcon(
    string? GlyphKey = null, ImageSource? Image = null, SearchResultIconShape Shape = SearchResultIconShape.Tile,
    IReadOnlyList<AvatarParticipant>? Participants = null)
{
    /// <summary>Whether the icon is an avatar.</summary>
    public bool HasParticipants => Participants is { Count: > 0 };

    /// <summary>The icon a result of <paramref name="kind"/> has when it brings none of its own.</summary>
    public static SearchResultIcon ForKind(SearchResultKind kind) => new(kind switch
    {
        SearchResultKind.App => "Result.Icon.App",
        SearchResultKind.File => "Result.Icon.File",
        SearchResultKind.Action => "Result.Icon.Action",
        SearchResultKind.Contact => "Result.Icon.Contact",
        SearchResultKind.Message => "Result.Icon.Message",
        SearchResultKind.Clipboard => "Result.Icon.Clipboard",
        _ => "Result.Icon.Knowledge",
    });

    /// <summary>An icon drawn by the theme.</summary>
    public static SearchResultIcon FromGlyph(string glyphKey) => new(glyphKey);

    /// <summary>An icon that is a picture.</summary>
    public static SearchResultIcon FromImage(ImageSource image, SearchResultIconShape shape = SearchResultIconShape.Tile) =>
        new(null, image, shape);

    /// <summary>The avatar of one person or of a group of them: up to seven are drawn.</summary>
    public static SearchResultIcon FromParticipants(IEnumerable<AvatarParticipant> participants) =>
        new(Participants: [.. participants]);

    /// <inheritdoc cref="FromParticipants(IEnumerable{AvatarParticipant})"/>
    public static SearchResultIcon FromParticipants(params AvatarParticipant[] participants) =>
        new(Participants: participants);
}

/// <summary>
/// A small mark on a result's icon, at its lower right corner, that says where the result comes from or what it opens
/// in: the phone an item came from, the application a message is in, a settings gear.
/// </summary>
/// <param name="GlyphKey">The key, in the theme's resources, of the data template that draws the badge.</param>
/// <param name="Image">A picture, which is drawn instead of the glyph.</param>
public sealed record SearchResultBadge(string? GlyphKey = null, ImageSource? Image = null)
{
    /// <summary>The Messages application's mark, on the avatar of a conversation.</summary>
    public static SearchResultBadge Messages { get; } = new("Result.Badge.Messages");

    /// <summary>A phone's mark, on what came from one.</summary>
    public static SearchResultBadge Phone { get; } = new("Result.Badge.Phone");

    /// <summary>A gear, on a setting.</summary>
    public static SearchResultBadge Settings { get; } = new("Result.Badge.Settings");

    /// <summary>A badge drawn by the theme.</summary>
    public static SearchResultBadge FromGlyph(string glyphKey) => new(glyphKey);
}

/// <summary>
/// The action a row offers while it is highlighted, drawn at its right: a few words and the key that does it, such as
/// <c>Search Brave Browser</c> and <c>tab</c>. Either may be left out.
/// </summary>
/// <param name="Text">The words.</param>
/// <param name="Key">The key, as written on its cap.</param>
public sealed record SearchResultActionHint(string? Text = null, string? Key = null);
