using System.Windows;
using System.Windows.Controls;
namespace Assistant.UI.Onboarding;
public partial class VoiceSetupControl : UserControl
{
    public VoiceSetupControl() => InitializeComponent();
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel setup) setup.PendingApiKey = ((PasswordBox)sender).Password;
    }
}
