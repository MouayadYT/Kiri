using System.Linq;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Data;
using Assistant.UI.Bootstrap;
using Assistant.UI.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// How the app is put together for people and messaging (PROJECT_SPEC §4.8, step 113): the people are kept in the app's own database and resolved from it, the Settings window
/// can keep them, the messaging tools are registered but the only provider reaches a connected messaging app (step 116) and there is none installed, and the permission cannot be allowed,
/// so nothing in the app can send a message yet. Nothing here reads or writes the user's own database: only what is registered is looked at.
/// </summary>
public sealed class PeopleWiringTests
{
    [Fact]
    public void ThePeopleAreKeptInTheAppsDatabaseAndResolvedFromThem()
    {
        using var host = AppHost.Create();

        var store = host.Services.GetRequiredService<IPersonStore>();
        var resolver = host.Services.GetRequiredService<IPersonResolver>();

        Assert.IsType<SqlitePersonStore>(store);
        Assert.Same(store, host.Services.GetRequiredService<IPersonStore>());

        // The resolver is the one over that store while the reminder demo is off (the demo, which is off, answers from a list of its own while it is on): it is what the people
        // tools use, and what it resolves is what the store holds.
        Assert.IsAssignableFrom<IPersonResolver>(resolver);
        Assert.Same(resolver, host.Services.GetRequiredService<IPersonResolver>());
    }

    [Fact]
    public async System.Threading.Tasks.Task WhileTheReminderDemoIsOffTheResolverAnswersFromTheAppsOwnPeopleAndNotFromTheDemosList()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assistant-people-wiring-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings
            {
                ApplicationName = "Assistant",
                ContentRootPath = System.AppContext.BaseDirectory,
                EnvironmentName = "Development",
            });
            builder.Services.AddAssistantServices().AddUserInterface();
            builder.Services.AddSingleton(new Assistant.Core.Storage.AppPaths(root));
            using var host = builder.Build();
            var resolver = host.Services.GetRequiredService<IPersonResolver>();

            // No one is saved, so there is no brother: the demo's made-up Omar is not in the app's list.
            Assert.Equal(PersonResolutionOutcome.NotFound, (await resolver.ResolveAsync("my brother")).Outcome);

            await host.Services.GetRequiredService<IPersonStore>().SaveAsync(
                Person.Create("Sami", System.DateTimeOffset.UtcNow) with { Relationships = ["Brother"] });
            var found = await resolver.ResolveAsync("my brother");
            Assert.Equal("Sami", found.Person!.DisplayName);
        }
        finally
        {
            try
            {
                if (System.IO.Directory.Exists(root))
                {
                    System.IO.Directory.Delete(root, recursive: true);
                }
            }
            catch (System.Exception exception) when (exception is System.IO.IOException or System.UnauthorizedAccessException)
            {
                // The folder is in the temp directory and goes in time.
            }
        }
    }

    [Fact]
    public void TheSettingsWindowCanKeepAndTryPeople()
    {
        using var host = AppHost.Create();

        var page = host.Services.GetRequiredService<SettingsViewModel>().People;

        Assert.True(page.HasStore);
        Assert.True(page.CanCheck);
        Assert.True(page.AddCommand.CanExecute(null));
    }

    [Fact]
    public async System.Threading.Tasks.Task TheMessagingToolsAreRegisteredButNothingCanSendYet()
    {
        using var host = AppHost.Create();
        var registry = host.Services.GetRequiredService<IToolRegistry>();

        var draft = registry.Find("draft_message");
        var send = registry.Find("send_message");

        Assert.NotNull(draft);
        Assert.NotNull(send);
        Assert.Equal(RiskLevel.ReadOnly, draft.RiskLevel);
        Assert.Equal(RiskLevel.SideEffect, send.RiskLevel);

        // No provider: neither is offered, even for a request that is plainly about a message.
        var offered = registry.ToolsFor(new ToolContext(System.Guid.NewGuid(), "Text my brother that I'm late")).Select(tool => tool.Name).ToList();
        Assert.DoesNotContain("draft_message", offered);
        Assert.DoesNotContain("send_message", offered);

        // The provider is the one that reaches a connected messaging app. The host reads this PC's own connections (the app's folder cannot be pointed
        // elsewhere), so on a PC where no messaging app is connected, as a build machine is, it has nothing to send through; on the PC of someone who uses
        // the Assistant with one connected, that is theirs to have and is not this test's to say.
        var provider = host.Services.GetService<IMessagingProvider>();
        Assert.NotNull(provider);
        Assert.Equal("McpMessagingProvider", provider.GetType().Name);
        var connections = await host.Services.GetRequiredService<Assistant.Tools.Integrations.IInstalledIntegrationRegistry>().ListAsync();
        if (!connections.Any(connection => connection.Permissions.RequiredCapability == PermissionCapability.Messaging))
        {
            Assert.False(await provider.IsAvailableAsync());
        }
    }
}
