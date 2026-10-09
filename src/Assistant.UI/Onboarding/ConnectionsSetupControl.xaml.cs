using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Assistant.UI.Onboarding;

public partial class ConnectionsSetupControl : UserControl
{
    /// <summary>
    /// Whether the control lists the apps the setup offers, with a Connect button each. Settings lists what can be connected itself, in one list, and shows only the
    /// form and what a connection came to.
    /// </summary>
    public static readonly DependencyProperty ShowsAppsProperty =
        DependencyProperty.Register(nameof(ShowsApps), typeof(bool), typeof(ConnectionsSetupControl), new PropertyMetadata(true));

    /// <summary>The words of the button that opens the form for a server of the user's own.</summary>
    public static readonly DependencyProperty AddLabelProperty =
        DependencyProperty.Register(nameof(AddLabel), typeof(string), typeof(ConnectionsSetupControl), new PropertyMetadata("+  Add MCP"));

    private ConnectionsSetupViewModel? _viewModel;

    public bool ShowsApps
    {
        get => (bool)GetValue(ShowsAppsProperty);
        set => SetValue(ShowsAppsProperty, value);
    }

    public string AddLabel
    {
        get => (string)GetValue(AddLabelProperty);
        set => SetValue(AddLabelProperty, value);
    }

    public ConnectionsSetupControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged; _viewModel = null; };
    }
    private void Attach()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = DataContext as ConnectionsSetupViewModel;
        if (_viewModel is null) return;
        _viewModel.ConfirmCloudConnection = name => MessageBox.Show(Window.GetWindow(this),
            $"{name} is not on this PC. Allow Kiri to connect to other devices and services?\n\nThis turns off Local Only in Settings → Privacy. Your selected AI model still runs on your PC.",
            "Allow connections beyond this PC?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        _viewModel.PropertyChanged += OnViewModelChanged;
    }
    private void OnTokenChanged(object sender, RoutedEventArgs args) { if (_viewModel is not null) _viewModel.PendingToken = TokenBox.Password; }
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ConnectionsSetupViewModel.IsBusy) && _viewModel?.IsBusy == false) TokenBox.Clear();
        if (args.PropertyName == nameof(ConnectionsSetupViewModel.ShowAddEditor) && _viewModel?.ShowAddEditor == false) TokenBox.Clear();
    }
}
