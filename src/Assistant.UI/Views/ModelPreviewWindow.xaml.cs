using System.Windows;
using Assistant.UI.ViewModels;
using Microsoft.Win32;

namespace Assistant.UI.Views;

/// <summary>
/// Where the local model is loaded and unloaded until the settings window exists: its status in words, a bar while it
/// loads or unloads, and the model file with its optional projector and chat template. Everything else lives in the
/// <see cref="ModelStatusViewModel"/>; this window only asks for files.
/// </summary>
public partial class ModelPreviewWindow : Window
{
    private const string ModelFilter = "GGUF model files (*.gguf)|*.gguf|All files (*.*)|*.*";
    private const string TemplateFilter = "Chat template files (*.jinja;*.txt)|*.jinja;*.txt|All files (*.*)|*.*";

    private readonly ModelStatusViewModel _viewModel;

    internal ModelPreviewWindow(ModelStatusViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void OnBrowseModel(object sender, RoutedEventArgs e)
    {
        if (Pick("Choose a model file", ModelFilter) is { } path)
        {
            _viewModel.ModelFilePath = path;
        }
    }

    private void OnBrowseProjector(object sender, RoutedEventArgs e)
    {
        if (Pick("Choose the projector file for images", ModelFilter) is { } path)
        {
            _viewModel.ProjectorFilePath = path;
        }
    }

    private void OnBrowseTemplate(object sender, RoutedEventArgs e)
    {
        if (Pick("Choose a chat template file", TemplateFilter) is { } path)
        {
            _viewModel.ChatTemplateFilePath = path;
        }
    }

    private string? Pick(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
