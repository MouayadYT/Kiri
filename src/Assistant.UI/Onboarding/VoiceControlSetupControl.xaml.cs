using System.Windows.Controls;
using System.Windows;

namespace Assistant.UI.Onboarding;

public partial class VoiceControlSetupControl : UserControl
{
    public static readonly DependencyProperty ShowApplyButtonProperty = DependencyProperty.Register(nameof(ShowApplyButton), typeof(bool), typeof(VoiceControlSetupControl), new PropertyMetadata(true));
    public bool ShowApplyButton { get => (bool)GetValue(ShowApplyButtonProperty); set => SetValue(ShowApplyButtonProperty, value); }

    /// <summary>
    /// Whether the control says itself how a download or a change is going. Setup says it in its own footer; in Settings, where what is chosen is used at
    /// once, there is no other place.
    /// </summary>
    public static readonly DependencyProperty ShowStatusProperty = DependencyProperty.Register(nameof(ShowStatus), typeof(bool), typeof(VoiceControlSetupControl), new PropertyMetadata(false));
    public bool ShowStatus { get => (bool)GetValue(ShowStatusProperty); set => SetValue(ShowStatusProperty, value); }
    public VoiceControlSetupControl() => InitializeComponent();

    // A microphone plugged in since the page was opened is in the list by the time the list is.
    private void OnMicrophonesOpened(object sender, EventArgs e) => (DataContext as SetupViewModel)?.RefreshMicrophones();
}
