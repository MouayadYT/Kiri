using System.Windows;
using System.Windows.Controls;

namespace Assistant.UI.Controls;

/// <summary>Whether a setting can be changed now, and, when it cannot or takes a while, what to tell the user.</summary>
public enum SettingStatus
{
    /// <summary>It can be changed and takes effect at once.</summary>
    Available,

    /// <summary>The feature it belongs to is a later step: the control shows what is saved, cannot be changed, and says so.</summary>
    ComingLater,

    /// <summary>It can be changed and takes effect the next time the Assistant starts.</summary>
    AtNextStart,

    /// <summary>It can be changed and takes effect the next time the local model is loaded.</summary>
    AtNextModelLoad,

    /// <summary>The feature is left out of this version: the control shows that it is off, cannot be changed, and says so.</summary>
    NotInThisVersion,

    /// <summary>The Assistant refuses to do it by design in this version: the control is off, cannot be changed, and says so.</summary>
    AlwaysOff,
}

/// <summary>Where a row puts its control.</summary>
public enum SettingLayout
{
    /// <summary>At the right of the row's text.</summary>
    Inline,

    /// <summary>Under the row's text, for a control too wide to sit beside it.</summary>
    Stacked,
}

/// <summary>
/// One row of a settings group: a title and a sentence on the left, its control (the row's content) on the right, and,
/// under both, a footer such as advice about what was typed. A row whose <see cref="Status"/> is
/// <see cref="SettingStatus.ComingLater"/>, <see cref="SettingStatus.NotInThisVersion"/> or <see cref="SettingStatus.AlwaysOff"/>
/// disables its control, dims the row and labels it, so a control that cannot do anything never looks as if it could.
/// </summary>
public sealed class SettingsRow : ContentControl
{
    /// <summary>Identifies the <see cref="Header"/> property.</summary>
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Description"/> property.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty, OnDescriptionChanged));

    /// <summary>Identifies the <see cref="Footer"/> property.</summary>
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(object), typeof(SettingsRow), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="Status"/> property.</summary>
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(SettingStatus), typeof(SettingsRow), new PropertyMetadata(SettingStatus.Available, OnStatusChanged));

    /// <summary>Identifies the <see cref="Layout"/> property.</summary>
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(SettingLayout), typeof(SettingsRow), new PropertyMetadata(SettingLayout.Inline));

    private static readonly DependencyPropertyKey StatusTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(StatusText), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey IsControlEnabledPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsControlEnabled), typeof(bool), typeof(SettingsRow), new PropertyMetadata(true));

    private static readonly DependencyPropertyKey HasDescriptionPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasDescription), typeof(bool), typeof(SettingsRow), new PropertyMetadata(false));

    /// <summary>Identifies the <see cref="StatusText"/> property.</summary>
    public static readonly DependencyProperty StatusTextProperty = StatusTextPropertyKey.DependencyProperty;

    /// <summary>Identifies the <see cref="IsControlEnabled"/> property.</summary>
    public static readonly DependencyProperty IsControlEnabledProperty = IsControlEnabledPropertyKey.DependencyProperty;

    /// <summary>Identifies the <see cref="HasDescription"/> property.</summary>
    public static readonly DependencyProperty HasDescriptionProperty = HasDescriptionPropertyKey.DependencyProperty;

    static SettingsRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingsRow), new FrameworkPropertyMetadata(typeof(SettingsRow)));
        FocusableProperty.OverrideMetadata(typeof(SettingsRow), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SettingsRow), new FrameworkPropertyMetadata(false));
    }

    /// <summary>The setting's name.</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>A sentence that says what it does.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>What is shown under the row's text and control, across its whole width, such as advice.</summary>
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>Whether the setting can be changed now.</summary>
    public SettingStatus Status
    {
        get => (SettingStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Where the row puts its control.</summary>
    public SettingLayout Layout
    {
        get => (SettingLayout)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>The label that goes with <see cref="Status"/>, or empty when there is none.</summary>
    public string StatusText => (string)GetValue(StatusTextProperty);

    /// <summary>
    /// Whether the row's control can be used: not while the setting is <see cref="SettingStatus.ComingLater"/>,
    /// <see cref="SettingStatus.NotInThisVersion"/> or <see cref="SettingStatus.AlwaysOff"/>.
    /// </summary>
    public bool IsControlEnabled => (bool)GetValue(IsControlEnabledProperty);

    /// <summary>Whether there is a <see cref="Description"/>.</summary>
    public bool HasDescription => (bool)GetValue(HasDescriptionProperty);

    /// <summary>The label a status is shown with.</summary>
    public static string Label(SettingStatus status) => status switch
    {
        SettingStatus.ComingLater => "Coming later",
        SettingStatus.AtNextStart => "Applies at next start",
        SettingStatus.AtNextModelLoad => "Applies when the model next loads",
        SettingStatus.NotInThisVersion => "Not in this version",
        SettingStatus.AlwaysOff => "Always off",
        _ => string.Empty,
    };

    private static void OnStatusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var row = (SettingsRow)sender;
        var status = (SettingStatus)e.NewValue;
        row.SetValue(StatusTextPropertyKey, Label(status));
        row.SetValue(
            IsControlEnabledPropertyKey,
            status is not (SettingStatus.ComingLater or SettingStatus.NotInThisVersion or SettingStatus.AlwaysOff));
    }

    private static void OnDescriptionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        sender.SetValue(HasDescriptionPropertyKey, !string.IsNullOrEmpty((string?)e.NewValue));
}

/// <summary>Attached state for a field in the settings window.</summary>
public static class SettingsField
{
    /// <summary>Identifies the IsInvalid attached property.</summary>
    public static readonly DependencyProperty IsInvalidProperty = DependencyProperty.RegisterAttached(
        "IsInvalid", typeof(bool), typeof(SettingsField), new PropertyMetadata(false));

    /// <summary>Gets whether the field holds something that cannot be saved, so it is outlined as a problem.</summary>
    public static bool GetIsInvalid(DependencyObject element) => (bool)element.GetValue(IsInvalidProperty);

    /// <summary>Sets whether the field holds something that cannot be saved.</summary>
    public static void SetIsInvalid(DependencyObject element, bool value) => element.SetValue(IsInvalidProperty, value);
}
