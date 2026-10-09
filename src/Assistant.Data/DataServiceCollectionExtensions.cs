using Assistant.Core.Audit;
using Assistant.Core.Contracts;
using Assistant.Core.People;
using Assistant.Core.Storage;
using Assistant.Data.Migrations;
using Assistant.Data.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Data;

/// <summary>Registers the local database and the conversation history built on it.</summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database (its connections, its migrations and their backups), <see cref="IConversationService"/>
    /// over it, and the people the user told the Assistant about (<see cref="IPersonStore"/>, <see cref="IPersonResolver"/>). It needs <see cref="AppPaths"/>, <see cref="TimeProvider"/>, <see cref="ISettingsService"/> and logging to
    /// be registered too. Nothing touches the disk until the history is first used, or
    /// <see cref="SqliteConversationService.InitializeAsync"/> is called.
    /// </summary>
    public static IServiceCollection AddAssistantData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider => DatabaseOptions.For(provider.GetRequiredService<AppPaths>()));
        services.AddSingleton<IDatabaseConnectionFactory, DatabaseConnectionFactory>();
        services.AddSingleton<ISchemaVersionRepository, SchemaVersionRepository>();
        services.AddSingleton(_ => MigrationCatalog.LoadDefault());
        services.AddSingleton<IDatabaseBackup, DatabaseBackup>();
        services.AddSingleton<IDatabaseMigrator, DatabaseMigrator>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();

        services.AddSingleton<IConversationRepository, ConversationRepository>();
        services.AddSingleton<IMessageRepository, MessageRepository>();
        services.AddSingleton<IConversationSearchRepository, ConversationSearchRepository>();
        services.AddSingleton<SqliteConversationService>();
        services.AddSingleton<IConversationService>(provider => provider.GetRequiredService<SqliteConversationService>());

        // The people the user told the Assistant about (step 112), and what resolves "my brother" from them: both local, in the same database.
        services.AddSingleton<IPersonRepository, PersonRepository>();
        services.AddSingleton<IPersonStore, SqlitePersonStore>();
        services.AddSingleton<IPersonResolver, PersonResolver>();

        // What the Assistant did on the user's behalf (step 117): the runs of the agent with their steps, and the integrations looked for, offered, installed, updated and
        // removed. Names, codes and fixed words only, in the same database.
        services.AddSingleton<IAuditRepository, AuditRepository>();
        services.AddSingleton<IAuditStore, SqliteAuditStore>();
        return services;
    }
}
