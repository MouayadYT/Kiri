namespace Assistant.Data.Migrations;

/// <summary>
/// The ordered list of every migration, from an empty database to the newest schema.
/// </summary>
/// <remarks>
/// A migration that has run on anyone's database is never edited or removed: a change to the schema is a new
/// migration with the next version number. Migrations only go forward; there is no way back except restoring a backup.
/// </remarks>
public sealed class MigrationCatalog
{
    private readonly Migration[] _migrations;

    /// <summary>Creates a catalog from <paramref name="migrations"/>, which must be versions 1, 2, 3… in order.</summary>
    /// <exception cref="ArgumentException">
    /// There are no migrations, a version is out of order, repeated or skipped, or a name or script is blank.
    /// </exception>
    public MigrationCatalog(IEnumerable<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        _migrations = [.. migrations];
        if (_migrations.Length == 0)
        {
            throw new ArgumentException("A catalog needs at least one migration.", nameof(migrations));
        }

        for (var index = 0; index < _migrations.Length; index++)
        {
            var migration = _migrations[index];
            var expected = index + 1;
            if (migration.Version != expected)
            {
                throw new ArgumentException(
                    $"Migration versions must be 1, 2, 3… in order, but position {expected} holds version {migration.Version}.",
                    nameof(migrations));
            }

            if (string.IsNullOrWhiteSpace(migration.Name) || string.IsNullOrWhiteSpace(migration.Sql))
            {
                throw new ArgumentException($"Migration {migration.Version} needs a name and a script.", nameof(migrations));
            }
        }
    }

    /// <summary>Every migration, lowest version first.</summary>
    public IReadOnlyList<Migration> Migrations => _migrations;

    /// <summary>The schema version of a database that has every migration applied.</summary>
    public int LatestVersion => _migrations[^1].Version;

    /// <summary>The migrations a database at <paramref name="version"/> still needs, in the order to apply them.</summary>
    public IEnumerable<Migration> After(int version) => _migrations.Where(migration => migration.Version > version);

    /// <summary>The app's own migrations: the scripts embedded in this assembly.</summary>
    public static MigrationCatalog LoadDefault() => new(MigrationScripts.Load(typeof(MigrationCatalog).Assembly));
}
