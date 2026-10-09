using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Assistant.UI.Settings;

/// <summary>Base for the settings window's view models: property change notification.</summary>
public abstract class NotifyingObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets <paramref name="field"/> and announces the change; returns whether the value changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>Announces that <paramref name="name"/> changed.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
