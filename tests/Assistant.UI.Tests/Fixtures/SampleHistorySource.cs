using Assistant.Core.Domain;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Tests;

/// <summary>
/// Sample earlier conversations for the History window's tests: the ones in the History window's references, newest
/// first, then a few hundred older ones, so a long list can be tried. All of it is made up here; none of it is the user's.
/// The app's own History window reads the saved history instead (Assistant.UI/History).
/// </summary>
internal sealed class SampleHistorySource(TimeProvider clock) : IHistorySource
{
    /// <summary>How many sample conversations come from the references: the grid's eight, then two more the list shows.</summary>
    public const int ReferenceCount = 10;

    /// <summary>How many older sample conversations follow the reference's.</summary>
    public const int OlderCount = 240;

    // Older conversations cycle through these, each a title, a question and an answer.
    private static readonly (string Title, string Question, string Answer)[] Topics =
    [
        ("Weekend Weather", "What’s the weather this weekend?", "Saturday looks sunny with a high of 22°, and Sunday brings light rain in the afternoon, so plan anything outdoors for Saturday."),
        ("Pasta Dinner Ideas", "What can I cook with pasta, spinach and garlic?", "Try a garlicky spinach pasta: sauté the garlic in olive oil, wilt the spinach, then toss it with the pasta, lemon and parmesan."),
        ("Flight Check-In", "When does check-in open for my flight?", "Online check-in opens 24 hours before departure. Your flight leaves at 8:15 AM, so you can check in from 8:15 AM the day before."),
        ("Unit Conversion", "How many cups are in a liter?", "A liter is about 4.2 US cups."),
        ("Meeting Summary", "Summarize the notes from Tuesday’s meeting", "The team agreed to ship the beta next month, move the design review to Thursday, and follow up on the budget before Friday."),
        ("Running Playlist", "Make a playlist for a 5K run", "I put together 12 upbeat songs, about 32 minutes long, starting at a steady tempo and building toward the finish."),
        ("Birthday Reminder", "Remind me about Sam’s birthday", "I’ll remind you on the morning of October 14. Would you like a reminder a week earlier to pick up a gift?"),
        ("Budget Spreadsheet", "Help me set up a monthly budget", "Start with four columns for category, planned, actual and difference, then add rows for rent, groceries, transport, savings and fun."),
        ("Train Times", "When is the next train downtown?", "The next train downtown leaves at 5:42 PM from platform 2, and another follows at 5:57 PM."),
        ("Book Recommendations", "Recommend a good science fiction book", "You might enjoy a classic space opera with a thoughtful take on first contact. It’s long, but the pacing keeps it moving."),
        ("Plant Care", "Why are my basil leaves turning yellow?", "Yellow basil leaves usually mean too much water. Let the soil dry out between waterings and make sure the pot drains well."),
        ("Translate Phrase", "How do I say “where is the station” in Spanish?", "You can say “¿Dónde está la estación?”"),
        ("Tip Calculator", "What’s a 20% tip on $64.50?", "A 20% tip on $64.50 is $12.90, for a total of $77.40."),
        ("Resume Feedback", "Can you review the summary on my resume?", "It reads well. Lead with your strongest result, cut the second sentence, and name the tools you used most."),
        ("Coffee Brewing", "What’s the ratio for pour-over coffee?", "A good starting point is 1 gram of coffee to 16 grams of water, so about 22 grams of coffee for a 350 ml cup."),
        ("Stretching Routine", "Give me a quick morning stretch routine", "Try five minutes: neck rolls, shoulder circles, a standing forward fold, a hip opener on each side, and a gentle twist."),
        ("Package Tracking", "Where is my package?", "Your package left the regional hub this morning and is out for delivery. It should arrive by 7 PM."),
        ("Movie Night", "Find a comedy to watch tonight", "Here are three well-reviewed comedies under two hours, all available on services you already use."),
        ("Math Homework", "Solve 3x + 7 = 22", "Subtract 7 from both sides to get 3x = 15, then divide by 3, so x = 5."),
        ("Parking Location", "Where did I park?", "You parked on level 3 of the Main Street garage, near the east elevators, at 2:14 PM."),
    ];

    /// <inheritdoc/>
    public Task<IReadOnlyList<HistoryConversation>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Load());

    /// <inheritdoc/>
    public Task<IReadOnlyList<MessageViewModel>?> LoadMessagesAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MessageViewModel>?>(null);

    /// <inheritdoc/>
    public Task<IReadOnlyList<HistorySearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HistorySearchHit>>([]);

    /// <summary>The sample conversations, made afresh.</summary>
    public IReadOnlyList<HistoryConversation> Load()
    {
        var now = clock.GetLocalNow();
        var valley = DemoImages.Valley();
        var conversations = new List<HistoryConversation>
        {
            Sample("Yesterday’s Photos", "Show me my photos from yesterday", "I found 4 photos from yesterday.", now.AddMinutes(-1),
                gallery: DemoImages.Photos()),
            Sample("Teenage Engineering Design Language", "What design language does this build use?", "The design of this build shares several key characteristics with the aesthetic of Teenage Engineering products: exposed structure, bold accent colors, and playful, precise typography.", now.AddMinutes(-168.5)),
            Sample("MVP Wall Photo", "Send the MVP wall photo to the team", "It’s sent.", now.AddMinutes(-168.5).AddSeconds(-1)),
            Sample("7-Eleven Cheese Pizza", "How much is a cheese pizza at 7-Eleven?", "A large cheese pizza from 7-Eleven typically costs around $7.00, though prices vary by location.", now.AddMinutes(-168.5).AddSeconds(-2)),
            Sample("December 3 Date", "Do I have exams around Black Friday?", "Black Friday is on November 27, 2026. I don’t see any exams scheduled for that week, but you have an Anatomy exam on December 3.", now.AddMinutes(-168.5).AddSeconds(-3)),
            Sample("Apple Card Cashback", "How much cashback have I earned on my Apple Card?", "To check the lifetime Daily Cash you’ve received on your Apple Card, open the Wallet app on your iPhone and tap your card, then scroll down.", now.AddMinutes(-185)),
            // Its card shows the lake, the image attached last, not the valley the conversation started with.
            Sample("Location Identification", "And this one?", "That’s a glacial lake higher up in the same range; the color comes from fine rock flour carried down by the ice.", now.AddDays(-2),
                attachment: DemoImages.Photos()[1],
                earlier: [new MessageViewModel(MessageRole.User, "Where was this photo taken?", [valley]), new MessageViewModel(MessageRole.Assistant, "This looks like a valley in the Canadian Rockies, with glaciers above a forested valley floor.")]),
            // Its card shows the valley, attached to its second question.
            Sample("Location Identification", "Where is this?", "It looks like the same valley from lower down, near the river.", now.AddDays(-2).AddMinutes(-5),
                attachment: valley,
                earlier: [new MessageViewModel(MessageRole.User, "I’m planning a hike in the Rockies"), new MessageViewModel(MessageRole.Assistant, "Late summer is a good time to go: most trails are clear of snow and the lakes are at their bluest.")]),
            Sample("Exam Summary", "Summarize this recording", "This recording covers instructions for an upcoming 25-question exam, which will mostly consist of multiple-choice questions, with two short written answers at the end.", now.AddDays(-2).AddHours(-3)),
            Sample("Voice Memo Summary", "Summarize my latest voice memo", "In this voice memo you remind yourself to pick up groceries on the way home and to call the dentist before Friday to move your appointment.", now.AddDays(-2).AddHours(-4)),
        };

        // Older conversations, a few hours apart, reaching back a few months.
        var at = now.AddDays(-3);
        var random = new Random(23);
        for (var i = 0; i < OlderCount; i++)
        {
            var (title, question, answer) = Topics[i % Topics.Length];
            conversations.Add(Sample(title, question, answer, at));
            at = at.AddMinutes(-(180 + random.Next(600)));
        }

        return conversations;
    }

    // A conversation that ends with the question and its answer, after any earlier messages. The question can have an
    // image attached, and the answer a gallery.
    private static HistoryConversation Sample(string title, string question, string answer, DateTimeOffset at,
        IReadOnlyList<ImageItem>? gallery = null, ImageItem? attachment = null, IReadOnlyList<MessageViewModel>? earlier = null)
    {
        var reply = new MessageViewModel(MessageRole.Assistant, answer);
        if (gallery is not null)
        {
            reply.Content.Add(new ImageCollection(gallery));
        }

        var asked = new MessageViewModel(MessageRole.User, question, attachment is null ? null : [attachment]);
        return new HistoryConversation(Guid.NewGuid(), [.. earlier ?? [], asked, reply], at, title);
    }
}
