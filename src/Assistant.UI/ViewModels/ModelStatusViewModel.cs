using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Contracts;
using Assistant.Core.Events;
using Assistant.Core.ModelHosting;
using Assistant.Core.Settings;

namespace Assistant.UI.ViewModels;

/// <summary>
/// What the model window shows and does (PROJECT_SPEC §5.6): the local model's status in words, whether it is busy,
/// and the files to load, which load and unload it through the <see cref="IModelLifecycle"/>. It follows the
/// <see cref="ModelStatusChanged"/> events the lifecycle publishes, on the UI thread. It never logs, and it keeps the
/// file paths only in memory and in the settings.
/// </summary>
public sealed class ModelStatusViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IModelLifecycle _lifecycle;
    private readonly ISettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly IDisposable _subscription;
    private readonly RelayCommand _loadCommand;
    private readonly RelayCommand _unloadCommand;
    private ModelStatusChanged _current;
    private string _modelFilePath = "";
    private string _projectorFilePath = "";
    private string _chatTemplateFilePath = "";
    private string _notice = "";
    private bool _initialized;
    private bool _disposed;

    public ModelStatusViewModel(
        IModelLifecycle lifecycle, IAppEventBus events, ISettingsService settings, Dispatcher? dispatcher = null)
    {
        _lifecycle = lifecycle;
        _settings = settings;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _current = lifecycle.Current;
        _loadCommand = new RelayCommand(_ => _ = LoadAsync(), _ => !_disposed);
        _unloadCommand = new RelayCommand(_ => _ = UnloadAsync(), _ => CanUnload);
        _subscription = events.Subscribe<ModelStatusViewModel, ModelStatusChanged>(
            this, static (viewModel, status, _) => viewModel.OnStatusChanged(status));
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the model stands.</summary>
    public ModelStatus Status => _current.Status;

    /// <summary>The model's status in plain words, and for a failure what to do about it.</summary>
    public string StatusText => ModelStatusText.Describe(_current.Status, _current.Failure);

    /// <summary>Whether the model is loading or unloading, so the window shows that something is going on.</summary>
    public bool IsBusy => _current.Status is ModelStatus.Loading or ModelStatus.Unloading;

    /// <summary>Whether the model is loaded and ready.</summary>
    public bool IsReady => _current.Status == ModelStatus.Ready;

    /// <summary>Whether the model failed, so the window shows the status as a problem.</summary>
    public bool IsFailed => _current.Status == ModelStatus.Failed;

    /// <summary>Whether there is a model, or a load, to unload.</summary>
    public bool CanUnload => _current.Status is not (ModelStatus.NotLoaded or ModelStatus.NotInstalled or ModelStatus.Unloading);

    /// <summary>The GGUF model file, as the user typed or picked it.</summary>
    public string ModelFilePath
    {
        get => _modelFilePath;
        set => Set(ref _modelFilePath, value ?? "");
    }

    /// <summary>The multimodal projector file, or empty for none.</summary>
    public string ProjectorFilePath
    {
        get => _projectorFilePath;
        set => Set(ref _projectorFilePath, value ?? "");
    }

    /// <summary>The chat template file, or empty to use the model's own.</summary>
    public string ChatTemplateFilePath
    {
        get => _chatTemplateFilePath;
        set => Set(ref _chatTemplateFilePath, value ?? "");
    }

    /// <summary>A problem with what was entered, such as no model file; empty when there is none.</summary>
    public string Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    /// <summary>Loads the model from the files above.</summary>
    public ICommand LoadCommand => _loadCommand;

    /// <summary>Unloads the model, or ends the load in progress.</summary>
    public ICommand UnloadCommand => _unloadCommand;

    /// <summary>
    /// Fills the file paths from the saved settings, once, and only those the user has not entered already.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var model = (await _settings.LoadAsync().ConfigureAwait(true)).Model;
        if (ModelFilePath.Length == 0)
        {
            ModelFilePath = model.ModelFilePath ?? "";
        }

        if (ProjectorFilePath.Length == 0)
        {
            ProjectorFilePath = model.ProjectorFilePath ?? "";
        }

        if (ChatTemplateFilePath.Length == 0)
        {
            ChatTemplateFilePath = model.ChatTemplateFilePath ?? "";
        }
    }

    /// <summary>Loads the model from the files entered, and remembers them in the settings.</summary>
    public async Task LoadAsync()
    {
        Notice = "";
        if (!TryCreateFiles(out var files, out var problem))
        {
            Notice = problem;
            return;
        }

        // The files are remembered in the settings, which refuse a path they cannot keep and may fail to write. That never
        // stops the model from loading: the user is told, and the load goes on.
        try
        {
            await _settings.UpdateAsync(settings => settings with
            {
                Model = settings.Model with
                {
                    ModelFilePath = files.ModelPath,
                    ProjectorFilePath = files.ProjectorPath,
                    ChatTemplateFilePath = files.ChatTemplatePath,
                },
            }).ConfigureAwait(true);
        }
        catch (SettingsValidationException)
        {
            Notice = "One of those file paths can't be used. Check that each is a full path, such as C:\\Models\\model.gguf.";
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Notice = "The file paths couldn't be saved in the settings, but the model is loading.";
        }

        try
        {
            await _lifecycle.LoadAsync(files).ConfigureAwait(true);
        }
        catch (ModelHostException)
        {
            // The status says why it failed, or that another request replaced this one.
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Unloads the model.</summary>
    public async Task UnloadAsync()
    {
        Notice = "";
        try
        {
            await _lifecycle.UnloadAsync().ConfigureAwait(true);
        }
        catch (ModelHostException)
        {
            // The status shows what the host reported.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subscription.Dispose();
    }

    private bool TryCreateFiles(out ModelFiles files, out string problem)
    {
        files = new ModelFiles(string.Empty);
        var model = Clean(ModelFilePath);
        if (model.Length == 0)
        {
            problem = "Choose a model file (.gguf) first.";
            return false;
        }

        var projector = Clean(ProjectorFilePath);
        var template = Clean(ChatTemplateFilePath);
        if (!IsFullyQualified(model) || (projector.Length > 0 && !IsFullyQualified(projector))
            || (template.Length > 0 && !IsFullyQualified(template)))
        {
            problem = "Enter the full path of each file, such as C:\\Models\\model.gguf.";
            return false;
        }

        files = new ModelFiles(model)
        {
            ProjectorPath = projector.Length > 0 ? projector : null,
            ChatTemplatePath = template.Length > 0 ? template : null,
        };
        problem = "";
        return true;
    }

    // A path pasted from Explorer's "Copy as path" comes with quotes.
    private static string Clean(string path) => path.Trim().Trim('"').Trim();

    private static bool IsFullyQualified(string path) => System.IO.Path.IsPathFullyQualified(path);

    // Called on the lifecycle's publishing thread.
    private Task OnStatusChanged(ModelStatusChanged status) =>
        _dispatcher.InvokeAsync(() => Apply(status)).Task;

    private void Apply(ModelStatusChanged status)
    {
        if (_disposed || _current == status)
        {
            return;
        }

        _current = status;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(CanUnload));
        _unloadCommand.RaiseCanExecuteChanged();
    }

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field != value)
        {
            field = value;
            OnPropertyChanged(name);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
