namespace Assistant.Core.Budgeting;

/// <summary>What fitting the prompt into the model's window did to a piece of context.</summary>
public enum ContextFate
{
    /// <summary>It is sent whole.</summary>
    Whole = 0,

    /// <summary>It is sent cut short: the start of it.</summary>
    Shortened = 1,

    /// <summary>It does not fit and is not sent.</summary>
    LeftOut = 2,
}
