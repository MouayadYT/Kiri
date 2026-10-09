namespace Assistant.Data.Migrations;

/// <summary>One step from a schema version to the next.</summary>
/// <param name="Version">
/// The schema version the database is at once the step has run. Versions start at 1 and each is one more than the last.
/// </param>
/// <param name="Name">A short snake_case name for the step, recorded next to its version.</param>
/// <param name="Sql">
/// The SQL that makes the change. It runs inside the migrator's transaction, so it must not begin or end one and must not
/// change <c>PRAGMA foreign_keys</c>; the migrator turns foreign keys off around the transaction, so a table can be
/// rebuilt (create new, copy, drop old, rename) without the drop cascading into the tables that refer to it.
/// </param>
public sealed record Migration(int Version, string Name, string Sql);

/// <summary>What <see cref="IDatabaseMigrator.Migrate"/> did.</summary>
/// <param name="PreviousVersion">The schema version before: 0 for a database that had none.</param>
/// <param name="CurrentVersion">The schema version now.</param>
/// <param name="BackupPath">The backup taken before the upgrade, or <see langword="null"/> when none was needed.</param>
public sealed record MigrationResult(int PreviousVersion, int CurrentVersion, string? BackupPath)
{
    /// <summary>Whether any migration was applied.</summary>
    public bool Upgraded => CurrentVersion > PreviousVersion;
}
