using System.Windows;
using System.Windows.Input;
using Assistant.Core.People;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Routing;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Assistant.Windows.Placement;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The bar opens as the bar alone, as the reference's does: its categories come when the pointer moves or the arrow keys ask. And a list that
    // ---- is up keeps still while what is typed changes: its rows stay until the new ones come, and its glass eases to a new height.

    [Fact]
    public void TheCategoriesWaitToBeAskedForWhereTheBarIsSetTo_AndArePutAwayWithIt()
    {
        var (launcher, _) = CreateLauncher();
        var bar = CreateBarModel(launcher: launcher);

        // Not set to wait, they are there whenever the field is empty, as they always were.
        Assert.True(bar.IsLauncherVisible);
        Assert.False(bar.IsLauncherWaiting);

        var changes = 0;
        bar.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(SearchOrAskViewModel.IsLauncherVisible) ? 1 : 0;
        bar.LauncherWaitsForPointer = true;
        Assert.False(bar.IsLauncherVisible);
        Assert.True(bar.IsLauncherWaiting);
        Assert.Equal(1, changes);

        // Asked for, they stay for as long as the bar is up: typing takes them away and emptying the field brings them back.
        bar.RevealLauncher();
        Assert.True(bar.IsLauncherVisible);
        Assert.False(bar.IsLauncherWaiting);
        bar.Query = "a";
        Assert.False(bar.IsLauncherVisible);
        bar.Query = "";
        Assert.True(bar.IsLauncherVisible);

        // Put away with the bar, they wait again, with nothing highlighted.
        launcher.MoveSelection(1);
        bar.ResetLauncher();
        Assert.False(bar.IsLauncherVisible);
        Assert.True(bar.IsLauncherWaiting);
        Assert.Null(launcher.SelectedItem);
    }

    [Fact]
    public void TheBarOpensAloneAndItsCategoriesComeWhenThePointerMovesOrTheArrowKeysAsk() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (launcher, commands) = CreateLauncher();
        var bar = CreateBarModel(launcher: launcher);
        bar.LauncherWaitsForPointer = true;
        var conversation = CreateConversationModel();
        ScreenPoint? pointer = new ScreenPoint(500, 400);
        var backdrops = new FakeBackdropFactory();
        var window = new AssistantWindow(
            bar, conversation, backdrops, new FakePlacement(),
            () =>
            {
                var fake = new FakeFrames();
                frames.Add(fake);
                return fake;
            },
            () => true, pointer: () => pointer)
        {
            Left = -10000, Top = -10000, Opacity = 0,
        };
        var controller = new AssistantWindowStateController(window, bar, conversation);
        try
        {
            controller.Invoke();
            Advance(frames[0], 300);
            var root = GridNamed(window, "Root");
            var layer = GridNamed(window, "LauncherLayer");

            // The bar, and nothing under it.
            Assert.False(window.IsLauncherShown);
            Assert.Equal(Visibility.Collapsed, layer.Visibility);
            Assert.Equal(28 + 91 + 64, root.Height);

            // A hand resting on the mouse is not a move.
            pointer = new ScreenPoint(502, 401);
            window.CheckPointer();
            Assert.False(window.IsLauncherShown);

            // The pointer moves: the categories come under the bar, growing down out of it and not appearing at once. The window has the room for all
            // of them from the start; the panel begins short of its height, a little over half as wide, and clear, as the reference's does, ...
            pointer = new ScreenPoint(520, 430);
            window.CheckPointer();
            Assert.True(window.IsLauncherShown);
            Assert.Equal(Visibility.Visible, layer.Visibility);
            Assert.Equal(28 + 91 + 10.5 + 215.5 + 64, root.Height);
            Assert.Equal(0, layer.Opacity);
            Assert.Equal(129, layer.Height);
            Assert.Equal(new Point(0.5, 0), layer.RenderTransformOrigin);
            var scale = Assert.IsType<System.Windows.Media.ScaleTransform>(layer.RenderTransform);
            Assert.Equal((0.55, 0.55), (Math.Round(scale.ScaleX, 3), Math.Round(scale.ScaleY, 3)));

            // ... with its blur not there yet: the blur's own window is moved by Windows sooner than this window's drawing is shown, so it is given
            // each glass two frames late, or it would run ahead of the panel as a second outline around it.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            var blurFrames = frames[5];
            Assert.True(blurFrames.Running);
            Assert.True(backdrops.Launcher!.Region.IsEmpty);
            blurFrames.Tick(blurFrames.Last + TimeSpan.FromMilliseconds(10));
            Assert.True(backdrops.Launcher.Region.IsEmpty);
            blurFrames.Tick(blurFrames.Last + TimeSpan.FromMilliseconds(10));
            Assert.Equal(520 * 0.55 * dpi, backdrops.Launcher.Region.Width, 1);
            Assert.Equal(0, backdrops.Launcher.Opacity);

            // The bar's shadow is still whole under where the panel will be: taken out at once, at the panel's full size, it showed the whole
            // panel's shape, lighter than what was around it, before the panel was there.
            var shadow = GridNamed(window, "ShadowLayer");
            var underThePanel = new Point(260, 91 + 10.5 + 60);
            Assert.Null(shadow.OpacityMask);
            Assert.True(shadow.Clip.FillContains(underThePanel));

            // ... part of the way it is taller and partly there, with the window as it was, ...
            Advance(frames[4], 60);
            Advance(frames[3], 60);
            Assert.InRange(layer.Height, 130, 215);
            Assert.InRange(layer.Opacity, 0.05, 0.95);
            Assert.InRange(scale.ScaleX, 0.551, 0.999);
            Assert.Equal(28 + 91 + 10.5 + 215.5 + 64, root.Height);

            // ... (the shadow goes from under it as much as the panel is there, and only where the panel is drawn now) ...
            Assert.IsType<System.Windows.Media.DrawingBrush>(shadow.OpacityMask);
            Assert.True(shadow.Clip.FillContains(underThePanel));

            // ... (it fades in evenly and widens fastest at first: it is wider, of what it has to widen by, than it is there) ...
            Assert.True((scale.ScaleX - 0.55) / 0.45 > layer.Opacity);

            // ... and it comes to rest at its height and its size. Its width and its fade are done before its height is.
            Advance(frames[3], 200);
            Assert.Equal(1, layer.Opacity);
            Assert.Equal((1, 1), (scale.ScaleX, scale.ScaleY));
            FinishPanel(frames);
            Assert.Equal(215.5, layer.Height);
            Assert.Equal(1, layer.Opacity);
            Assert.Equal((1, 1), (scale.ScaleX, scale.ScaleY));

            // The blur under the glass is where the glass is, at its full size, as soon as the panel is there: it is not left where the panel was
            // two frames before, and nothing holds it back any longer.
            Assert.Equal(520 * dpi, backdrops.Launcher.Region.Width, 1);
            Assert.Equal(backdrops.Created!.Opacity, backdrops.Launcher.Opacity);
            Assert.False(blurFrames.Running);

            // And the bar's shadow is out from under the panel's glass, all of it, with nothing left to fade.
            Assert.Null(shadow.OpacityMask);
            Assert.False(shadow.Clip.FillContains(underThePanel));

            // Put away and opened again, the bar is alone again, wherever the pointer is.
            window.Dismiss();
            Advance(frames[0], 300);
            Assert.False(window.IsVisible);
            controller.Invoke();
            Advance(frames[0], 300);
            window.CheckPointer();
            Assert.False(window.IsLauncherShown);
            Assert.Equal(28 + 91 + 64, root.Height);

            // The arrow keys ask for them too, and move through them.
            PressPreview(window, Key.Down);
            Assert.True(window.IsLauncherShown);
            Assert.Same(launcher.Items[0], launcher.SelectedItem);
            Assert.All(commands, command => Assert.Equal(0, command.Count));
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public void AListThatIsUpEasesToItsNewHeight_AndTheWindowIsResizedOnceForIt() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var source = new ScriptedQuickSource();
        var results = new SearchResultsViewModel(quickSource: source);
        var bar = CreateBarModel(results: results, router: new QueryRouter());
        var conversation = CreateConversationModel();
        var window = new AssistantWindow(
            bar, conversation, new FakeBackdropFactory(), new FakePlacement(),
            () =>
            {
                var fake = new FakeFrames();
                frames.Add(fake);
                return fake;
            },
            () => true)
        {
            Left = -10000, Top = -10000, Opacity = 0,
        };
        var controller = new AssistantWindowStateController(window, bar, conversation);
        try
        {
            controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            var root = GridNamed(window, "Root");
            var layer = GridNamed(window, "LauncherLayer");

            // A list arrives growing down out of the bar: it starts short of its height and clear, and the window has the room for all of it at once.
            input.Text = "wor";
            WaitUntil(() => source.Asked.Count == 1, "The source was not asked.");
            source.Asked[0].Report(Snapshot(null, QuickRow("Word"), QuickRow("WordPad"), QuickRow("Works"), QuickRow("World Clock")));
            WaitUntil(() => results.Items.Count == 4, "The list was not shown.");
            Pump();
            Assert.True(window.IsResultsShown);
            var arriving = layer.Height;
            var room = root.Height;
            Assert.Equal(0, layer.Opacity);
            FinishPanel(frames);
            var tall = layer.Height;
            Assert.InRange(arriving, tall * 0.55, tall * 0.65);
            Assert.Equal(tall, window.PanelHeight);
            Assert.Equal(28 + 91 + 10.5 + tall + 64, room);
            Assert.Equal(room, root.Height);
            Assert.Equal(1, layer.Opacity);

            // A letter is taken back and the new list is shorter: the glass does not jump to it. It stays where it was until the next frame, ...
            input.Text = "wo";
            WaitUntil(() => source.Asked.Count == 2, "The newer query was not asked.");
            source.Asked[1].Report(Snapshot(null, QuickRow("Word"), QuickRow("WordPad")));
            WaitUntil(() => results.Items.Count == 2, "The newer list was not shown.");
            Pump();
            var easing = frames[4];
            Assert.True(easing.Running);
            Assert.Equal(tall, layer.Height);
            Assert.Equal(1, layer.Opacity);

            // ... eases toward the new height, with the window keeping the taller room all the while, ...
            Advance(easing, 70);
            Assert.True(easing.Running);
            Assert.InRange(layer.Height, 1, tall - 1);
            var midway = layer.Height;
            Assert.Equal(28 + 91 + 10.5 + tall + 64, root.Height);

            // ... and rests at it, when the window takes its height too. The list was up throughout: it never faded.
            Advance(easing, 400);
            Assert.False(easing.Running);
            Assert.True(layer.Height < midway);
            Assert.Equal(layer.Height, window.PanelHeight);
            Assert.Equal(28 + 91 + 10.5 + layer.Height + 64, root.Height);
            Assert.Equal(1, layer.Opacity);

            // A longer list again: the window makes the room at once, and the glass eases into it.
            var low = layer.Height;
            input.Text = "w";
            WaitUntil(() => source.Asked.Count == 3, "The third query was not asked.");
            source.Asked[2].Report(Snapshot(null, QuickRow("Word"), QuickRow("WordPad"), QuickRow("Works"), QuickRow("World Clock")));
            WaitUntil(() => results.Items.Count == 4, "The third list was not shown.");
            Pump();
            Assert.Equal(low, layer.Height);
            Assert.Equal(28 + 91 + 10.5 + tall + 64, root.Height);
            Advance(easing, 400);
            Assert.Equal(tall, layer.Height);
        }
        finally
        {
            window.Close();
        }
    }));

    [Fact]
    public async Task AGroupKeepsItsRowsUntilItsNewOnesCome_WhenALetterIsTypedOrTakenBack_AndNotForSomethingElse()
    {
        QuickSearchResult[] programs = [QuickApp("Word"), QuickApp("WordPad"), QuickApp("Paint")];
        var apps = new FakeQuickProvider(
            "applications", QuickSearchResultType.Applications,
            answer: request => [.. programs.Where(program => program.Title.StartsWith(request.Query, StringComparison.OrdinalIgnoreCase))]);
        var actions = new FakeQuickProvider("actions", QuickSearchResultType.Actions, answer: _ => [QuickAction("Word count", "count")]);
        var kit = new QuickKit([apps, actions]);
        static string[] Titles(SearchResultsSnapshot snapshot) => [.. snapshot.Sections.SelectMany(section => section.Items).Select(item => item.Title)];

        async Task<List<SearchResultsSnapshot>> SearchWhileActionsAreSlowAsync(string query)
        {
            actions.Gate = new TaskCompletionSource();
            var snapshots = new List<SearchResultsSnapshot>();
            var running = kit.Source.SearchAsync(query, null, snapshot => { lock (snapshots) { snapshots.Add(snapshot); } }, CancellationToken.None);
            for (var waited = 0; waited < 500 && snapshots.Count == 0; waited++)
            {
                await Task.Delay(10);
            }

            Assert.NotEmpty(snapshots);
            actions.Gate.SetResult();
            await running;
            return snapshots;
        }

        var first = await kit.SearchAsync("word");
        Assert.Contains("Word count", Titles(first[^1]));

        // A letter is taken back while the actions have not answered yet: the list that is up keeps the action it had beside the new applications, ...
        var back = await SearchWhileActionsAreSlowAsync("wor");
        Assert.Contains("Word count", Titles(back[0]));
        Assert.Contains("WordPad", Titles(back[0]));
        Assert.Contains("Word count", Titles(back[^1]));

        // ... and the same when a letter is typed.
        var on = await SearchWhileActionsAreSlowAsync("word");
        Assert.Contains("Word count", Titles(on[0]));

        // Something else altogether has no rows to keep: only what has answered for it is listed.
        var other = await SearchWhileActionsAreSlowAsync("pa");
        Assert.Equal(["Paint"], Titles(other[0]));
    }

    [Fact]
    public void SomeoneRememberedInAConversationIsOnThePeoplePageAtOnce_AndWhatIsBeingTypedIsLeftAlone() => RunSta(() =>
    {
        var (kit, store) = PeopleKit();
        var page = kit.Model.People;
        Assert.True(page.HasNoPeople);

        // The Assistant asked who the user's brother is and remembered the answer: the page lists him without being opened again.
        var omar = store.SaveAsync(Person.Create("Omar Hassan", PeopleNow) with { Relationships = ["Brother"] }).GetAwaiter().GetResult();
        SettingsUntil(() => page.People.Count == 1, "the remembered person was listed");
        Assert.Equal("Omar Hassan", page.People[0].Title);
        Assert.Equal("OH", page.People[0].Initials);
        Assert.Contains("Brother", page.People[0].Subtitle, StringComparison.Ordinal);

        // While the user edits him, a change from elsewhere does not take what they typed; another person still joins the list.
        page.People[0].EditCommand.Execute(null);
        page.People[0].Name = "Omar H.";
        store.SaveAsync(omar with { Aliases = ["Omi"] }).GetAwaiter().GetResult();
        store.SaveAsync(Person.Create("Sara", PeopleNow) with { Relationships = ["Sister"] }).GetAwaiter().GetResult();
        SettingsUntil(() => page.People.Count == 2, "the second person was listed");
        Assert.Equal("Omar H.", page.People.Single(item => item.Id == omar.Id).Name);

        // The usual relationships are a press each.
        var editing = page.People.Single(item => item.Id == omar.Id);
        Assert.Contains("Brother", editing.Suggestions);
        editing.AddRelationshipCommand.Execute("Friend");
        editing.AddRelationshipCommand.Execute("brother");
        Assert.Equal("Brother, Friend", editing.Relationships);

        // Someone removed elsewhere goes from the list.
        store.DeleteAsync(page.People.Single(item => item.Title == "Sara").Id).GetAwaiter().GetResult();
        SettingsUntil(() => page.People.Count == 1, "the removed person went");
        page.Dispose();
    });

    private static void PressPreview(Window window, Key key) =>
        (Keyboard.FocusedElement as UIElement ?? window).RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
}
