using System.Windows;
using System.Windows.Controls;

namespace Assistant.UI.Onboarding;

public partial class SearchSetupControl : UserControl
{
    public SearchSetupControl()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is SearchSetupViewModel previous) previous.PropertyChanged -= OnSearchChanged;
            ApiKey.Clear();
            if (IsLoaded && args.NewValue is SearchSetupViewModel current) current.PropertyChanged += OnSearchChanged;
        };
        Loaded += (_, _) => { if (DataContext is SearchSetupViewModel search) { search.PropertyChanged -= OnSearchChanged; search.PropertyChanged += OnSearchChanged; } };
        Unloaded += (_, _) => { if (DataContext is SearchSetupViewModel search) { search.PropertyChanged -= OnSearchChanged; search.PendingApiKey = null; } ApiKey.Clear(); };
    }
    private void OnKeyChanged(object sender, RoutedEventArgs args) { if (DataContext is SearchSetupViewModel search) search.PendingApiKey = ApiKey.Password.Length == 0 ? null : ApiKey.Password; }
    private void OnSearchChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SearchSetupViewModel.PendingApiKey) && sender is SearchSetupViewModel { PendingApiKey: null } && ApiKey.Password.Length > 0) ApiKey.Clear();
    }
    public static readonly DependencyProperty ShowApplyButtonProperty = DependencyProperty.Register(nameof(ShowApplyButton), typeof(bool), typeof(SearchSetupControl),
        new PropertyMetadata(true, (element, args) => ((SearchSetupControl)element).ApplyButton.Visibility = (bool)args.NewValue ? Visibility.Visible : Visibility.Collapsed));
    public bool ShowApplyButton { get => (bool)GetValue(ShowApplyButtonProperty); set => SetValue(ShowApplyButtonProperty, value); }
}
