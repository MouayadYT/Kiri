using Assistant.Core.Contracts;
using Assistant.Core.Storage;
using Assistant.Tools.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Steps 105-106: the pieces are wired the way the app builds them.</summary>
public sealed class IntegrationWiringTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new AppPaths(Path.Combine(Path.GetTempPath(), "assistant-wiring-" + Guid.NewGuid().ToString("N"))));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsService>(new FixedSettings());
        services.AddSingleton<IPermissionPolicy>(new FakePermissions(true));
        services.AddSingleton<IModelService>(new FakeModels());
        more?.Invoke(services);
        services.AddConnectedApps();
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task TheOrchestratorCanBeGivenTheHandler()
    {
        await using var provider = Build();

        var handler = provider.GetRequiredService<IConnectedAppRequestHandler>();

        Assert.IsType<ConnectedAppRequestHandler>(handler);
        Assert.Same(handler, provider.GetRequiredService<IConnectedAppRequestHandler>());
    }

    [Fact]
    public async Task TheFinderAsksTheRegistryAndGitHubFirstAndThePackageRegistriesOnlyWhenNeeded()
    {
        await using var provider = Build();

        var sources = provider.GetServices<IIntegrationDiscoverySource>().ToList();

        Assert.Equal(["mcp-registry", "github", "npm", "pypi"], sources.Select(source => source.Id));
        Assert.Equal([DiscoveryStage.First, DiscoveryStage.First, DiscoveryStage.WhenNeeded, DiscoveryStage.WhenNeeded], sources.Select(source => source.Stage));
    }

    [Fact]
    public async Task TheResolverLooksAtOtherProgramsFilesOnlyThroughTheOneSource()
    {
        await using var provider = Build();

        Assert.IsType<LocalMcpConfigSource>(Assert.Single(provider.GetServices<IAvailableIntegrationSource>()));
    }

    [Fact]
    public async Task RegisteringAgainDoesNotMakeEverySourceAppearTwice()
    {
        await using var provider = Build(services =>
        {
            services.AddIntegrationResolution();
            services.AddIntegrationFinder();
            services.AddIntegrationFinder();
        });

        Assert.Equal(4, provider.GetServices<IIntegrationDiscoverySource>().Count());
        Assert.Single(provider.GetServices<IAvailableIntegrationSource>());
    }

    [Fact]
    public async Task TheCacheLivesInTheAppsCacheFolder()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "assistant-wiring-" + Guid.NewGuid().ToString("N")));
        await using var provider = Build(services => services.AddSingleton(paths));
        var need = DiscoveryFixtures.Todoist;
        var finder = provider.GetRequiredService<IIntegrationFinder>();

        // With Local Only on (the default) nothing is searched and nothing is written.
        var result = await finder.FindAsync(need);

        Assert.Equal(DiscoveryStatus.Blocked, result.Status);
        Assert.False(File.Exists(Path.Combine(paths.CacheDirectory, "integration-discovery.json")));
    }

    [Fact]
    public async Task EverythingResolvesWithTheDefaultSettings()
    {
        await using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<IIntegrationResolver>());
        Assert.NotNull(provider.GetRequiredService<IIntegrationFinder>());
        Assert.NotNull(provider.GetRequiredService<ICandidateAssessor>());
        Assert.NotNull(provider.GetRequiredService<IIntegrationRequestReader>());
    }

    [Fact]
    public void TheHostsTheFinderMayReachAreTheFixedFour()
    {
        Assert.Equal(
            ["api.github.com", "pypi.org", "registry.modelcontextprotocol.io", "registry.npmjs.org"],
            DiscoveryHttp.AllowedHosts.Order(StringComparer.Ordinal));
    }
}
