namespace Assistant.Windows.Tray;

/// <summary>One line of the menu the notification-area icon opens.</summary>
/// <param name="Id">What <see cref="INotificationAreaIcon.CommandInvoked"/> says when the user chooses it. Not zero, which Windows uses for no choice.</param>
/// <param name="Text">The words on the line.</param>
public sealed record TrayMenuItem(int Id, string Text)
{
    /// <summary>The thin line between groups of commands.</summary>
    public static TrayMenuItem Separator { get; } = new(0, "");

    /// <summary>Whether the line shows a check mark.</summary>
    public bool IsChecked { get; init; }

    /// <summary>Whether the line is drawn in bold: the command a click on the icon itself does.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Whether the line can be chosen. A line that cannot is dimmed.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Whether this is a <see cref="Separator"/>.</summary>
    public bool IsSeparator => Id == 0;
}
