using System.Windows.Input;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>
/// Made-up people and conversations for the sample message and contact results (PROJECT_SPEC §4.1). Nothing here is
/// read from a messaging app or from the user: names, snippets and photos are invented, and messaging is not
/// integrated. It shows the treatments: a group's cluster avatar, a person's initials or photo, an Arabic name, and
/// the date or status at the right.
/// </summary>
internal sealed class FakeMessageDirectory(TimeProvider time)
{
    private static readonly AvatarParticipant[] Group =
    [
        new(Photo: DemoImages.Portrait(0)), new("J"), new(Photo: DemoImages.Portrait(1)), new("AB"), new("SA"),
        new(Photo: DemoImages.Portrait(2)), new(),
    ];

    /// <summary>The conversations and contacts that match <paramref name="query"/>; all of them for an empty one.</summary>
    public IReadOnlyList<SearchResultViewModel> Search(string query)
    {
        var now = time.GetLocalNow();
        ICommand nothing = new RelayCommand(() => { });

        var conversations = new (string Name, AvatarParticipant[] People, string Snippet, DateTimeOffset When, string? Status)[]
        {
            // The name is Arabic, as in the reference, and its date is old enough to be written out.
            ("الإخوة", Group,
                "(If you need a browser version (only works on brave) let me know, I have one ready to share…",
                new DateTimeOffset(2026, 4, 25, 18, 30, 0, now.Offset), null),
            ("Alex Morgan", [new("AM")], "Sent you the sample slides, take a look when you get a chance.", now.AddHours(-2), null),
            ("Weekend plans", [new(Photo: DemoImages.Portrait(3)), new("KL"), new()],
                "Anyone up for a hike on Saturday? The trail should be dry by then.", now.AddDays(-1), null),
            ("Priya Nair", [new(Photo: DemoImages.Portrait(1))], "The brave new schedule works for me, thanks for sorting it out.", now.AddDays(-3), null),
            ("Sam Rivera", [new()], "Draft: see you at the station at eight", now.AddDays(-5), "Draft"),
        };

        var people = new (string Name, AvatarParticipant Avatar, string Line, string? Status)[]
        {
            ("Alex Morgan", new("AM"), "Mobile", null),
            ("Priya Nair", new(Photo: DemoImages.Portrait(1)), "priya@example.com", null),
            ("Sam Rivera", new(), "Mobile", "Missed"),
        };

        var messages = conversations
            .Where(c => Matches(query, c.Name) || Matches(query, c.Snippet))
            .Select(c => SearchResultFactory.Message(c.Name, c.Snippet, c.People, nothing, c.When, now, c.Status));
        var contacts = people
            .Where(p => Matches(query, p.Name))
            .Select(p => SearchResultFactory.Contact(p.Name, p.Avatar, nothing, p.Line, p.Status));
        return [.. messages, .. contacts];
    }

    private static bool Matches(string query, string text) =>
        query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase);
}
