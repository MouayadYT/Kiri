using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Assistant.UI.Windowing;
using Assistant.Windows.Frame;
using Assistant.Windows.Placement;

namespace Assistant.UI.Onboarding;

public partial class OnboardingWindow : Window
{
    // The steps in order: AI, voice, recognition, gaming, cleanup, connections, search, and the page that says setup is done.
    internal const int GamingStepIndex = 3, CleanupStepIndex = 4, ConnectionsStepIndex = 5, SearchStepIndex = 6, DoneStepIndex = 7;
    // The hairline Windows draws around the window: the one the Settings and History windows have.
    private const int BorderColor = 0x3C3C3C;
    private readonly SetupViewModel _setup;
    private int _step;
    public bool IsFinished => _step == DoneStepIndex;
    public OnboardingWindow(SetupViewModel setup, IWindowFrameFactory frames, IWindowPlacementService placement, bool initialize = true)
    {
        InitializeComponent(); _setup = setup; DataContext = setup;
        WindowFrameHost.Attach(this, frames, new(SystemBackdropKind.Mica, BorderColor));
        Loaded += async (_, _) =>
        {
            placement.PlaceCenteredOnActiveMonitor(new WindowInteropHelper(this).Handle, Width, Height, 24);
            if (initialize) await setup.InitializeAsync();
        };
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 0) { if (await _setup.ApplyAiAsync()) ShowStep(1); }
        else if (_step == 1) { if (await _setup.ApplyVoiceAsync()) ShowStep(2); }
        else if (_step == 2) { if (await _setup.ApplyVoiceControlAsync()) ShowStep(GamingStepIndex); }
        else if (_step == GamingStepIndex) { if (await _setup.ApplyGameModeAsync()) ShowStep(CleanupStepIndex); }
        else if (_step == CleanupStepIndex) { if (await _setup.ApplyCleanupAsync()) ShowStep(ConnectionsStepIndex); }
        else if (_step == ConnectionsStepIndex) { if (_setup.CanContinueConnections) ShowStep(SearchStepIndex); }
        else if (_step == SearchStepIndex) { if (await _setup.FinishAsync()) ShowStep(DoneStepIndex); else StatusFooter.Visibility = Visibility.Visible; }
        else Close();
    }
    // The voice can be left for later: nothing is downloaded or chosen, and setup goes on. Answers are written and not spoken until one is set up in Settings.
    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_step == 1 && !_setup.IsBusy) { _setup.SkipVoice = true; ShowStep(2); }
    }
    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(Math.Max(0, _step - 1));
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_setup.IsBusy) { _setup.CancelCommand.Execute(null); e.Cancel = true; }
        base.OnClosing(e);
    }
    internal void ShowStep(int step)
    {
        _step = step;
        AiPage.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        VoicePage.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ControlPage.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        GamingPage.Visibility = step == GamingStepIndex ? Visibility.Visible : Visibility.Collapsed;
        CleanupPage.Visibility = step == CleanupStepIndex ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPage.Visibility = step == ConnectionsStepIndex ? Visibility.Visible : Visibility.Collapsed;
        SearchPage.Visibility = step == SearchStepIndex ? Visibility.Visible : Visibility.Collapsed;
        DonePage.Visibility = step == DoneStepIndex ? Visibility.Visible : Visibility.Collapsed;
        StatusFooter.Visibility = step is ConnectionsStepIndex or SearchStepIndex ? Visibility.Collapsed : Visibility.Visible;
        Steps.Visibility = step == DoneStepIndex ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = step is > 0 and < DoneStepIndex ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Heading.Text = step switch { 0 => "Choose an AI model", 1 => "Choose a voice model", 2 => "Voice mode", GamingStepIndex => "Game mode", CleanupStepIndex => "Cleanup", ConnectionsStepIndex => "Let's get you connected.", SearchStepIndex => "Search", _ => "We're all set." };
        // Nothing goes under a heading: each page says what it is by what it asks.
        AiStep.Foreground = StepBrush(step == 0);
        VoiceStep.Foreground = StepBrush(step == 1);
        ControlStep.Foreground = StepBrush(step == 2);
        GamingStep.Foreground = StepBrush(step == GamingStepIndex);
        CleanupStep.Foreground = StepBrush(step == CleanupStepIndex);
        ConnectionsStep.Foreground = StepBrush(step == ConnectionsStepIndex);
        SearchStep.Foreground = StepBrush(step == SearchStepIndex);
        NextButton.Content = step switch { < SearchStepIndex => "Next →", SearchStepIndex => "Finish", _ => "Start using Kiri" };
        if (step < DoneStepIndex) NextButton.SetBinding(IsEnabledProperty, new Binding(step switch { 0 => nameof(SetupViewModel.CanContinueAi), 1 => nameof(SetupViewModel.CanContinueVoice), 2 => nameof(SetupViewModel.CanContinueVoiceControl), GamingStepIndex => nameof(SetupViewModel.CanContinueGameMode), CleanupStepIndex => nameof(SetupViewModel.CanContinueCleanup), ConnectionsStepIndex => nameof(SetupViewModel.CanContinueConnections), _ => nameof(SetupViewModel.CanContinueSearch) }));
        else { NextButton.ClearValue(IsEnabledProperty); NextButton.IsEnabled = true; }
    }
    private static SolidColorBrush StepBrush(bool current) => new(current ? Color.FromRgb(0xF5, 0xF5, 0xF5) : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
}
