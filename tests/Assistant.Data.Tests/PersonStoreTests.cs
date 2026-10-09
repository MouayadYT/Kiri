using Assistant.Core.People;
using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.Data.Tests;

/// <summary>
/// The people the user told the Assistant about, over a real SQLite database (PROJECT_SPEC §3.5, §5.9, step 112): what is kept, how it is tidied,
/// what is removed with a person, that it survives a restart and an upgrade, and that nothing private reaches a log or an error.
/// </summary>
public sealed class PersonStoreTests : IDisposable
{
    private const string NameSecret = "the-secret-person-name";
    private const string AliasSecret = "the-secret-alias";
    private const string PhoneSecret = "+44 7700 900123";
    private const string PhoneDigits = "7700900123";
    private const string RelationshipSecret = "the-secret-relationship";

    private readonly TestDatabase _database = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly SqlitePersonStore _store;

    public PersonStoreTests()
    {
        _store = CreateStore();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task APersonIsKeptWithEverythingThatBelongsToThem()
    {
        var saved = await _store.SaveAsync(Omar() with
        {
            Aliases = ["Bro", "Omi"],
            Relationships = ["Brother"],
            Identifiers =
            [
                new PersonIdentifier(PersonIdentifierKind.Phone, PhoneSecret, "WhatsApp"),
                new PersonIdentifier(PersonIdentifierKind.Email, "omar@example.com"),
                new PersonIdentifier(PersonIdentifierKind.Username, "@omar:beeper.com", "Beeper"),
            ],
        });

        var read = Assert.Single(await _store.ListAsync());
        AssertSame(saved, read);
        AssertSame(saved, (await _store.GetAsync(saved.Id))!);
        Assert.Equal("Omar Hassan", read.DisplayName);
        Assert.Equal(["Bro", "Omi"], read.Aliases);
        Assert.Equal(["Brother"], read.Relationships);
        Assert.Equal(
            [
                new PersonIdentifier(PersonIdentifierKind.Phone, PhoneSecret, "WhatsApp"),
                new PersonIdentifier(PersonIdentifierKind.Email, "omar@example.com"),
                new PersonIdentifier(PersonIdentifierKind.Username, "@omar:beeper.com", "Beeper"),
            ],
            read.Identifiers);
    }

    [Fact]
    public async Task PeopleAreListedByName_WithoutRegardToCase()
    {
        await _store.SaveAsync(Person.Create("sami", _database.Time.GetUtcNow()));
        await _store.SaveAsync(Person.Create("Omar", _database.Time.GetUtcNow()));
        await _store.SaveAsync(Person.Create("Amal", _database.Time.GetUtcNow()));

        Assert.Equal(["Amal", "Omar", "sami"], (await _store.ListAsync()).Select(person => person.DisplayName));
    }

    [Fact]
    public async Task APersonIsTidiedBeforeItIsKept()
    {
        var saved = await _store.SaveAsync(Omar() with
        {
            DisplayName = "  Omar   Hassan ",
            Aliases = [" Bro ", "bro", "BRO", "  ", "Omi"],
            Relationships = ["my Brother", "brother", "Colleague"],
        });

        Assert.Equal("Omar Hassan", saved.DisplayName);
        Assert.Equal(["Bro", "Omi"], saved.Aliases);
        Assert.Equal(["Brother", "Colleague"], saved.Relationships);
    }

    [Fact]
    public async Task ChangingAPersonReplacesWhatBelongsToThem_AndKeepsWhenTheyWereAdded()
    {
        var added = await _store.SaveAsync(Omar() with { Aliases = ["Bro"], Relationships = ["Brother"], Identifiers = [new(PersonIdentifierKind.Phone, PhoneSecret)] });
        _database.Time.Advance(TimeSpan.FromHours(2));

        var changed = await _store.SaveAsync(added with
        {
            DisplayName = "Omar H.",
            Aliases = ["Omi"],
            Relationships = ["Cousin"],
            Identifiers = [new(PersonIdentifierKind.Email, "omar@example.com")],
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        var read = Assert.Single(await _store.ListAsync());
        AssertSame(changed, read);
        Assert.Equal(added.CreatedAt, read.CreatedAt);
        Assert.Equal(added.CreatedAt.AddHours(2), read.UpdatedAt);
        Assert.Equal(["Omi"], read.Aliases);
        Assert.Equal(["Cousin"], read.Relationships);
        Assert.Equal([new PersonIdentifier(PersonIdentifierKind.Email, "omar@example.com")], read.Identifiers);
        using var connection = _database.Open();
        Assert.Equal(1, connection.Count("person_aliases"));
        Assert.Equal(1, connection.Count("person_relationships"));
        Assert.Equal(1, connection.Count("person_identifiers"));
    }

    [Fact]
    public async Task RemovingAPersonRemovesWhatBelongsToThem_AndOnlyThem()
    {
        var omar = await _store.SaveAsync(Omar() with { Aliases = ["Bro"], Relationships = ["Brother"], Identifiers = [new(PersonIdentifierKind.Phone, PhoneSecret)] });
        var sara = await _store.SaveAsync(Person.Create("Sara", _database.Time.GetUtcNow()) with { Aliases = ["Sis"], Relationships = ["Sister"] });
        var changes = 0;
        _store.Changed += (_, _) => changes++;

        Assert.True(await _store.DeleteAsync(omar.Id));
        Assert.False(await _store.DeleteAsync(omar.Id));

        Assert.Equal(sara.Id, Assert.Single(await _store.ListAsync()).Id);
        Assert.Null(await _store.GetAsync(omar.Id));
        Assert.Equal(1, changes);
        using var connection = _database.Open();
        Assert.Equal(1, connection.Count("person_aliases"));
        Assert.Equal(1, connection.Count("person_relationships"));
        Assert.Equal(0, connection.Count("person_identifiers"));
    }

    [Fact]
    public async Task EverySaveSaysThatThePeopleChanged()
    {
        var changes = 0;
        _store.Changed += (_, _) => changes++;

        var person = await _store.SaveAsync(Omar());
        await _store.SaveAsync(person with { DisplayName = "Omar" });

        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task ThePeopleAreStillThereAfterARestart()
    {
        var saved = await _store.SaveAsync(Omar() with { Aliases = ["Bro"], Relationships = ["Brother"], Identifiers = [new(PersonIdentifierKind.Phone, PhoneSecret, "Signal")] });

        var restarted = CreateStore();

        AssertSame(saved, Assert.Single(await restarted.ListAsync()));
    }

    [Fact]
    public async Task APersonWhoCannotBeKeptAsWrittenIsNotKept_AndTheMessageRepeatsNothingTheUserTyped()
    {
        var blank = await Assert.ThrowsAsync<PersonValidationException>(() => _store.SaveAsync(Omar() with { DisplayName = "   " }));
        var phone = await Assert.ThrowsAsync<PersonValidationException>(
            () => _store.SaveAsync(Omar() with { Identifiers = [new(PersonIdentifierKind.Phone, "call me " + PhoneSecret)] }));
        var email = await Assert.ThrowsAsync<PersonValidationException>(
            () => _store.SaveAsync(Omar() with { Identifiers = [new(PersonIdentifierKind.Email, "not an address")] }));

        Assert.Empty(await _store.ListAsync());
        Assert.DoesNotContain(PhoneSecret, phone.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not an address", email.Message, StringComparison.Ordinal);
        Assert.Contains("name", blank.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThereIsALimitToHowManyPeopleAreKept_ButAKnownPersonCanStillBeChanged()
    {
        await _store.ListAsync();
        using (var connection = _database.Open())
        {
            for (var index = 0; index < PersonRules.MaxPeople; index++)
            {
                connection.Execute(
                    "INSERT INTO people (id, display_name, created_at, updated_at) VALUES ($id, $name, $at, $at)",
                    ("$id", Guid.NewGuid().ToString("D")),
                    ("$name", "Person " + index),
                    ("$at", SqlHelpers.Timestamp));
            }
        }

        await Assert.ThrowsAsync<PersonValidationException>(() => _store.SaveAsync(Omar()));

        var existing = (await _store.ListAsync())[0];
        await _store.SaveAsync(existing with { Aliases = ["Still works"] });
        Assert.Equal(PersonRules.MaxPeople, (await _store.ListAsync()).Count);
    }

    [Fact]
    public async Task AnIdentifierOfAKindANewerBuildWroteIsLeftOut_NotFailedOn()
    {
        var saved = await _store.SaveAsync(Omar() with { Identifiers = [new(PersonIdentifierKind.Email, "omar@example.com")] });
        using (var connection = _database.Open())
        {
            connection.Execute(
                "INSERT INTO person_identifiers (person_id, kind, service, value) VALUES ($id, 'Carrier pigeon', '', 'coop 4')",
                ("$id", DatabaseIds.ToText(saved.Id)));
        }

        var read = Assert.Single(await _store.ListAsync());
        var single = await _store.GetAsync(saved.Id);

        Assert.Equal([new PersonIdentifier(PersonIdentifierKind.Email, "omar@example.com")], read.Identifiers);
        Assert.Equal(read.Identifiers, single!.Identifiers);
    }

    [Fact]
    public async Task ADatabaseFailureIsAPersonStoreException_ThatHoldsNoContent_AndIsLoggedByItsTypeAlone()
    {
        await _store.ListAsync();
        using (var connection = _database.Open())
        {
            connection.Execute($"CREATE TRIGGER refuse BEFORE INSERT ON people BEGIN SELECT RAISE(ABORT, '{NameSecret}'); END");
        }

        var failure = await Assert.ThrowsAsync<PersonStoreException>(() => _store.SaveAsync(Person.Create(NameSecret, _database.Time.GetUtcNow())));

        Assert.DoesNotContain(NameSecret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(NameSecret, _logs.AllText.Replace(failure.InnerException!.Message, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Empty(await _store.ListAsync());
    }

    [Fact]
    public async Task ADatabaseFromANewerBuildIsAPersonStoreException_AndIsLeftAsItIs()
    {
        var latest = _database.CreateInitializer().Initialize().CurrentVersion;
        using (var connection = _database.Open())
        {
            new SchemaVersionRepository().Record(connection, latest + 1, "from_the_future", DateTimeOffset.UnixEpoch);
        }

        await Assert.ThrowsAsync<PersonStoreException>(() => CreateStore().ListAsync());
        using var check = _database.Open();
        Assert.Equal(latest + 1, check.ExecuteScalar<int>("SELECT MAX(version) FROM schema_version"));
    }

    [Fact]
    public async Task NoOperationLogsANameAnAliasARelationshipOrAnAddress()
    {
        var person = await _store.SaveAsync(Person.Create(NameSecret, _database.Time.GetUtcNow()) with
        {
            Aliases = [AliasSecret],
            Relationships = [RelationshipSecret],
            Identifiers = [new(PersonIdentifierKind.Phone, PhoneSecret)],
        });
        await _store.ListAsync();
        await _store.GetAsync(person.Id);
        await _store.SaveAsync(person with { Aliases = [AliasSecret + " again"] });
        await _store.DeleteAsync(person.Id);

        var text = _logs.AllText;
        Assert.Contains("A person was saved", text, StringComparison.Ordinal);
        Assert.Contains("A person was removed", text, StringComparison.Ordinal);
        Assert.DoesNotContain(NameSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AliasSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(RelationshipSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PhoneDigits, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_database.Root, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APersonPrintsWithoutTheirPrivateDetails()
    {
        var person = Person.Create(NameSecret, DateTimeOffset.UnixEpoch) with
        {
            Aliases = [AliasSecret],
            Identifiers = [new(PersonIdentifierKind.Phone, PhoneSecret)],
        };

        var text = person + " " + person.Identifiers[0];

        Assert.DoesNotContain(NameSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AliasSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PhoneDigits, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpgradingADatabaseThatHadPeopleKeepsThemAndGivesThemSomewhereToKeepTheNewDetails()
    {
        var before = new MigrationCatalog(MigrationCatalog.LoadDefault().Migrations.Take(3));
        using (var connection = _database.Open())
        {
            _database.CreateMigrator(before).Migrate(connection);
            connection.Execute("INSERT INTO people (id, display_name, created_at, updated_at) VALUES ('0f8fad5b-d9cb-469f-a165-70867728950e', 'Sara', $at, $at)", ("$at", SqlHelpers.Timestamp));
            connection.Execute("INSERT INTO person_relationships (person_id, relationship) VALUES ('0f8fad5b-d9cb-469f-a165-70867728950e', 'Sister')");
            Assert.DoesNotContain("person_aliases", connection.Tables());
        }

        var sara = Assert.Single(await CreateStore().ListAsync());

        Assert.Equal("Sara", sara.DisplayName);
        Assert.Equal(["Sister"], sara.Relationships);
        Assert.Empty(sara.Aliases);
        using var after = _database.Open();
        Assert.Contains("person_aliases", after.Tables());
        Assert.Contains("person_identifiers", after.Tables());
        Assert.Equal(["person_id", "kind", "service", "value"], after.Columns("person_identifiers"));
    }

    [Fact]
    public async Task SavesThatRaceEachOtherAreAllKept()
    {
        await _store.ListAsync();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => _store.SaveAsync(Person.Create("Person " + index, _database.Time.GetUtcNow()) with { Aliases = ["Alias " + index] })));

        var people = await _store.ListAsync();
        Assert.Equal(12, people.Count);
        Assert.All(people, person => Assert.Single(person.Aliases));
    }

    // A person holds lists, which a record compares by reference, so the parts are compared one by one.
    private static void AssertSame(Person expected, Person actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.Equal(expected.Aliases, actual.Aliases);
        Assert.Equal(expected.Relationships, actual.Relationships);
        Assert.Equal(expected.Identifiers, actual.Identifiers);
    }

    private Person Omar() => Person.Create("Omar Hassan", _database.Time.GetUtcNow());

    private SqlitePersonStore CreateStore() =>
        new(
            _database.Connections,
            _database.CreateInitializer(),
            new PersonRepository(),
            _database.Time,
            _logs.CreateFactory().CreateLogger<SqlitePersonStore>());
}
