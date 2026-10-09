using System.Globalization;
using Assistant.Core.ModelProfiles;

namespace Assistant.UI.Settings;

/// <summary>
/// A whole number the user types in the Settings window: its text, and what the Assistant says about it as it is typed.
/// A number the app can work with is saved at once; one it cannot is explained and left unsaved, and what the user typed
/// stays as they typed it until it is fixed or the window is opened again. A number that may use a lot of memory is
/// warned about and still saved (<see cref="ContextAdvice"/>).
/// </summary>
public sealed class NumberField : NotifyingObject
{
    private readonly Func<int, ContextAdvice> _assess;
    private readonly Action<int> _commit;
    private string _text = string.Empty;
    private ContextAdvice _advice = ContextAdvice.None;
    private bool _showing;
    private bool _hasRejectedText;

    /// <summary>Creates a field.</summary>
    /// <param name="assess">Judges a number that was typed. An error means it cannot be saved.</param>
    /// <param name="commit">Saves a number that was typed and accepted.</param>
    public NumberField(Func<int, ContextAdvice> assess, Action<int> commit)
    {
        _assess = assess;
        _commit = commit;
    }

    /// <summary>What is typed. Digits, with commas or spaces between groups of them, are read as a number.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value ?? string.Empty) && !_showing)
            {
                Typed();
            }
        }
    }

    /// <summary>What the Assistant says about the number, or empty.</summary>
    public string AdviceText => _advice.Message;

    /// <summary>How serious what it says is.</summary>
    public ContextAdviceLevel AdviceLevel => _advice.Level;

    /// <summary>Whether there is anything to say.</summary>
    public bool HasAdvice => _advice.Level != ContextAdviceLevel.None;

    /// <summary>Whether the text is a number the app cannot work with, so nothing was saved.</summary>
    public bool HasError => _advice.IsError;

    /// <summary>Shows the saved <paramref name="value"/>, unless the user is in the middle of typing something else.</summary>
    internal void Show(int value)
    {
        if (!_hasRejectedText && !(TryParse(_text, out var typed) && typed == value))
        {
            SetText(Format(value));
        }

        Refresh();
    }

    /// <summary>Shows the saved <paramref name="value"/>, dropping anything the user typed that was not accepted.</summary>
    internal void Reset(int value)
    {
        _hasRejectedText = false;
        SetText(Format(value));
        Refresh();
    }

    /// <summary>Judges the text again, after something it depends on changed.</summary>
    internal void Refresh()
    {
        SetAdvice(TryParse(_text, out var number) ? _assess(number) : NotANumber(_text));
    }

    private void Typed()
    {
        var advice = TryParse(_text, out var number) ? _assess(number) : NotANumber(_text);
        SetAdvice(advice);
        if (advice.IsError)
        {
            _hasRejectedText = true;
            return;
        }

        _hasRejectedText = false;
        _commit(number);
    }

    private void SetText(string text)
    {
        _showing = true;
        try
        {
            Text = text;
        }
        finally
        {
            _showing = false;
        }
    }

    private void SetAdvice(ContextAdvice advice)
    {
        if (_advice == advice)
        {
            return;
        }

        _advice = advice;
        OnPropertyChanged(nameof(AdviceText));
        OnPropertyChanged(nameof(AdviceLevel));
        OnPropertyChanged(nameof(HasAdvice));
        OnPropertyChanged(nameof(HasError));
    }

    private static ContextAdvice NotANumber(string text) => new(
        ContextAdviceLevel.Error,
        string.IsNullOrWhiteSpace(text) ? "Enter a whole number." : "Enter a whole number, without letters or a decimal point.");

    /// <summary>Formats <paramref name="value"/> as the field shows it, with its groups of digits apart.</summary>
    public static string Format(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>
    /// Reads <paramref name="text"/> as a whole number, ignoring commas, spaces and non-breaking spaces between groups
    /// of digits. A number too large for an <see cref="int"/> reads as the largest one, which every field refuses as out
    /// of range. Returns <see langword="false"/> for text that is not a whole number.
    /// </summary>
    public static bool TryParse(string text, out int number)
    {
        number = 0;
        var digits = new string(text.Where(letter => letter is not (',' or ' ' or ' ' or ' ')).ToArray());
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        number = digits.Length <= 10 && long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) is var value && value <= int.MaxValue
            ? (int)value
            : int.MaxValue;
        return true;
    }
}
