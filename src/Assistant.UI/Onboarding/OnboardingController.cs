using Assistant.UI.ViewModels;
using Assistant.Windows.Frame;
using Assistant.Windows.Placement;
using System.Windows.Input;

namespace Assistant.UI.Onboarding;

public sealed class OnboardingController(SetupViewModel setup, IWindowFrameFactory frames, IWindowPlacementService placement)
{
    private OnboardingWindow? _window;
    public ICommand ShowCommand => new RelayCommand(_ => Show());
    public void Show(Action? onComplete = null)
    {
        if (_window is not null) { _window.Activate(); return; }
        _window = new(setup, frames, placement);
        _window.Closed += (_, _) => { var finished = _window?.IsFinished == true; _window = null; if (finished) onComplete?.Invoke(); };
        _window.Show();
    }
}
