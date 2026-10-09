using System.IO;
using System.Linq;
using System.Windows.Controls;
using Assistant.Core.QuickSearch;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

// A right-click on a file or a folder in the results: open the folder it is in, or copy the file itself.
public sealed partial class PromptInputControlTests
{
    [Fact]
    public async Task CopyFilePutsTheFileItselfOnTheClipboard_AndOneThatHasGoneIsSaidAndNotCopied()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kiri-copy-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "Budget 2026.txt");
            File.WriteAllText(path, "numbers");
            var (files, _) = QuickFiles([FoundFile(path), FoundFolder(folder), FoundFile(Path.Combine(folder, "gone.txt"))], profile: folder);
            var kit = new QuickKit([files]).Listen();

            var rows = (await kit.SearchAsync("budget"))[^1].Sections.SelectMany(section => section.Items).ToDictionary(row => row.Title);
            var file = rows["Budget 2026.txt"];

            // The file itself, as File Explorer copies it, and not its path as text.
            file.Alternates.Single(alternate => alternate.Title == "Copy file").Command.Execute(null);
            Assert.Equal([path], kit.Clipboard.Files);
            Assert.Empty(kit.Clipboard.Copied);
            Assert.Equal(1, kit.Finished);

            // A folder is copied the same way, and "Open file location" shows either in its folder.
            var folderRow = rows[Path.GetFileName(folder)];
            folderRow.Alternates.Single(alternate => alternate.Title == "Copy folder").Command.Execute(null);
            Assert.Equal([path, folder], kit.Clipboard.Files);
            file.Alternates.Single(alternate => alternate.Title == "Open file location").Command.Execute(null);
            Assert.Equal([path], kit.Files.Revealed);

            // One that was moved or deleted since it was listed is said, and nothing is put on the clipboard.
            rows["gone.txt"].Alternates.Single(alternate => alternate.Title == "Copy file").Command.Execute(null);
            Assert.Equal(2, kit.Clipboard.Files.Count);
            Assert.Contains(kit.Failures, failure => failure.Contains("could not be copied", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ARowsRightClickMenuListsWhatTheResultCanDo_AndARowWithNothingElseToDoHasNone() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (kit, assistant, results) = CreateBraveBar(frames);
        var (window, _, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            var input = Named<PromptInputControl>(window, "PromptInput");
            input.Text = "brave";
            WaitUntil(() => results.SelectedItem is not null, "The best match was not highlighted.");
            Pump();

            var rows = Descendants<SearchResultRow>(Named<ItemsControl>(window, "ResultsList")).ToArray();
            Assert.Equal(["Brave Browser", "Brave Browser Beta", "Brave Browser Nightly"], rows.Select(row => row.Title));

            // The row with other things to do has a menu of them, each one a command of the result; every row has a menu of its own.
            var menu = rows[0].ContextMenu;
            Assert.NotNull(menu);
            Assert.True(ContextMenuService.GetIsEnabled(rows[0]));
            menu.PlacementTarget = rows[0];
            menu.DataContext = rows[0].DataContext;
            Assert.Equal(["Show in File Explorer", "Copy path"], menu.Items.Cast<SearchResultAlternate>().Select(alternate => alternate.Title));
            Assert.NotSame(menu, rows[1].ContextMenu);

            // A row with nothing else to do opens no menu.
            Assert.False(ContextMenuService.GetIsEnabled(rows[1]));

            // Choosing one runs it, as Tab and Enter would.
            menu.Items.Cast<SearchResultAlternate>().Single(alternate => alternate.Title == "Copy path").Command.Execute(null);
            Assert.Equal([@"C:\Program Files\Brave\brave.exe"], kit.Clipboard.Copied);
        }
        finally { window.Close(); }
    }));

    [Fact]
    public void TheRightMouseButtonOpensTheFirstRowsMenuItself_WhateverThePointerWasLastKnownToBeOver() => RunSta(() => WithTheme(() =>
    {
        var frames = new List<FakeFrames>();
        var (_, assistant, results) = CreateBraveBar(frames);
        var (window, _, _) = assistant;
        try
        {
            assistant.Controller.Invoke();
            Advance(frames[0], 300);
            Named<PromptInputControl>(window, "PromptInput").Text = "brave";
            WaitUntil(() => results.SelectedItem is not null, "The best match was not highlighted.");
            Pump();

            // The first row is the highlighted one, the top hit: the row the list appears under the pointer with.
            var rows = Descendants<SearchResultRow>(Named<ItemsControl>(window, "ResultsList")).ToArray();
            Assert.True(rows[0].IsSelected);
            Assert.True(rows[0].HasMenu);
            Assert.False(rows[1].HasMenu);

            static System.Windows.Input.MouseButtonEventArgs Right(System.Windows.RoutedEvent what) =>
                new(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right) { RoutedEvent = what };

            // Pressed and let go over the row, the button is the row's: it opens the menu, and nothing else is left to do with the click.
            var down = Right(System.Windows.UIElement.MouseRightButtonDownEvent);
            rows[0].RaiseEvent(down);
            Assert.True(down.Handled);
            var up = Right(System.Windows.UIElement.MouseRightButtonUpEvent);
            rows[0].RaiseEvent(up);
            Pump();
            Assert.True(up.Handled);
            var menu = rows[0].ContextMenu!;
            Assert.True(menu.IsOpen);
            Assert.Same(rows[0], menu.PlacementTarget);
            Assert.Equal(["Show in File Explorer", "Copy path"], menu.Items.Cast<SearchResultAlternate>().Select(alternate => alternate.Title));
            menu.IsOpen = false;
            Pump();

            // A row with nothing else to do leaves the button alone and opens nothing.
            var nothing = Right(System.Windows.UIElement.MouseRightButtonUpEvent);
            rows[1].RaiseEvent(nothing);
            Assert.False(nothing.Handled);
            Assert.False(rows[1].OpenMenu());
        }
        finally { window.Close(); }
    }));
}