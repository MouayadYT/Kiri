using Assistant.Core.People;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>What a person may be and how it is tidied (PROJECT_SPEC §4.8, step 112), and the in-memory store that applies the same rules.</summary>
public sealed class PersonRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static Person Someone() => Person.Create("Omar", Now);

    [Theory]
    [InlineData("Omar", "omar")]
    [InlineData("  Ömar  Hassan ", "omar hassan")]
    [InlineData("O'Brien-Smith", "o brien smith")]
    [InlineData("my brother!!", "my brother")]
    [InlineData("عمر", "عمر")]
    [InlineData("   ", "")]
    [InlineData("?!", "")]
    [InlineData(null, "")]
    public void FoldingIgnoresCaseAccentsPunctuationAndExtraSpaces(string? text, string folded)
    {
        Assert.Equal(folded, PersonText.Fold(text));
    }

    [Fact]
    public void ATidyPersonIsLeftAsItIs()
    {
        var person = Someone() with { Aliases = ["Omi"], Relationships = ["Brother"], Identifiers = [new(PersonIdentifierKind.Email, "omar@example.com", "Mail")] };

        var tidy = PersonRules.Normalize(person);

        Assert.Equal(person.Aliases, tidy.Aliases);
        Assert.Equal(person.Relationships, tidy.Relationships);
        Assert.Equal(person.Identifiers, tidy.Identifiers);
        Assert.Equal(person.Id, tidy.Id);
    }

    [Fact]
    public void TextsAreTrimmed_AndControlCharactersAndRunsOfSpacesAreMadeOneSpace()
    {
        var tidy = PersonRules.Normalize(Someone() with { DisplayName = "  Omar\t\r\n  Hassan\0 ", Aliases = ["\u0007Bro\u0007 man"] });

        Assert.Equal("Omar Hassan", tidy.DisplayName);
        Assert.Equal(["Bro man"], tidy.Aliases);
    }

    [Fact]
    public void RepeatsAreTakenOut_WithoutRegardToCaseOrAccents_AndTheFirstSpellingStays()
    {
        var tidy = PersonRules.Normalize(Someone() with
        {
            Aliases = ["Bro", "BRO", "bró", "Omi"],
            Relationships = ["Brother", "my brother", "BROTHER"],
        });

        Assert.Equal(["Bro", "Omi"], tidy.Aliases);
        Assert.Equal(["Brother"], tidy.Relationships);
    }

    [Fact]
    public void EveryListIsKeptInTheSameOrderWhateverOrderItWasTypedIn()
    {
        var tidy = PersonRules.Normalize(Someone() with
        {
            Aliases = ["omi", "Bro", "Zed", "abe"],
            Relationships = ["Colleague", "brother"],
            Identifiers =
            [
                new(PersonIdentifierKind.Username, "omar_h", "Signal"),
                new(PersonIdentifierKind.Email, "omar@example.com"),
                new(PersonIdentifierKind.Phone, "+44 7700 900124", "WhatsApp"),
                new(PersonIdentifierKind.Phone, "+44 7700 900123", "Signal"),
                new(PersonIdentifierKind.Phone, "+44 7700 900125", "Signal"),
            ],
        });

        Assert.Equal(["abe", "Bro", "omi", "Zed"], tidy.Aliases);
        Assert.Equal(["brother", "Colleague"], tidy.Relationships);
        Assert.Equal(
            ["Signal|+44 7700 900123", "Signal|+44 7700 900125", "WhatsApp|+44 7700 900124", "|omar@example.com", "Signal|omar_h"],
            tidy.Identifiers.Select(identifier => identifier.Service + "|" + identifier.Value));
        Assert.Equal(PersonIdentifierKind.Email, tidy.Identifiers[3].Kind);
    }

    [Fact]
    public void ABlankAliasOrRelationshipIsLeftOut()
    {
        var tidy = PersonRules.Normalize(Someone() with { Aliases = ["", "  ", "!!"], Relationships = ["my ", " "] });

        Assert.Empty(tidy.Aliases);
        Assert.Empty(tidy.Relationships);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!")]
    public void APersonNeedsAName(string name)
    {
        var failure = Assert.Throws<PersonValidationException>(() => PersonRules.Normalize(Someone() with { DisplayName = name }));

        Assert.Equal("A person needs a name.", failure.Message);
    }

    [Fact]
    public void TheLimitsAreEnforced()
    {
        Assert.Throws<PersonValidationException>(() => PersonRules.Normalize(Someone() with { DisplayName = new string('a', PersonRules.MaxNameLength + 1) }));
        Assert.Throws<PersonValidationException>(() => PersonRules.Normalize(Someone() with { Aliases = [new string('a', PersonRules.MaxAliasLength + 1)] }));
        Assert.Throws<PersonValidationException>(() => PersonRules.Normalize(Someone() with { Relationships = [new string('a', PersonRules.MaxRelationshipLength + 1)] }));
        Assert.Throws<PersonValidationException>(
            () => PersonRules.Normalize(Someone() with { Aliases = [.. Enumerable.Range(0, PersonRules.MaxAliases + 1).Select(index => "alias " + index)] }));
        Assert.Throws<PersonValidationException>(
            () => PersonRules.Normalize(Someone() with { Relationships = [.. Enumerable.Range(0, PersonRules.MaxRelationships + 1).Select(index => "relation " + index)] }));
        Assert.Throws<PersonValidationException>(
            () => PersonRules.Normalize(Someone() with { Identifiers = [.. Enumerable.Range(0, PersonRules.MaxIdentifiers + 1).Select(index => new PersonIdentifier(PersonIdentifierKind.Username, "user" + index))] }));
        Assert.Throws<PersonValidationException>(() => PersonRules.Normalize(Someone() with { Identifiers = [new(PersonIdentifierKind.Username, new string('a', PersonRules.MaxIdentifierLength + 1))] }));
        Assert.Throws<PersonValidationException>(() => PersonRules.Normalize(Someone() with { Identifiers = [new(PersonIdentifierKind.Username, "omar", new string('a', PersonRules.MaxServiceLength + 1))] }));

        var longest = PersonRules.Normalize(Someone() with
        {
            DisplayName = new string('a', PersonRules.MaxNameLength),
            Aliases = [.. Enumerable.Range(0, PersonRules.MaxAliases).Select(index => "alias " + index)],
        });
        Assert.Equal(PersonRules.MaxAliases, longest.Aliases.Count);
    }

    [Theory]
    [InlineData("+44 7700 900123")]
    [InlineData("07700 900123")]
    [InlineData("(555) 123-4567")]
    [InlineData("+1.555.123.4567")]
    [InlineData("12345")]
    public void APhoneNumberIsDigitsWithTheUsualPunctuation(string number)
    {
        var tidy = PersonRules.Normalize(Someone() with { Identifiers = [new(PersonIdentifierKind.Phone, number)] });

        Assert.Equal(number, Assert.Single(tidy.Identifiers).Value);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("call me")]
    [InlineData("555-CALL-NOW")]
    [InlineData("+44 7700 +900123")]
    [InlineData("123456789012345678901")]
    [InlineData("")]
    public void ThingsThatAreNotPhoneNumbersAreRefused(string number)
    {
        Assert.Throws<PersonValidationException>(() => PersonRules.Normalize(Someone() with { Identifiers = [new(PersonIdentifierKind.Phone, number)] }));
    }

    [Theory]
    [InlineData("omar@example.com")]
    [InlineData("o.h+news@mail.example.co.uk")]
    public void AnEmailAddressIsOneAtSignWithNamesOnBothSides(string address)
    {
        Assert.Equal(address, Assert.Single(PersonRules.Normalize(Someone() with { Identifiers = [new(PersonIdentifierKind.Email, address)] }).Identifiers).Value);
    }

    [Theory]
    [InlineData("omar")]
    [InlineData("@omar@example.com")]
    [InlineData("omar@")]
    [InlineData("@example.com")]
    [InlineData("omar @example.com")]
    [InlineData("omar@example")]
    [InlineData("omar@example.")]
    public void ThingsThatAreNotEmailAddressesAreRefused(string address)
    {
        Assert.Throws<PersonValidationException>(() => PersonRules.Normalize(Someone() with { Identifiers = [new(PersonIdentifierKind.Email, address)] }));
    }

    [Fact]
    public void AUsernameHasNoSpacesInIt_ButMayHaveAnyOtherCharacter()
    {
        var tidy = PersonRules.Normalize(Someone() with { Identifiers = [new(PersonIdentifierKind.Username, "@omar:beeper.com")] });

        Assert.Equal("@omar:beeper.com", Assert.Single(tidy.Identifiers).Value);
        Assert.Throws<PersonValidationException>(() => PersonRules.Normalize(Someone() with { Identifiers = [new(PersonIdentifierKind.Username, "omar h")] }));
    }

    [Fact]
    public void TheSameAddressWrittenTwoWaysIsKeptOnce_ButOnTwoServicesItIsKeptTwice()
    {
        var tidy = PersonRules.Normalize(Someone() with
        {
            Identifiers =
            [
                new(PersonIdentifierKind.Phone, "+44 7700 900123", "WhatsApp"),
                new(PersonIdentifierKind.Phone, "+44-7700-900-123", "whatsapp"),
                new(PersonIdentifierKind.Phone, "+44 7700 900123", "Signal"),
                new(PersonIdentifierKind.Phone, "07700 900123", "Signal"),
                new(PersonIdentifierKind.Email, "Omar@Example.com"),
                new(PersonIdentifierKind.Email, "omar@example.com"),
            ],
        });

        Assert.Equal(
            ["+44 7700 900123|Signal", "07700 900123|Signal", "+44 7700 900123|WhatsApp", "Omar@Example.com|"],
            tidy.Identifiers.Select(identifier => identifier.Value + "|" + identifier.Service));
    }

    [Fact]
    public void ThePhoneKeyKeepsTheDigitsAndAPlusInFront()
    {
        Assert.Equal("+447700900123", PersonRules.PhoneKey(" +44 (7700) 900-123"));
        Assert.Equal("07700900123", PersonRules.PhoneKey("07700 900123"));
    }

    [Fact]
    public async Task TheInMemoryStoreAppliesTheSameRules_AndKeepsWhenAPersonWasAdded()
    {
        var clock = new FakeClock(Now);
        var store = new InMemoryPersonStore(clock);
        var saved = await store.SaveAsync(Someone() with { Aliases = ["Bro", "bro"] });
        clock.Now = Now.AddHours(3);
        var changed = await store.SaveAsync(saved with { DisplayName = "Omar H", CreatedAt = DateTimeOffset.UnixEpoch });

        Assert.Equal(["Bro"], saved.Aliases);
        Assert.Equal(Now, changed.CreatedAt);
        Assert.Equal(Now.AddHours(3), changed.UpdatedAt);
        await Assert.ThrowsAsync<PersonValidationException>(() => store.SaveAsync(Someone() with { DisplayName = " " }));
        Assert.True(await store.DeleteAsync(saved.Id));
        Assert.False(await store.DeleteAsync(saved.Id));
        Assert.Empty(await store.ListAsync());
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
