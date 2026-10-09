using Assistant.Core.People;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// Who "my brother" is (PROJECT_SPEC §4.8, step 112): decided from the people the user saved, by fixed rules, and never guessed: when two people fit, the
/// answer is a question for the user, and when none does, nothing is chosen.
/// </summary>
public sealed class PersonResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryPersonStore _store = new();
    private readonly PersonResolver _resolver;

    public PersonResolverTests() => _resolver = new PersonResolver(_store);

    [Theory]
    [InlineData("my brother")]
    [InlineData("My Brother")]
    [InlineData("  my   brother!  ")]
    [InlineData("my bro")]
    [InlineData("brother")]
    [InlineData("my brother Omar")]
    [InlineData("my brother omar hassan")]
    [InlineData("Omar")]
    [InlineData("ÖMAR")]
    [InlineData("omar hassan")]
    [InlineData("Omi")]
    [InlineData("my Omi")]
    public async Task AReferenceToTheOnePersonWhoFitsFindsThem(string reference)
    {
        var omar = await Save("Omar Hassan", relationships: ["Brother"], aliases: ["Omi"]);
        await Save("Sara Ahmed", relationships: ["Sister"]);

        var resolution = await _resolver.ResolveAsync(reference);

        Assert.Equal(PersonResolutionOutcome.Found, resolution.Outcome);
        Assert.Equal(omar.Id, resolution.Person!.Id);
        Assert.Equal(string.Empty, resolution.Message);
    }

    [Theory]
    [InlineData("my borther")]
    [InlineData("my brothr")]
    [InlineData("my brohter")]
    [InlineData("send to my brother")]
    public async Task AKeyboardSlipInARelationshipStillFindsTheOnePersonWhoFits(string reference)
    {
        var omar = await Save("Omar Hassan", relationships: ["Brother"]);
        await Save("Sara Ahmed", relationships: ["Sister"]);
        var words = reference.Replace("send to ", string.Empty, StringComparison.Ordinal);

        var resolution = await _resolver.ResolveAsync(words);

        Assert.Equal(PersonResolutionOutcome.Found, resolution.Outcome);
        Assert.Equal(omar.Id, resolution.Person!.Id);
    }

    [Fact]
    public async Task AKeyboardSlipInANameFindsThePersonOnlyWhenOneFitsAndNeverChoosesBetweenTwo()
    {
        await Save("Omar Hassan");
        await Save("Sara Ahmed");

        Assert.Equal(PersonResolutionOutcome.Found, (await _resolver.ResolveAsync("Omra Hassan")).Outcome);
        Assert.Equal(PersonResolutionOutcome.Found, (await _resolver.ResolveAsync("sarah ahmed")).Outcome);
        Assert.Equal(PersonResolutionOutcome.NotFound, (await _resolver.ResolveAsync("Zed Hassan")).Outcome);

        await Save("Omari Hassan");
        var two = await _resolver.ResolveAsync("Omar Hasan");
        Assert.Equal(PersonResolutionOutcome.Ambiguous, two.Outcome);
    }

    [Fact]
    public async Task ShortWordsAndWordsOnTheListAreNeverTakenForASlip()
    {
        await Save("Bra Smith", relationships: ["Brother"]);

        Assert.Equal(PersonResolutionOutcome.NotFound, (await _resolver.ResolveAsync("my bro tom")).Outcome);
        Assert.Null(RelationshipTerms.Closest("brother"));
        Assert.Null(RelationshipTerms.Closest("bra"));
        Assert.Equal("brother", RelationshipTerms.Closest("borther"));
        Assert.True(PersonText.IsClose("borther", "brother"));
        Assert.False(PersonText.IsClose("bro", "bra"));
        Assert.False(PersonText.IsClose("mother", "brother"));
    }

    [Theory]
    [InlineData("my mom", "Mother")]
    [InlineData("my mum", "Mom")]
    [InlineData("my mother", "Mum")]
    [InlineData("my dad", "Father")]
    [InlineData("my sis", "Sister")]
    [InlineData("my grandma", "Grandmother")]
    [InlineData("my hubby", "Husband")]
    [InlineData("my colleague", "Colleague")]
    [InlineData("my best friend", "Best Friend")]
    public async Task TheWordsPeopleUseInterchangeablyForARelativeAreTheSameRelationship(string reference, string relationship)
    {
        var person = await Save("Someone", relationships: [relationship]);

        var resolution = await _resolver.ResolveAsync(reference);

        Assert.Equal(person.Id, resolution.Person?.Id);
    }

    [Fact]
    public async Task TwoPeopleWhoFitAreAQuestion_NotAChoice()
    {
        await Save("Omar Hassan", relationships: ["Brother"]);
        await Save("Sami Hassan", relationships: ["Brother"]);
        await Save("Sara Ahmed", relationships: ["Sister"]);

        var resolution = await _resolver.ResolveAsync("my brother");

        Assert.Equal(PersonResolutionOutcome.Ambiguous, resolution.Outcome);
        Assert.Null(resolution.Person);
        Assert.Equal(["Omar Hassan", "Sami Hassan"], resolution.Candidates.Select(person => person.DisplayName));
        Assert.Equal("More than one person fits “my brother”: Omar Hassan and Sami Hassan. Which one do you mean?", resolution.Message);
    }

    [Fact]
    public async Task AQuestionListsEveryoneWhoFits()
    {
        await Save("Amal", relationships: ["Cousin"]);
        await Save("Basil", relationships: ["Cousin"]);
        await Save("Carim", relationships: ["Cousin"]);

        var resolution = await _resolver.ResolveAsync("my cousin");

        Assert.Equal("More than one person fits “my cousin”: Amal, Basil and Carim. Which one do you mean?", resolution.Message);
    }

    [Fact]
    public async Task TheNameOfTheBrotherSaysWhichOne()
    {
        await Save("Omar Hassan", relationships: ["Brother"]);
        var sami = await Save("Sami Hassan", relationships: ["Brother"]);

        var resolution = await _resolver.ResolveAsync("my brother Sami");

        Assert.Equal(sami.Id, resolution.Person?.Id);
    }

    [Fact]
    public async Task ANameThatIsNotThatRelativesIsNotTakenForOneOfThem()
    {
        await Save("Omar Hassan", relationships: ["Brother"]);
        await Save("Sami Hassan", relationships: ["Cousin"]);

        var resolution = await _resolver.ResolveAsync("my brother Sami");

        Assert.Equal(PersonResolutionOutcome.NotFound, resolution.Outcome);
        Assert.Empty(resolution.Candidates);
    }

    [Fact]
    public async Task ANicknameAndARelationshipOfTwoPeopleIsAQuestionToo()
    {
        await Save("Omar", relationships: ["Brother"]);
        await Save("Tariq", aliases: ["Bro"]);

        var resolution = await _resolver.ResolveAsync("my bro");

        Assert.Equal(PersonResolutionOutcome.Ambiguous, resolution.Outcome);
        Assert.Equal(["Omar", "Tariq"], resolution.Candidates.Select(person => person.DisplayName));
    }

    [Fact]
    public async Task AFirstNameFindsThePersonWithTheFullName_AndTwoWithTheSameFirstNameAreAQuestion()
    {
        var sara = await Save("Sara Ahmed");
        await Save("Omar Hassan");

        Assert.Equal(sara.Id, (await _resolver.ResolveAsync("Sara")).Person?.Id);

        await Save("Sara Khalil");
        var both = await _resolver.ResolveAsync("sara");

        Assert.Equal(PersonResolutionOutcome.Ambiguous, both.Outcome);
        Assert.Equal(sara.Id, (await _resolver.ResolveAsync("Sara Ahmed")).Person?.Id);
    }

    [Fact]
    public async Task ThePersonWhoseWholeNameIsAskedForWinsOverThosePartOfWhoseNameItIs()
    {
        var sara = await Save("Sara");
        await Save("Sara Khalil");

        var resolution = await _resolver.ResolveAsync("Sara");

        Assert.Equal(sara.Id, resolution.Person?.Id);
    }

    [Fact]
    public async Task ARelativeNobodyIsSavedAsIsNotFound_AndTheUserIsToldWhereToAddThem()
    {
        await Save("Sara Ahmed", relationships: ["Sister"]);

        var resolution = await _resolver.ResolveAsync("my brother");

        Assert.Equal(PersonResolutionOutcome.NotFound, resolution.Outcome);
        Assert.Equal("I don't have anyone saved as “my brother”. You can add them in Settings, under People.", resolution.Message);
    }

    [Fact]
    public async Task WithNoOneSavedTheUserIsToldToAddSomeone()
    {
        var resolution = await _resolver.ResolveAsync("my brother");

        Assert.Equal(PersonResolutionOutcome.NotFound, resolution.Outcome);
        Assert.Equal("No one is saved yet. You can add the people you message in Settings, under People.", resolution.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public async Task NothingToLookForIsSaidSo(string? reference)
    {
        await Save("Omar");

        var resolution = await _resolver.ResolveAsync(reference);

        Assert.Equal(PersonResolutionOutcome.NothingToResolve, resolution.Outcome);
        Assert.NotEqual(string.Empty, resolution.Message);
    }

    [Fact]
    public async Task APartOfAWordIsNotAWord()
    {
        await Save("Omar Hassan");

        Assert.Equal(PersonResolutionOutcome.NotFound, (await _resolver.ResolveAsync("Oma")).Outcome);
        Assert.Equal(PersonResolutionOutcome.NotFound, (await _resolver.ResolveAsync("Has")).Outcome);
    }

    [Fact]
    public async Task WhatWasAskedIsQuotedOnOneLineAndNotTooLong()
    {
        await Save("Omar");
        var resolution = await _resolver.ResolveAsync("my\r\nbrother " + new string('x', 200));

        Assert.DoesNotContain('\n', resolution.Message);
        Assert.Contains("my brother xxxx", resolution.Message, StringComparison.Ordinal);
        Assert.Contains("…”", resolution.Message, StringComparison.Ordinal);
        Assert.True(resolution.Message.Length < 200);
    }

    [Fact]
    public async Task APersonKeepsBeingFoundAfterTheyAreChanged_AndNotAfterTheyAreRemoved()
    {
        var omar = await Save("Omar", relationships: ["Brother"]);
        await _store.SaveAsync(omar with { Relationships = ["Cousin"] });

        Assert.Equal(PersonResolutionOutcome.NotFound, (await _resolver.ResolveAsync("my brother")).Outcome);
        Assert.Equal(omar.Id, (await _resolver.ResolveAsync("my cousin")).Person?.Id);

        await _store.DeleteAsync(omar.Id);

        Assert.Equal(PersonResolutionOutcome.NotFound, (await _resolver.ResolveAsync("my cousin")).Outcome);
    }

    [Fact]
    public async Task ARefusalToReadThePeopleIsNotAnAnswer()
    {
        var resolver = new PersonResolver(new BrokenStore());

        await Assert.ThrowsAsync<PersonStoreException>(() => resolver.ResolveAsync("my brother"));
    }

    [Fact]
    public async Task AResolutionPrintsWithoutTheNamesInIt()
    {
        await Save("Omar Hassan", relationships: ["Brother"]);
        await Save("Sami Hassan", relationships: ["Brother"]);

        var text = (await _resolver.ResolveAsync("my brother")).ToString();

        Assert.DoesNotContain("Omar", text, StringComparison.Ordinal);
        Assert.DoesNotContain("brother", text, StringComparison.OrdinalIgnoreCase);
    }

    private Task<Person> Save(string name, string[]? relationships = null, string[]? aliases = null) =>
        _store.SaveAsync(Person.Create(name, Now) with { Relationships = relationships ?? [], Aliases = aliases ?? [] });

    private sealed class BrokenStore : IPersonStore
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<Person>> ListAsync(CancellationToken cancellationToken = default) => throw new PersonStoreException("broken");

        public Task<Person?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new PersonStoreException("broken");

        public Task<Person> SaveAsync(Person person, CancellationToken cancellationToken = default) => throw new PersonStoreException("broken");

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new PersonStoreException("broken");
    }
}
