using System.Runtime.CompilerServices;
using Assistant.Windows.Interop;
using Windows.UI.ViewManagement;

namespace Assistant.Windows.Backdrop;

/// <summary>
/// Whether Windows allows blurred, translucent surfaces: transparency effects are on and high contrast is off
/// (PROJECT_SPEC §4.0).
/// </summary>
internal sealed class TransparencySettings : IDisposable
{
    private readonly UISettings _uiSettings = new();
    private readonly Action<Action> _post;

    /// <param name="post">Runs an action on the thread that <see cref="Changed"/> is raised on.</param>
    public TransparencySettings(Action<Action> post)
    {
        _post = post;
        _uiSettings.AdvancedEffectsEnabledChanged += OnAdvancedEffectsEnabledChanged;
    }

    /// <summary>
    /// Raised when the transparency effects setting changes. Windows reports high contrast changes to top-level
    /// windows with <c>WM_SETTINGCHANGE</c> instead.
    /// </summary>
    public event EventHandler? Changed;

    public bool AllowsBlur => _uiSettings.AdvancedEffectsEnabled && !IsHighContrastOn();

    public void Dispose() => _uiSettings.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsEnabledChanged;

    private static bool IsHighContrastOn()
    {
        var highContrast = new User32.HighContrast { Size = (uint)Unsafe.SizeOf<User32.HighContrast>() };
        return User32.SystemParametersInfo(User32.SPI_GETHIGHCONTRAST, highContrast.Size, ref highContrast, 0)
            && (highContrast.Flags & User32.HCF_HIGHCONTRASTON) != 0;
    }

    // Raised on a thread-pool thread.
    private void OnAdvancedEffectsEnabledChanged(UISettings sender, object args) =>
        _post(() => Changed?.Invoke(this, EventArgs.Empty));
}
