namespace Assistant.Core.Settings;

/// <summary>What is wrong with a setting.</summary>
public enum SettingsIssueKind
{
    /// <summary>A number or time outside the range the app can work with.</summary>
    OutOfRange,

    /// <summary>A value that is not one of the allowed ones, or is not well formed, such as a path that is not complete.</summary>
    Invalid,

    /// <summary>Settings that cannot both hold, such as two shortcuts that are the same.</summary>
    Conflict,
}

/// <summary>One thing wrong with the settings. It names the setting and never holds its value, so it is safe to log.</summary>
/// <param name="Setting">The setting, as <c>Section.Property</c>, such as <c>Model.ContextLength</c>.</param>
/// <param name="Kind">What is wrong with it.</param>
public sealed record SettingsIssue(string Setting, SettingsIssueKind Kind);

/// <summary>Settings that cannot be saved because they hold values the app cannot work with.</summary>
public sealed class SettingsValidationException : ArgumentException
{
    /// <summary>Creates the exception for <paramref name="issues"/>.</summary>
    public SettingsValidationException(IReadOnlyList<SettingsIssue> issues)
        : base("The settings hold values that are not allowed.")
    {
        Issues = issues;
    }

    /// <summary>What is wrong, setting by setting.</summary>
    public IReadOnlyList<SettingsIssue> Issues { get; }
}
