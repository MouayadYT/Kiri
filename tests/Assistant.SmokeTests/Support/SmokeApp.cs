using Assistant.Core.Contracts;
using Assistant.Core.Ipc;
using Assistant.Core.ModelHosting;
using Assistant.Core.Settings;
using Assistant.Core.Startup;
using Assistant.Core.Storage;
using Assistant.UI.Bootstrap;
using Assistant.UI.Browser;
using Assistant.UI.Explorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.SmokeTests.Support;

/// <summary>
/// The app as the bootstrapper puts it together (<c>AddAssistantServices</c> and <c>AddUserInterface</c>, validated and started as a host), over a data
/// folder of the check's own. Three things are not the app's: its pipe has a name of its own (so a running Assistant is never answered by a check), and
/// the three entries it would write into the user's registry (the sign-in entry, File Explorer's menu, the browsers' host) do nothing. Everything else is
/// the real registration, unless a check replaces one edge of it.
/// </summary>
internal sealed class SmokeApp : IAsyncDisposable
{
    private readonly IHost _host;

    private SmokeApp(IHost host, ScratchFolder scratch, string pipeName, bool firstRun, CapturedLogs logs)
    {
        _host = host;
        Logs = logs;
        Scratch = scratch;
        PipeName = pipeName;
        FirstRun = firstRun;
    }

    public ScratchFolder Scratch { get; }

    /// <summary>The app pipe the Explorer and browser handoffs arrive on in this check.</summary>
    public string PipeName { get; }

    /// <summary>Whether the data folder did not exist before this start, which is what a first run is to the app.</summary>
    public bool FirstRun { get; }

    /// <summary>What the app has logged so far.</summary>
    public CapturedLogs Logs { get; }

    public IServiceProvider Services => _host.Services;

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public AppPaths Paths => Get<AppPaths>();

    public Task<AppSettings> ChangeSettingsAsync(Func<AppSettings, AppSettings> change) => Get<ISettingsService>().UpdateAsync(change);

    /// <summary>Builds the host over <paramref name="scratch"/> and starts it, as the bootstrapper does before the application runs.</summary>
    public static async Task<SmokeApp> StartAsync(ScratchFolder scratch, Action<IServiceCollection>? replace = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development",
        });

        // The container is validated as the app's is: a registration that cannot be built fails here, at once.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        builder.Services.AddAssistantServices().AddUserInterface();
        var logs = new CapturedLogs();
        builder.Logging.SetMinimumLevel(LogLevel.Debug).AddProvider(logs);

        var pipe = LocalPipe.CreateUniqueName("Assistant.Smoke");
        builder.Services.AddSingleton(new AppPaths(scratch.File("data")));
        builder.Services.AddSingleton(new ExplorerIntegrationOptions(pipe));
        builder.Services.AddSingleton<IExplorerMenuInstaller, NoRegistry>();
        builder.Services.AddSingleton<IBrowserBridgeInstaller, NoRegistry>();
        builder.Services.AddSingleton<ILaunchAtLogin, NoRegistry>();
        replace?.Invoke(builder.Services);

        var host = builder.Build();
        var firstRun = host.Services.GetRequiredService<AppPaths>().EnsureDirectoriesExist();
        await host.StartAsync();
        return new SmokeApp(host, scratch, pipe, firstRun, logs);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(10));
        _host.Dispose();
    }

    /// <summary>The registry the app would write to, left alone: each answers that it worked and says nothing else.</summary>
    private sealed class NoRegistry : IExplorerMenuInstaller, IBrowserBridgeInstaller, ILaunchAtLogin
    {
        Task<bool> IExplorerMenuInstaller.InstallAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        Task<bool> IExplorerMenuInstaller.RemoveAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        Task<bool> IBrowserBridgeInstaller.InstallAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        Task<bool> IBrowserBridgeInstaller.RemoveAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public LaunchAtLoginState GetState() => LaunchAtLoginState.Off;

        public Task<bool> EnableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> DisableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
