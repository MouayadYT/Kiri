using System.Windows;
using Assistant.Core.ModelHosting;
using Assistant.Core.Startup;
using Assistant.UI.Bootstrap;
using Assistant.UI.Tray;
using Assistant.Windows.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

// How the app puts the tray menu, Pause Local AI and starting with Windows together (PROJECT_SPEC §4.9, step 121).
public sealed partial class PromptInputControlTests
{
    [Fact]
    public void TheAppBuildsItsTrayMenuOverTheRealWindowAndTheOneModelLifecycle() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        using var host = AppHost.Create();
        try
        {
            // Pause Local AI is the model lifecycle itself, so pausing it stops the loads every question goes through.
            var pause = host.Services.GetRequiredService<ILocalAiPause>();
            Assert.Same(host.Services.GetRequiredService<ModelLifecycle>(), pause);
            Assert.Same(host.Services.GetRequiredService<IModelLifecycle>(), pause);

            Assert.IsType<RegistryLaunchAtLogin>(host.Services.GetRequiredService<ILaunchAtLogin>());
            Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is LaunchAtLoginRefresh);

            using var tray = host.Services.GetRequiredService<TrayController>();
            Assert.Same(tray, host.Services.GetRequiredService<TrayController>());
            Assert.Equal(
                ["Open Assistant", "New Conversation", "", "Settings", "Pause Local AI", "", "Exit"],
                tray.BuildMenu().Select(item => item.Text));
        }
        finally
        {
            app.Resources.MergedDictionaries.Clear();
        }
    });
}
