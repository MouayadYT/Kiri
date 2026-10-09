using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Assistant.UI.Messages;

namespace Assistant.UI.ViewModels;

/// <summary>
/// The answer to a sum, under the Search or Ask bar while the sum is typed (PROJECT_SPEC §4.1), as the reference draws it: the sum as it was typed
/// with an equals sign, its value large under it, and a button that copies the value. It is worked out on every keystroke, without a model and
/// without waiting. The card stays the same object while the sum changes, so that only its words change on screen.
/// </summary>
public sealed class CalculationCardViewModel : INotifyPropertyChanged
{
    private readonly ITextClipboard? _clipboard;
    private readonly RelayCommand _copyCommand;
    private string _caption = "";
    private string _value = "";

    public CalculationCardViewModel(ITextClipboard? clipboard)
    {
        _clipboard = clipboard;
        _copyCommand = new RelayCommand(_ => _clipboard?.TrySetText(_value), _ => _clipboard is not null && _value.Length > 0);
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The sum as it was typed, with an equals sign after it ("9+10 =").</summary>
    public string Caption
    {
        get => _caption;
        set => Set(ref _caption, value ?? "");
    }

    /// <summary>The value ("19").</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value ?? ""))
            {
                _copyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Copies <see cref="Value"/> to the clipboard.</summary>
    public ICommand CopyCommand => _copyCommand;

    private bool Set(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
