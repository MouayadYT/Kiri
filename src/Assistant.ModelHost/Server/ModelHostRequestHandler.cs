using Assistant.Core.ModelHosting;
using Assistant.ModelHost.Generation;
using Assistant.ModelHost.Models;
using Assistant.ModelHost.Runtime;

namespace Assistant.ModelHost.Server;

/// <summary>
/// Answers the owner's requests. It answers the ping, <see cref="HealthRequest"/>, with the bundled runtime's and the
/// model's state, loads and unloads models through the <see cref="IModelController"/>, and has the
/// <see cref="ITextGenerator"/> stream the answers to generation requests.
/// </summary>
internal sealed class ModelHostRequestHandler(
    TimeProvider timeProvider,
    IModelRuntimeLocator runtimeLocator,
    IModelController models,
    ITextGenerator generator) : IModelHostRequestHandler
{
    private static readonly Version HostVersion =
        typeof(ModelHostRequestHandler).Assembly.GetName().Version ?? new Version(0, 0);

    private readonly long _started = timeProvider.GetTimestamp();

    public async Task HandleAsync(ModelHostRequest request, IModelHostReplies replies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(replies);

        // A generation sends its replies as they come, its last one included.
        if (request is GenerationRequest generation)
        {
            await generator.GenerateAsync(generation, replies, cancellationToken).ConfigureAwait(false);
            return;
        }

        ModelHostReply reply;
        try
        {
            reply = request switch
            {
                HealthRequest => CreateHealthReport(),
                LoadModelRequest load => new ModelLoaded(await models.LoadAsync(load, cancellationToken).ConfigureAwait(false)),
                UnloadModelRequest => new ModelUnloaded
                {
                    ModelId = await models.UnloadAsync(cancellationToken).ConfigureAwait(false),
                },
                _ => new ModelHostError(ModelHostErrorCode.NotImplemented),
            };
        }
        catch (ModelRequestException failed)
        {
            reply = new ModelHostError(failed.Code);
        }

        await replies.SendAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    private HealthReport CreateHealthReport()
    {
        var status = models.Current;
        return new HealthReport(HostVersion, Environment.ProcessId, timeProvider.GetElapsedTime(_started))
        {
            WorkingSetBytes = Environment.WorkingSet,
            LoadedModelId = models.Model?.Id,
            ModelStatus = status.Status,
            Runtime = runtimeLocator.Locate().State,
        };
    }
}
