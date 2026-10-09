using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.People;
using Assistant.UI.Settings;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    private sealed class FailingPeopleStore(bool onList = false, bool onSave = false, bool onDelete = false) : IPersonStore
    {
        public event System.EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<Person>> ListAsync(CancellationToken cancellationToken = default) =>
            onList ? throw new PersonStoreException("broken") : Task.FromResult<IReadOnlyList<Person>>([]);

        public Task<Person?> GetAsync(System.Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Person?>(null);

        public Task<Person> SaveAsync(Person person, CancellationToken cancellationToken = default) =>
            onSave ? throw new PersonStoreException("broken") : Task.FromResult(person);

        public Task<bool> DeleteAsync(System.Guid id, CancellationToken cancellationToken = default) =>
            onDelete ? throw new PersonStoreException("broken") : Task.FromResult(true);
    }

    private static readonly System.DateTimeOffset PeopleNow = new(2026, 10, 2, 12, 0, 0, System.TimeSpan.Zero);

    private static (SettingsKit Kit, InMemoryPersonStore Store) PeopleKit(params (string Name, string[] Relationships, string[] Aliases)[] people)
    {
        var store = new InMemoryPersonStore();
        foreach (var (name, relationships, aliases) in people)
        {
            store.SaveAsync(Person.Create(name, PeopleNow) with { Relationships = relationships, Aliases = aliases }).GetAwaiter().GetResult();
        }

        var kit = CreateSettingsKit(people: store, personResolver: new PersonResolver(store));
        return (kit, store);
    }

    private static PersonItem PersonRow(SettingsKit kit, string title) => kit.Model.People.People.Single(item => item.Title == title);

    // ---- the section ---------------------------------------------------------------------------------------------------

    [Fact]
    public void PeopleIsASectionOfTheSettingsWindowRightAfterPermissions() => RunSta(() =>
    {
        var kit = CreateSettingsKit();

        var titles = kit.Model.Sections.Select(section => section.Title).ToList();

        Assert.Equal(titles.IndexOf("Permissions") + 1, titles.IndexOf("People"));
        kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.People);
        Assert.Same(kit.Model.People, kit.Model.CurrentPage);
    });

    // ---- listing ------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePeopleAreListedByNameWithWhatIsKnownOfEach() => RunSta(() =>
    {
        var (kit, _) = PeopleKit(("Sara Ahmed", ["Sister"], []), ("Omar Hassan", ["Brother"], ["Bro", "Omi"]), ("Zed", [], []));

        var page = kit.Model.People;

        Assert.True(page.HasStore);
        Assert.True(page.HasPeople);
        Assert.False(page.HasNoPeople);
        Assert.Equal(["Omar Hassan", "Sara Ahmed", "Zed"], page.People.Select(item => item.Title));
        Assert.Equal("Brother · Also called Bro, Omi · No way to reach them yet", PersonRow(kit, "Omar Hassan").Subtitle);
        Assert.Equal("Sister · No way to reach them yet", PersonRow(kit, "Sara Ahmed").Subtitle);
        Assert.Equal("No way to reach them yet", PersonRow(kit, "Zed").Subtitle);
        Assert.All(page.People, item => Assert.False(item.IsEditing));
    });

    [Fact]
    public void WithNoOneSavedThePageSaysWhatPeopleAreFor() => RunSta(() =>
    {
        var (kit, _) = PeopleKit();

        var page = kit.Model.People;

        Assert.True(page.HasNoPeople);
        Assert.Equal("No one yet.", page.EmptyText);

    });

    [Fact]
    public void WithoutAStoreThePageCannotKeepAnyone() => RunSta(() =>
    {
        var page = CreateSettingsKit().Model.People;

        Assert.False(page.HasStore);
        Assert.False(page.CanCheck);
        Assert.False(page.AddCommand.CanExecute(null));
        Assert.Empty(page.People);
    });

    [Fact]
    public void WhenThePeopleCannotBeReadThePageSaysSo_AndTheOtherSettingsStillWork() => RunSta(() =>
    {
        var kit = CreateSettingsKit(people: new FailingPeopleStore(onList: true));

        Assert.True(kit.Model.People.HasNotice);
        Assert.Contains("couldn't be read", kit.Model.People.Notice, System.StringComparison.Ordinal);
        Assert.Empty(kit.Model.People.People);
        Assert.False(kit.Model.HasNotice);
    });

    [Fact]
    public void OpeningTheWindowAgainReadsThePeopleAgainAndDropsWhatWasBeingEdited() => RunSta(() =>
    {
        var (kit, store) = PeopleKit(("Omar", ["Brother"], []));
        PersonRow(kit, "Omar").EditCommand.Execute(null);
        PersonRow(kit, "Omar").Name = "Changed but not saved";
        store.SaveAsync(Person.Create("Sara", PeopleNow)).GetAwaiter().GetResult();

        kit.Model.LoadAsync().GetAwaiter().GetResult();

        Assert.Equal(["Omar", "Sara"], kit.Model.People.People.Select(item => item.Title));
        Assert.All(kit.Model.People.People, item => Assert.False(item.IsEditing));
    });

    // ---- adding ------------------------------------------------------------------------------------------------------

    [Fact]
    public void APersonIsDefinedWithAliasesARelationshipAndAWayToReachThemAndIsKeptOnSave() => RunSta(() =>
    {
        var (kit, store) = PeopleKit(("Amal", [], []), ("Sara", ["Sister"], []));
        var page = kit.Model.People;

        page.AddCommand.Execute(null);
        var item = page.People.Last();
        Assert.True(item.IsNew);
        Assert.True(item.IsEditing);
        Assert.Equal("New person", item.Title);
        Assert.Equal("Not saved yet", item.Subtitle);
        Assert.False(page.AddCommand.CanExecute(null));

        item.Name = "Omar Hassan";
        item.Aliases = "Bro, Omi ;  Omar H";
        item.Relationships = "my Brother";
        item.AddIdentifierCommand.Execute(null);
        var phone = item.Identifiers.Single();
        phone.Value = "+44 7700 900123";
        phone.Service = "WhatsApp";
        item.AddIdentifierCommand.Execute(null);
        item.Identifiers[1].Kind = item.Kinds.Single(kind => kind.Kind == PersonIdentifierKind.Email);
        item.Identifiers[1].Value = "omar@example.com";
        Assert.Equal("Omar Hassan", item.Title);
        Assert.DoesNotContain(store.ListAsync().GetAwaiter().GetResult(), person => person.DisplayName == "Omar Hassan");

        item.SaveCommand.Execute(null);

        var kept = store.ListAsync().GetAwaiter().GetResult().Single(person => person.DisplayName == "Omar Hassan");
        Assert.Equal(["Bro", "Omar H", "Omi"], kept.Aliases);
        Assert.Equal(["Brother"], kept.Relationships);
        Assert.Equal(
            [new PersonIdentifier(PersonIdentifierKind.Phone, "+44 7700 900123", "WhatsApp"), new PersonIdentifier(PersonIdentifierKind.Email, "omar@example.com")],
            kept.Identifiers);
        Assert.False(item.IsNew);
        Assert.False(item.IsEditing);
        Assert.False(item.HasMessage);
        Assert.Equal("Brother · Also called Bro, Omar H, Omi · 2 ways to reach them", item.Subtitle);
        Assert.Equal(["Amal", "Omar Hassan", "Sara"], page.People.Select(row => row.Title));
        Assert.True(page.AddCommand.CanExecute(null));
    });

    [Fact]
    public void AddingDoesNotTouchTheSettingsFile() => RunSta(() =>
    {
        var (kit, _) = PeopleKit();
        var before = kit.Saved;

        kit.Model.People.AddCommand.Execute(null);
        kit.Model.People.People[0].Name = "Omar";
        kit.Model.People.People[0].SaveCommand.Execute(null);
        kit.Settle();

        Assert.Equal(before, kit.Saved);
        Assert.False(kit.Model.HasNotice);
    });

    [Fact]
    public void AWayToReachThemThatIsTakenOutIsNotKept() => RunSta(() =>
    {
        var (kit, store) = PeopleKit();
        kit.Model.People.AddCommand.Execute(null);
        var item = kit.Model.People.People[0];
        item.Name = "Omar";
        item.AddIdentifierCommand.Execute(null);
        item.AddIdentifierCommand.Execute(null);
        item.Identifiers[0].Kind = item.Kinds.Single(kind => kind.Kind == PersonIdentifierKind.Username);
        item.Identifiers[0].Value = "omar_h";
        item.Identifiers[1].RemoveCommand.Execute(null);

        item.SaveCommand.Execute(null);

        Assert.Equal(["omar_h"], store.ListAsync().GetAwaiter().GetResult().Single().Identifiers.Select(identifier => identifier.Value));
    });

    [Fact]
    public void WhatCannotBeKeptIsExplainedAndThePersonStaysOpenToFix() => RunSta(() =>
    {
        var (kit, store) = PeopleKit();
        kit.Model.People.AddCommand.Execute(null);
        var item = kit.Model.People.People[0];

        item.SaveCommand.Execute(null);
        Assert.Equal("A person needs a name.", item.Message);
        Assert.True(item.HasMessage);
        Assert.True(item.IsEditing);
        Assert.True(item.IsNew);

        item.Name = "Omar";
        item.AddIdentifierCommand.Execute(null);
        item.Identifiers[0].Value = "call me maybe";
        item.SaveCommand.Execute(null);
        Assert.Contains("A phone number has 5 to 20 digits", item.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("call me maybe", item.Message, System.StringComparison.Ordinal);
        Assert.Empty(store.ListAsync().GetAwaiter().GetResult());

        item.Identifiers[0].Value = "07700 900123";
        item.SaveCommand.Execute(null);
        Assert.False(item.HasMessage);
        Assert.False(item.IsEditing);
        Assert.Single(store.ListAsync().GetAwaiter().GetResult());
    });

    [Fact]
    public void AFailureToSaveIsSaidInWordsAndNothingTheUserTypedIsLost() => RunSta(() =>
    {
        var kit = CreateSettingsKit(people: new FailingPeopleStore(onSave: true));
        kit.Model.People.AddCommand.Execute(null);
        var item = kit.Model.People.People[0];
        item.Name = "Omar";
        item.Aliases = "Bro";

        item.SaveCommand.Execute(null);

        Assert.Contains("couldn't be saved", item.Message, System.StringComparison.Ordinal);
        Assert.True(item.IsEditing);
        Assert.Equal("Omar", item.Name);
        Assert.Equal("Bro", item.Aliases);
        Assert.False(item.Busy);
        Assert.True(item.SaveCommand.CanExecute(null));
    });

    [Fact]
    public void GivingUpOnANewPersonDropsThem_AndGivingUpOnAnEditPutsBackWhatWasKept() => RunSta(() =>
    {
        var (kit, store) = PeopleKit(("Omar", ["Brother"], ["Bro"]));
        var page = kit.Model.People;

        page.AddCommand.Execute(null);
        page.People.Last().Name = "Never kept";
        page.People.Last().CancelCommand.Execute(null);
        Assert.Equal(["Omar"], page.People.Select(item => item.Title));
        Assert.True(page.AddCommand.CanExecute(null));

        var omar = PersonRow(kit, "Omar");
        omar.EditCommand.Execute(null);
        omar.Name = "Changed";
        omar.Aliases = "Other";
        omar.Relationships = "Cousin";
        omar.AddIdentifierCommand.Execute(null);
        omar.CancelCommand.Execute(null);

        Assert.False(omar.IsEditing);
        Assert.Equal(("Omar", "Bro", "Brother"), (omar.Name, omar.Aliases, omar.Relationships));
        Assert.Empty(omar.Identifiers);
        Assert.Equal("Omar", store.ListAsync().GetAwaiter().GetResult().Single().DisplayName);
    });

    // ---- editing -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ChangingAPersonKeepsTheirIdAndMovesThemToWhereTheirNewNameGoes() => RunSta(() =>
    {
        var (kit, store) = PeopleKit(("Amal", [], []), ("Omar", ["Brother"], []), ("Sara", [], []));
        var id = store.ListAsync().GetAwaiter().GetResult().Single(person => person.DisplayName == "Omar").Id;
        var omar = PersonRow(kit, "Omar");

        omar.EditCommand.Execute(null);
        omar.Name = "Zoe";
        omar.SaveCommand.Execute(null);

        Assert.Equal(["Amal", "Sara", "Zoe"], kit.Model.People.People.Select(item => item.Title));
        Assert.Equal(id, store.ListAsync().GetAwaiter().GetResult().Single(person => person.DisplayName == "Zoe").Id);
        Assert.Equal(3, store.ListAsync().GetAwaiter().GetResult().Count);
    });

    [Fact]
    public void ThereIsALimitToTheWaysToReachSomeone() => RunSta(() =>
    {
        var (kit, _) = PeopleKit();
        kit.Model.People.AddCommand.Execute(null);
        var item = kit.Model.People.People[0];

        for (var index = 0; index < PersonRules.MaxIdentifiers; index++)
        {
            Assert.True(item.AddIdentifierCommand.CanExecute(null));
            item.AddIdentifierCommand.Execute(null);
        }

        Assert.False(item.AddIdentifierCommand.CanExecute(null));
    });

    // ---- removing ----------------------------------------------------------------------------------------------------

    [Fact]
    public void RemovingAPersonAsksOnceMore_AndKeepingThemChangesNothing() => RunSta(() =>
    {
        var (kit, store) = PeopleKit(("Omar", ["Brother"], []));
        var omar = PersonRow(kit, "Omar");

        omar.RemoveCommand.Execute(null);
        Assert.True(omar.ConfirmingRemove);
        Assert.Contains("Remove Omar?", omar.RemoveQuestion, System.StringComparison.Ordinal);
        Assert.Single(store.ListAsync().GetAwaiter().GetResult());

        omar.KeepCommand.Execute(null);

        Assert.False(omar.ConfirmingRemove);
        Assert.Single(store.ListAsync().GetAwaiter().GetResult());
        Assert.Single(kit.Model.People.People);
    });

    [Fact]
    public void ConfirmingRemovesThePersonFromTheStoreAndTheList() => RunSta(() =>
    {
        var (kit, store) = PeopleKit(("Omar", ["Brother"], []), ("Sara", [], []));

        PersonRow(kit, "Omar").RemoveCommand.Execute(null);
        PersonRow(kit, "Omar").ConfirmRemoveCommand.Execute(null);

        Assert.Equal(["Sara"], kit.Model.People.People.Select(item => item.Title));
        Assert.Equal(["Sara"], store.ListAsync().GetAwaiter().GetResult().Select(person => person.DisplayName));
        Assert.Equal(PersonResolutionOutcome.NotFound, new PersonResolver(store).ResolveAsync("my brother").GetAwaiter().GetResult().Outcome);
    });

    [Fact]
    public void AFailureToRemoveIsSaidInWordsAndThePersonStays() => RunSta(() =>
    {
        var failing = new FailingPeopleStore(onDelete: true);
        var kit = CreateSettingsKit(people: failing);
        kit.Model.People.AddCommand.Execute(null);
        var item = kit.Model.People.People[0];
        item.Name = "Omar";
        item.SaveCommand.Execute(null);

        item.RemoveCommand.Execute(null);
        item.ConfirmRemoveCommand.Execute(null);

        Assert.Contains("couldn't be removed", item.Message, System.StringComparison.Ordinal);
        Assert.False(item.ConfirmingRemove);
        Assert.Single(kit.Model.People.People);
    });

    [Fact]
    public void ANewPersonCannotBeRemovedOnlyGivenUpOn() => RunSta(() =>
    {
        var (kit, _) = PeopleKit();
        kit.Model.People.AddCommand.Execute(null);

        Assert.False(kit.Model.People.People[0].RemoveCommand.CanExecute(null));
        Assert.True(kit.Model.People.People[0].CancelCommand.CanExecute(null));
    });

    // ---- trying a name ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCheckSaysWhoMyBrotherIs() => RunSta(() =>
    {
        var (kit, _) = PeopleKit(("Omar Hassan", ["Brother"], []), ("Sara Ahmed", ["Sister"], []));
        var page = kit.Model.People;

        Assert.True(page.CanCheck);
        Assert.False(page.CheckCommand.CanExecute(null));
        page.CheckText = "my brother";
        Assert.True(page.CheckCommand.CanExecute(null));
        page.CheckCommand.Execute(null);

        Assert.Equal("That is Omar Hassan.", page.CheckResult);
        Assert.True(page.HasCheckResult);
    });

    [Fact]
    public void TheCheckAsksWhichOneWhenTwoFit_AndSaysWhenNoneDoes() => RunSta(() =>
    {
        var (kit, _) = PeopleKit(("Omar Hassan", ["Brother"], []), ("Sami Hassan", ["Brother"], []));
        var page = kit.Model.People;

        page.CheckText = "my bro";
        page.CheckCommand.Execute(null);
        Assert.Equal("More than one person fits “my bro”: Omar Hassan and Sami Hassan. Which one do you mean?", page.CheckResult);

        page.CheckText = "my sister";
        page.CheckCommand.Execute(null);
        Assert.Equal("I don't have anyone saved as “my sister”. You can add them in Settings, under People.", page.CheckResult);
    });

    [Fact]
    public void TheAnswerIsClearedAsSoonAsTheNameChanges_AndNothingIsAskedOfBlankText() => RunSta(() =>
    {
        var (kit, _) = PeopleKit(("Omar", ["Brother"], []));
        var page = kit.Model.People;
        page.CheckText = "Omar";
        page.CheckCommand.Execute(null);
        Assert.True(page.HasCheckResult);

        page.CheckText = "Omar H";
        Assert.False(page.HasCheckResult);

        page.CheckText = "  ?! ";
        Assert.False(page.CheckCommand.CanExecute(null));
    });

    [Fact]
    public void ThePageUsesWhatIsSavedAtOnce_SoAnAddedBrotherIsFoundAtOnce() => RunSta(() =>
    {
        var (kit, _) = PeopleKit();
        var page = kit.Model.People;
        page.CheckText = "my brother";
        page.CheckCommand.Execute(null);
        Assert.Contains("No one is saved yet", page.CheckResult, System.StringComparison.Ordinal);

        page.AddCommand.Execute(null);
        page.People[0].Name = "Omar";
        page.People[0].Relationships = "Brother";
        page.People[0].SaveCommand.Execute(null);
        page.CheckText = "my brother";
        page.CheckCommand.Execute(null);

        Assert.Equal("That is Omar.", page.CheckResult);
    });

    // ---- how the page is drawn --------------------------------------------------------------------------------------------

    [Fact]
    public void ThePeoplePageIsDrawnWithItsPeopleItsEditorAndTheCheckAndNoBindingErrors() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new System.Uri("/Assistant.UI;component/Themes/Theme.xaml", System.UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var (kit, store) = PeopleKit(("Omar Hassan", ["Brother"], ["Bro", "Omi"]), ("Sara Ahmed", ["Sister"], []));
        var omar = store.ListAsync().GetAwaiter().GetResult().Single(person => person.DisplayName == "Omar Hassan");
        store.SaveAsync(omar with { Identifiers = [new(PersonIdentifierKind.Phone, "+44 7700 900123", "WhatsApp")] }).GetAwaiter().GetResult();
        kit.Model.LoadAsync().GetAwaiter().GetResult();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.People);
            Pump();
            Pump();

            var texts = AllTextOf(window).ToList();
            Assert.Contains("People", texts);
            Assert.Contains("Omar Hassan", texts);
            Assert.Contains("Sara Ahmed", texts);
            Assert.Contains("Brother · Also called Bro, Omi · 1 way to reach them", texts);
            Assert.Contains("Try a name", texts);
            Assert.Equal(2, Descendants<Button>(window).Count(button => button.Content as string == "Edit" && button.IsVisible));
            Assert.Equal(2, Descendants<Button>(window).Count(button => button.Content as string == "Remove" && button.IsVisible));
            Assert.True(ButtonsVisible(window, "Add a person"));
            Assert.True(ButtonsVisible(window, "Check"));
            RenderFixture(window, "settings-people-list.png", 2);

            PersonRow(kit, "Omar Hassan").EditCommand.Execute(null);
            PersonRow(kit, "Omar Hassan").AddIdentifierCommand.Execute(null);
            Pump();
            Pump();

            var editing = AllTextOf(window).ToList();
            Assert.Contains("Name", editing);
            Assert.Contains("Also called", editing);
            Assert.Contains("Relationship", editing);
            Assert.Contains("Ways to reach them", editing);
            Assert.True(ButtonsVisible(window, "Save"));
            Assert.True(ButtonsVisible(window, "Cancel"));
            Assert.True(ButtonsVisible(window, "Add a way to reach them"));
            Assert.Equal(2, Descendants<ComboBox>(window).Count(box => box.IsVisible));
            Assert.Contains(Descendants<TextBox>(window), box => box.IsVisible && (string)box.GetValue(System.Windows.Automation.AutomationProperties.NameProperty) == "Relationship" && box.Text == "Brother");
            RenderFixture(window, "settings-people-editing.png", 2);

            PersonRow(kit, "Omar Hassan").CancelCommand.Execute(null);
            PersonRow(kit, "Sara Ahmed").RemoveCommand.Execute(null);
            Pump();
            Assert.Contains(AllTextOf(window), text => text.StartsWith("Remove Sara Ahmed? Their name, aliases", System.StringComparison.Ordinal));
            Assert.True(ButtonsVisible(window, "Keep"));
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void WithNoOneSavedThePeoplePageShowsTheEmptyWordsAndTheAddButton() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new System.Uri("/Assistant.UI;component/Themes/Theme.xaml", System.UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var (kit, _) = PeopleKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.People);
            Pump();

            Assert.Contains(AllTextOf(window), text => text == "No one yet.");
            Assert.True(ButtonsVisible(window, "Add a person"));

            kit.Model.People.AddCommand.Execute(null);
            Pump();
            Pump();
            Assert.Contains("New person", AllTextOf(window));
            Assert.True(ButtonsVisible(window, "Save"));
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void WithoutAStoreThePeoplePageOffersNothingToChange() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new System.Uri("/Assistant.UI;component/Themes/Theme.xaml", System.UriKind.Relative) });
        var kit = CreateSettingsKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.People);
            Pump();

            var texts = AllTextOf(window).ToList();
            Assert.DoesNotContain("Try a name", texts);
            Assert.False(ButtonsVisible(window, "Add a person"));
        }
        finally
        {
            window.CloseForGood();
            app.Resources.MergedDictionaries.Clear();
        }
    });
}
