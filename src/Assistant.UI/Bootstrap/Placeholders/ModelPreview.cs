using System.Windows;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>Shows the model window, where a model file is loaded and unloaded by hand.</summary>
internal interface IModelPreview
{
    /// <summary>Opens the window, or brings the open one forward.</summary>
    void Show();
}

/// <summary>Opens one <see cref="ModelPreviewWindow"/> at a time, on the UI thread.</summary>
internal sealed class ModelPreviewLauncher : IModelPreview
{
    private readonly Func<ModelPreviewWindow> _create;
    private ModelPreviewWindow? _window;

    public ModelPreviewLauncher(Func<ModelStatusViewModel> viewModel) : this(() => new ModelPreviewWindow(viewModel()))
    {
    }

    internal ModelPreviewLauncher(Func<ModelPreviewWindow> create) => _create = create;

    /// <inheritdoc/>
    public void Show()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.Dispatcher.InvokeAsync(() =>
        {
            if (_window is null)
            {
                _window = _create();
                _window.Closed += (_, _) => _window = null;
                _window.Show();
            }

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
        });
    }
}
