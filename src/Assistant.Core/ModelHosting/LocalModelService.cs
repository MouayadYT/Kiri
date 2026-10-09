using System.Runtime.CompilerServices;
using Assistant.Core.Assets;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ModelProfiles;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// The app's <see cref="IModelService"/> (PROJECT_SPEC §5.4, §5.6): generates with the local model in the model host,
/// through the <see cref="IModelLifecycle"/>, and streams the host's replies as <see cref="AssistantResponseChunk"/>s
/// while they arrive.
/// </summary>
/// <remarks>
/// <para>
/// The model is the one loaded, or else the one the settings name, which is loaded on its first use: the first
/// generation starts the host and loads it. The settings name it by a file the user picked
/// (<see cref="ModelSettings.ModelFilePath"/> and the files that go with it), used as it is, or else by a model profile
/// (<see cref="ModelSettings.ProfileId"/>, the default profile when none is set) whose files are installed, run with the
/// hardware preset that suits the machine. Loading is done one caller at a time, so two first questions load it once.
/// </para>
/// <para>
/// Leaving a stream early, or cancelling it, stops the generation in the host. It logs events only, never a prompt,
/// an answer or a path (PROJECT_SPEC §3.3); <c>LoggingModelService</c> logs the outcomes.
/// </para>
/// <para>
/// The context window the model is loaded with follows the conversation being answered (<see cref="IModelContextDemand"/>, <see cref="ContextWindowPlan"/>):
/// the ordinary window, and the larger one while the conversation carries files. A model that is loaded with the other window is loaded again with the one
/// wanted when it is next asked to generate, which takes as long as loading it did; a window the user set themselves is never changed.
/// </para>
/// </remarks>
public sealed partial class LocalModelService(
    IModelLifecycle lifecycle,
    ISettingsService settings,
    IModelProfileResolver profiles,
    ILogger<LocalModelService> logger,
    IPackagedAssets? assets = null,
    IHardwareInfoProvider? hardware = null) : IModelService, IModelPreloader, IModelContextDemand
{
    /// <summary>The notice that follows an answer the model had to stop because it reached its length limit.</summary>
    public const string OutputLimitNotice = "The answer was cut short because it reached the model's length limit.";

    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // Whether the conversation being answered carries files, as the turn that answers it said.
    private volatile bool _documents;

    // The model this service loaded last, and the window it asked for it: the engine may give a model less than was asked (one trained on a shorter
    // context), so what was asked is what is compared with what is wanted, and such a model is not loaded over and over.
    private ModelInfo? _loaded;
    private int _loadedWindow;
    private ModelFiles? _loadedFiles;

    // Reading pictures only when it is needed (ModelSettings.VisionOnDemand): the questions with a picture that are being answered now, and the wait
    // that puts the projector away again once none has been asked for a while.
    private int _picturesBeingRead;
    private int _answering;
    private CancellationTokenSource? _visionRest;
    private volatile bool _projectorOnDemand;
    private volatile bool _resting;

    /// <summary>
    /// How long after the last question about a picture the model is loaded again without its projector, when the user chose to read pictures only
    /// when it is needed.
    /// </summary>
    public TimeSpan VisionRest { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The model's reload without its projector that is waiting or running, or a finished task: for whoever must know it is over.</summary>
    public Task VisionResting { get; private set; } = Task.CompletedTask;

    /// <inheritdoc/>
    public bool Documents => _documents;

    /// <inheritdoc/>
    public bool Use(bool documents)
    {
        if (_documents == documents)
        {
            return false;
        }

        _documents = documents;
        LogWindowDemand(logger, documents);
        return true;
    }

    /// <summary>
    /// Gets the model used for generation: the loaded one, or else the one the settings name, which is not loaded by
    /// this. Returns <see langword="null"/> when no model is set up.
    /// </summary>
    /// <remarks>
    /// Until a model has loaded, its context length is the one it will be loaded with; the engine may give a model
    /// trained on a shorter context less, which the model has once it is loaded.
    /// </remarks>
    public async Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default)
    {
        // The loaded model answers, unless the conversation wants the other window: then it is the model as it will be once it is loaded again,
        // so that what is asked of it is fitted to the window it will have.
        var choice = await FindModelAsync(cancellationToken).ConfigureAwait(false);
        if (lifecycle.Model is { } loaded && (choice is null || HasConfigurationOf(loaded, choice.Files)))
        {
            // A model that is loaded without the projector it has (pictures are read only when it is needed) reads pictures all the same: it is
            // given them, and loaded with the projector then.
            return choice is { Files.ProjectorPath: not null } && !loaded.SupportsVision ? loaded with { SupportsVision = true } : loaded;
        }

        if (choice is null)
        {
            return null;
        }

        var files = choice.Files;
        return new ModelInfo(files.DeriveModelId(), files.ContextLength ?? ModelFiles.DefaultContextLength)
        {
            SupportsVision = files.ProjectorPath is not null,
            SupportsConstrainedOutput = true,

            // The engine applies the model's own chat template, which carries the tools to a model that was made for them.
            SupportsToolCalling = true,
        };
    }

    /// <summary>
    /// Streams the model's answer to <paramref name="request"/>, loading the model first when it is not loaded. Text
    /// arrives as <see cref="AssistantResponseChunkType.TextDelta"/>s and requested tool calls as
    /// <see cref="AssistantResponseChunkType.ToolCall"/>s. An answer that reached its length limit ends with an
    /// <see cref="AssistantResponseChunkType.Notice"/> that says so.
    /// </summary>
    /// <exception cref="ModelHostException">
    /// No model is set up (<see cref="ModelHostErrorCode.ModelNotFound"/>), the model could not be loaded (the model's
    /// status says why), the host could not be reached, or the generation failed, perhaps after some text.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pictures = request.Images.Count > 0;
        Interlocked.Increment(ref _answering);
        if (pictures)
        {
            // While a picture is being read the projector stays, and the wait that would put it away starts over when the answer is done.
            Interlocked.Increment(ref _picturesBeingRead);
            Interlocked.Exchange(ref _visionRest, null)?.Cancel();
        }

        try
        {
            var model = await EnsureLoadedAsync(cancellationToken, pictures).ConfigureAwait(false);
            await foreach (var reply in lifecycle.GenerateAsync(ToGenerationRequest(model.Id, request), cancellationToken)
                               .ConfigureAwait(false))
            {
                switch (reply)
                {
                    case TextDelta { Text.Length: > 0 } delta:
                        yield return AssistantResponseChunk.ForTextDelta(delta.Text);
                        break;
                    case ToolCallGenerated call:
                        yield return AssistantResponseChunk.ForToolCall(call.Call);
                        break;
                    case GenerationEnded { Reason: GenerationStopReason.OutputLimit }:
                        yield return AssistantResponseChunk.ForNotice(OutputLimitNotice);
                        break;
                    case GenerationEnded { Reason: GenerationStopReason.Cancelled }:
                        // Not asked for here, so the host stopped it as it shut down.
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new ModelHostException(ModelHostErrorCode.ShuttingDown);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _answering);
            if (pictures && Interlocked.Decrement(ref _picturesBeingRead) == 0 && _projectorOnDemand)
            {
                RestVisionLater();
            }
        }
    }

    // Some minutes after the last question about a picture, the model is loaded again without its projector, ahead of the next question and never
    // under an answer.
    private void RestVisionLater()
    {
        var rest = new CancellationTokenSource();
        Interlocked.Exchange(ref _visionRest, rest)?.Cancel();
        VisionResting = RestVisionAsync(rest.Token);
    }

    private async Task RestVisionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(VisionRest, cancellationToken).ConfigureAwait(false);
            await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Said before the look at what is being answered, as an answer says it is being given before it looks here: one of the two
                // always sees the other, so the model is never loaded again under an answer.
                _resting = true;
                if (Volatile.Read(ref _answering) > 0)
                {
                    _resting = false;
                    RestVisionLater();
                    return;
                }

                var choice = await FindModelAsync(cancellationToken).ConfigureAwait(false);
                if (lifecycle.Model is not { SupportsVision: true } loaded || choice is not { VisionOnDemand: true, Files.ProjectorPath: not null }
                    || !HasConfigurationOf(loaded, choice.Files))
                {
                    return;
                }

                LogVisionRested(logger);
                var model = await lifecycle.LoadAsync(choice.Files with { ProjectorPath = null }, cancellationToken).ConfigureAwait(false);
                _loaded = model;
                _loadedWindow = choice.Files.ContextLength ?? ModelFiles.DefaultContextLength;
                _loadedFiles = choice.Files;
            }
            finally
            {
                _resting = false;
                _loadGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Another picture was asked about, or the service is going.
        }
        catch (ModelHostException)
        {
            // The model could not be loaded again now; the next question loads it.
        }
    }

    /// <inheritdoc/>
    public async Task<ModelInfo?> PreloadAsync(CancellationToken cancellationToken = default)
    {
        if (await FindModelAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return lifecycle.Model;
        }

        LogPreloading(logger);
        return await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The protocol's request for <paramref name="request"/>: the same prompt, for the model named.</summary>
    public static GenerationRequest ToGenerationRequest(string modelId, ModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var messages = request.Messages.Select(ToPromptMessage).ToArray();
        return request.Images.Count > 0
            ? new GenerateMultimodalRequest(modelId, request.Instructions, messages, request.Images)
            {
                Tools = request.Tools,
                MaxOutputTokens = request.MaxOutputTokens,
                Temperature = request.Temperature,
            }
            : new GenerateTextRequest(modelId, request.Instructions, messages)
            {
                Tools = request.Tools,
                MaxOutputTokens = request.MaxOutputTokens,
                Temperature = request.Temperature,
            };
    }

    private static PromptMessage ToPromptMessage(Message message) => new(message.Role, message.Text)
    {
        ToolCalls = message.ToolCalls,
        ToolCallId = message.ToolResult?.ToolCallId,
    };

    // The loaded model, when it has the window the conversation wants; else the one the settings name, loaded now: its first use, or again with
    // the other window.
    private async Task<ModelInfo> EnsureLoadedAsync(CancellationToken cancellationToken, bool pictures = false)
    {
        var wanted = await FindModelAsync(cancellationToken).ConfigureAwait(false);
        if (!_resting && lifecycle.Model is { } loaded && (wanted is null || Suits(loaded, wanted, pictures)))
        {
            return loaded;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have loaded it while this one waited, and what is wanted may have changed.
            var choice = await FindModelAsync(cancellationToken).ConfigureAwait(false);
            var current = lifecycle.Model;
            if (current is not null && (choice is null || Suits(current, choice, pictures)))
            {
                return current;
            }

            if (choice is null)
            {
                LogNoModelSetUp(logger);
                throw new ModelHostException(ModelHostErrorCode.ModelNotFound, "No local model is set up.");
            }

            // With pictures read only when it is needed, the projector is loaded for a question that carries a picture, and kept while one is answered.
            var onDemand = choice.VisionOnDemand && choice.Files.ProjectorPath is not null;
            var files = onDemand && !pictures && Volatile.Read(ref _picturesBeingRead) == 0 ? choice.Files with { ProjectorPath = null } : choice.Files;
            var window = files.ContextLength ?? ModelFiles.DefaultContextLength;
            if (current is null)
            {
                LogLoadingOnFirstUse(logger);
            }
            else if (HasConfigurationOf(current, choice.Files))
            {
                LogLoadingForPictures(logger);
            }
            else
            {
                LogLoadingAgain(logger, window, _documents);
            }

            if (choice.Resolved is { } resolved)
            {
                LogProfile(logger, resolved.Profile.Id, resolved.Preset.Id, resolved.ProjectorMissing, resolved.BelowRecommendedMemory);
            }

            await EnsureFilesMatchPackageAsync(files, cancellationToken).ConfigureAwait(false);
            var model = await lifecycle.LoadAsync(files, cancellationToken).ConfigureAwait(false);
            _loaded = model;
            _loadedWindow = window;
            _loadedFiles = choice.Files;
            _projectorOnDemand = onDemand;
            return model;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    // Whether the loaded model is what is asked of it: the window the conversation wants, and, for a question that carries a picture, the projector
    // when the model has one to load (it may have been loaded without it: pictures are read only when it is needed, or the projector came later).
    private bool Suits(ModelInfo loaded, ModelChoice wanted, bool pictures) =>
        HasConfigurationOf(loaded, wanted.Files) && (!pictures || wanted.Files.ProjectorPath is null || loaded.SupportsVision);

    // Compare the requested configuration, since the engine may clamp the context or temporarily omit the vision projector.
    // For a model loaded elsewhere (Settings, setup), only its reported context is known.
    private bool HasConfigurationOf(ModelInfo loaded, ModelFiles files)
    {
        var window = files.ContextLength ?? ModelFiles.DefaultContextLength;
        if (_loaded is not { } mine || !mine.Equals(loaded)) return loaded.ContextLength == window;

        // Reusing a loaded model must also honor changes to the device and files. In particular, a model
        // preloaded on CPU must not stay there after the user enables GPU acceleration.
        return _loadedWindow == window && _loadedFiles is { } previous
            && previous.CpuOnly == files.CpuOnly && previous.GpuDeviceId == files.GpuDeviceId
            && previous.ModelPath == files.ModelPath && previous.ProjectorPath == files.ProjectorPath
            && previous.ChatTemplatePath == files.ChatTemplatePath
            && previous.RuntimeArguments.SequenceEqual(files.RuntimeArguments);
    }

    // A model that came packaged with the Assistant is loaded only when its files are the ones that were packaged (PROJECT_SPEC §3.5, step 123): present, the
    // size and the SHA-256 the package lists. A model the user picked or put in their own models folder is not listed anywhere and is used as it is.
    private async Task EnsureFilesMatchPackageAsync(ModelFiles files, CancellationToken cancellationToken)
    {
        if (assets is null)
        {
            return;
        }

        try
        {
            await assets.EnsureModelUsableAsync(files, cancellationToken).ConfigureAwait(false);
        }
        catch (AssetIntegrityException failure)
        {
            LogFilesFailedCheck(logger, failure.State.GroupId, failure.State.Status);
            throw new ModelHostException(ModelHostErrorCode.FilesFailedCheck, "The model's packaged files did not pass their check.");
        }
    }

    // The model the settings name: a file the user picked, as it is, else the installed model profile they choose.
    private async Task<ModelChoice?> FindModelAsync(CancellationToken cancellationToken)
    {
        var all = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var model = all.Model;
        var machine = hardware?.Get();
        if (string.IsNullOrWhiteSpace(model.ModelFilePath))
        {
            if (profiles.Resolve(model) is not { IsInstalled: true } resolved)
            {
                return null;
            }

            // The window the conversation wants, unless the user set one: the profile's own only when a limit is "no limit".
            var window = ContextWindowPlan.Window(
                model.ContextLength, all.ContextLimits, _documents, resolved.Files.ContextLength, resolved.Profile, machine,
                ceiling: model.HardwarePresetId is null ? null : resolved.Preset.MaxContextTokens);
            return new ModelChoice(
                resolved.Files with { ContextLength = window, CpuOnly = !model.UseGpuAcceleration, GpuDeviceId = model.GpuDeviceId }, resolved, model.VisionOnDemand);
        }

        var projector = ProjectorFor(model);
        return new ModelChoice(
            new ModelFiles(model.ModelFilePath)
            {
                ProjectorPath = projector,
                ChatTemplatePath = string.IsNullOrWhiteSpace(model.ChatTemplateFilePath) ? null : model.ChatTemplateFilePath,
                ContextLength = ContextWindowPlan.Window(
                    model.ContextLength, all.ContextLimits, _documents, fallback: null, profile: null, machine,
                    modelFileBytes: machine is null ? null : ContextWindowPlan.FileBytes(model.ModelFilePath, projector)),
                CpuOnly = !model.UseGpuAcceleration,
                GpuDeviceId = model.GpuDeviceId,
            },
            null,
            model.VisionOnDemand);
    }

    // The projector that goes with the user's model file: the one the settings name, or else the one that lies beside a model the Assistant
    // downloaded, under the name its downloads give it. A model that was downloaded before its projector was offered is set up without one in the
    // settings; once the projector is beside it, the model reads pictures without being set up again.
    private static string? ProjectorFor(ModelSettings model)
    {
        if (!string.IsNullOrWhiteSpace(model.ProjectorFilePath))
        {
            return model.ProjectorFilePath;
        }

        try
        {
            if (string.Equals(Path.GetFileName(model.ModelFilePath), Assistant.Core.Models.DownloadCatalog.ModelFileName, StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(model.ModelFilePath) is { Length: > 0 } folder
                && Path.Combine(folder, Assistant.Core.Models.DownloadCatalog.ProjectorFileName) is var beside && File.Exists(beside))
            {
                return beside;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A path that cannot be read as one names no projector.
        }

        return null;
    }

    /// <summary>
    /// The files to load, the profile they came from (none for a file the user picked), and whether the projector among them is loaded only for a
    /// question that carries a picture.
    /// </summary>
    private sealed record ModelChoice(ModelFiles Files, ResolvedModel? Resolved, bool VisionOnDemand = false);

    [LoggerMessage(EventId = 2120, Level = LogLevel.Information, Message = "Loading the local model on its first use")]
    private static partial void LogLoadingOnFirstUse(ILogger logger);

    [LoggerMessage(EventId = 2124, Level = LogLevel.Information, Message = "Loading the local model ahead of its first use")]
    private static partial void LogPreloading(ILogger logger);

    [LoggerMessage(EventId = 2125, Level = LogLevel.Information, Message = "Loading the local model again with a window of {Window} tokens (conversation with files: {Documents})")]
    private static partial void LogLoadingAgain(ILogger logger, int window, bool documents);

    [LoggerMessage(EventId = 2126, Level = LogLevel.Debug, Message = "The conversation being answered carries files: {Documents}")]
    private static partial void LogWindowDemand(ILogger logger, bool documents);

    [LoggerMessage(EventId = 2127, Level = LogLevel.Information, Message = "Loading the local model again with its projector, for a question that carries a picture")]
    private static partial void LogLoadingForPictures(ILogger logger);

    [LoggerMessage(EventId = 2128, Level = LogLevel.Information, Message = "Loading the local model again without its projector: no picture has been asked about for a while")]
    private static partial void LogVisionRested(ILogger logger);

    [LoggerMessage(EventId = 2121, Level = LogLevel.Information, Message = "Asked to generate, but no local model is set up")]
    private static partial void LogNoModelSetUp(ILogger logger);

    [LoggerMessage(
        EventId = 2122,
        Level = LogLevel.Information,
        Message = "Loading model profile {ProfileId} with the {PresetId} hardware preset (projector missing: {ProjectorMissing}, below recommended memory: {BelowMemory})")]
    private static partial void LogProfile(ILogger logger, string profileId, string presetId, bool projectorMissing, bool belowMemory);

    [LoggerMessage(
        EventId = 2123,
        Level = LogLevel.Warning,
        Message = "The packaged files of model {ModelId} did not pass their check ({Status}), so it was not loaded")]
    private static partial void LogFilesFailedCheck(ILogger logger, string modelId, AssetGroupStatus status);
}
