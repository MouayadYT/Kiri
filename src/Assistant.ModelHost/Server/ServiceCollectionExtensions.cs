using Assistant.Core.Storage;
using Assistant.ModelHost.Generation;
using Assistant.ModelHost.Models;
using Assistant.ModelHost.Processes;
using Assistant.ModelHost.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Assistant.ModelHost.Server;

/// <summary>Service registrations of the model host process.</summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the server that serves the owner on the pipe <paramref name="options"/> names, the bundled runtime's
    /// locator, the manager of the engine process, and the generator that streams its answers.
    /// </summary>
    public static IServiceCollection AddModelHost(this IServiceCollection services, ModelHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => AppPaths.ForCurrentUser());
        services.AddSingleton(new ModelRuntimeOptions(ModelRuntimeLayout.DefaultDirectory));
        services.AddSingleton<IModelRuntimeLocator, BundledRuntimeLocator>();
        services.AddSingleton(provider => new ModelProcessOptions(provider.GetRequiredService<AppPaths>().SocketsDirectory));
        services.AddSingleton<IEngineDeviceProbe, LlamaServerDeviceProbe>();
        services.AddSingleton<IModelProcessManager, ModelProcessManager>();
        services.AddSingleton<ModelController>();
        services.AddSingleton<IModelController>(provider => provider.GetRequiredService<ModelController>());
        services.AddSingleton<IModelStatusSource>(provider => provider.GetRequiredService<ModelController>());
        services.AddSingleton<IChatEngine, LlamaServerChatEngine>();
        services.AddSingleton<ITextGenerator, TextGenerator>();
        services.AddSingleton<IModelHostRequestHandler, ModelHostRequestHandler>();
        services.AddSingleton<ModelHostSession>();
        services.AddSingleton<ModelHostServer>();
        services.AddSingleton<ModelHostService>();
        services.AddHostedService(provider => provider.GetRequiredService<ModelHostService>());
        return services;
    }
}
