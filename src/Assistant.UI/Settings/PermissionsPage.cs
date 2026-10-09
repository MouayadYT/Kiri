using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Assistant.UI.Controls;

namespace Assistant.UI.Settings;

/// <summary>One of the choices of a permission that can ask: the mode and the words the user sees for it.</summary>
/// <param name="Mode">What it stands for.</param>
/// <param name="Label">The words: Off, Ask every time, Allowed.</param>
public sealed record PermissionChoice(PermissionMode Mode, string Label);

/// <summary>
/// One row of the Permissions page: a capability, what allowing it lets the Assistant do, and its choice. A capability whose uses can be asked about one by one
/// (<see cref="PermissionDefinition.SupportsAskEveryTime"/>) has three choices, Off, Ask every time and Allowed; the others are a switch. The choice of a
/// capability the Assistant cannot do yet, or refuses to do, is off and cannot be changed: the row says why, so a control that cannot do anything never looks as if it could.
/// </summary>
public sealed class PermissionItem : NotifyingObject
{
    private static readonly PermissionChoice[] AllChoices =
    [
        new(PermissionMode.Off, "Off"), new(PermissionMode.AskEveryTime, "Ask every time"), new(PermissionMode.Allowed, "Allowed"),
    ];

    private readonly Action<PermissionCapability, PermissionMode> _commit;
    private PermissionMode _mode;

    internal PermissionItem(PermissionDefinition definition, bool showsDivider, Action<PermissionCapability, PermissionMode> commit)
    {
        Definition = definition;
        ShowsDivider = showsDivider;
        _commit = commit;
        Status = definition.Availability switch
        {
            PermissionAvailability.Available => SettingStatus.Available,
            PermissionAvailability.Planned => SettingStatus.ComingLater,
            PermissionAvailability.NotInThisVersion => SettingStatus.NotInThisVersion,
            _ => SettingStatus.AlwaysOff,
        };
    }

    /// <summary>What the capability is and whether this build can do it.</summary>
    public PermissionDefinition Definition { get; }

    /// <summary>The capability.</summary>
    public PermissionCapability Capability => Definition.Capability;

    /// <summary>The capability's name.</summary>
    public string Title => Definition.Title;

    /// <summary>What allowing it lets the Assistant do.</summary>
    public string Summary => Definition.Summary;

    /// <summary>Whether the user can change it: the Assistant can do it now.</summary>
    public bool IsAvailable => Definition.IsAvailable;

    /// <summary>Whether the user can choose to be asked each time, so the row shows three choices and not a switch.</summary>
    public bool SupportsAskEveryTime => Definition.IsAvailable && Definition.SupportsAskEveryTime;

    /// <summary>Whether the row shows a switch: its uses cannot be asked about one by one.</summary>
    public bool ShowsSwitch => !SupportsAskEveryTime;

    /// <summary>The choices, in order, for a row that can ask; empty for one that is a switch.</summary>
    public IReadOnlyList<PermissionChoice> Choices => SupportsAskEveryTime ? AllChoices : [];

    /// <summary>What the row says about whether its switch can be used.</summary>
    public SettingStatus Status { get; }

    /// <summary>Whether a line is drawn above the row, which every row of a group has but its first.</summary>
    public bool ShowsDivider { get; }

    /// <summary>
    /// What the user has chosen and the Assistant may do: off, asked each time, or allowed. Setting it saves the choice at once; setting it on a capability that is not
    /// available, or to asking on one that cannot ask, does nothing.
    /// </summary>
    public PermissionMode Mode
    {
        get => _mode;
        set
        {
            if (!IsAvailable || value == PermissionMode.AskEveryTime && !SupportsAskEveryTime || !Enum.IsDefined(value))
            {
                // A choice that cannot move stays where it is, even if something other than a click tried to move it.
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedChoice));
                OnPropertyChanged(nameof(IsOn));
                return;
            }

            if (Set(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsOn));
                OnPropertyChanged(nameof(SelectedChoice));
                _commit(Capability, value);
            }
        }
    }

    /// <summary>Whether the Assistant may use the capability without asking: it can, and the user allows it. Setting it saves the choice at once.</summary>
    public bool IsOn
    {
        get => _mode == PermissionMode.Allowed;
        set => Mode = value ? PermissionMode.Allowed : PermissionMode.Off;
    }

    /// <summary>The choice that is made, for the list of choices.</summary>
    public PermissionChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(choice => choice.Mode == _mode);
        set
        {
            if (value is not null)
            {
                Mode = value.Mode;
            }
        }
    }

    /// <summary>Shows what is chosen, as saved. Nothing is saved.</summary>
    internal void Show(PermissionMode mode)
    {
        // Announced even when nothing changed: a control that was moved and refused must read the real value again.
        _mode = mode;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(SelectedChoice));
    }
}

/// <summary>
/// An action the user answered "Always allow" for when the Assistant asked (step 115): what it is in words, and the way to be asked about it again.
/// </summary>
public sealed class AlwaysAllowedItem
{
    internal AlwaysAllowedItem(string tool, Action<string> askAgain)
    {
        Tool = tool;
        Title = Assistant.Core.Audit.AuditText.HasPhrase(tool) ? Assistant.Core.Audit.AuditText.ToolPhrase(tool) : "A connected app: " + tool.Replace('_', ' ');
        AskAgainCommand = new ViewModels.RelayCommand(_ => askAgain(tool));
    }

    /// <summary>The name of the tool that does it.</summary>
    public string Tool { get; }

    /// <summary>What it is, in words: "Start a timer".</summary>
    public string Title { get; }

    /// <summary>What the button says to assistive technology.</summary>
    public string AskAgainName => "Ask again before: " + Title;

    /// <summary>Has the Assistant ask about this action again, from the next time on.</summary>
    public System.Windows.Input.ICommand AskAgainCommand { get; }
}

/// <summary>A titled group of permissions on the Permissions page.</summary>
/// <param name="Title">The group's heading.</param>
/// <param name="Items">Its rows, in order.</param>
public sealed record PermissionGroup(string Title, IReadOnlyList<PermissionItem> Items);

/// <summary>
/// Permissions: what the user allows the Assistant to use (PROJECT_SPEC §4.9), one switch for each
/// <see cref="PermissionCapability"/> in three groups: a switch, or, for a capability whose uses can be asked about one by one, Off, Ask every time and Allowed. Only the capabilities
/// the Assistant can do now (<see cref="PermissionCatalog"/>) can be changed; the others are listed as they will be and stay off. What the page says is what holds: the services
/// that read a file, the screen or a selection, and the tools that use them, ask <c>IPermissionPolicy</c>, which reads the same saved setting, and a use that is set to ask is asked about.
/// </summary>
public sealed class PermissionsPage : SettingsPage
{
    // How the capabilities are grouped. A capability missing here would not be shown, so a test checks that each is listed once.
    private static readonly (string Title, PermissionCapability[] Capabilities)[] Layout =
    [
        ("On this PC", [PermissionCapability.Files, PermissionCapability.ScreenCapture, PermissionCapability.SelectedText, PermissionCapability.SelectedTextByCopy, PermissionCapability.ClipboardHistory]),
        ("Other apps and the web", [PermissionCapability.Calendar, PermissionCapability.Messaging, PermissionCapability.ExternalSearch]),
        ("Actions", [PermissionCapability.DestructiveActions]),
    ];

    internal PermissionsPage(SettingsViewModel root)
        : base(root, SettingsSection.Permissions)
    {
        Groups =
        [
            .. Layout.Select(group => new PermissionGroup(
                group.Title,
                [.. group.Capabilities.Select((capability, index) => new PermissionItem(PermissionCatalog.Get(capability), index > 0, Change))])),
        ];
        Items = [.. Groups.SelectMany(group => group.Items)];
    }

   /// <summary>The groups, in order.</summary>
    public IReadOnlyList<PermissionGroup> Groups { get; }

    /// <summary>Every row of every group, in order.</summary>
    public IReadOnlyList<PermissionItem> Items { get; }

    /// <summary>The row of <paramref name="capability"/>.</summary>
    public PermissionItem this[PermissionCapability capability] => Items.Single(item => item.Capability == capability);

    /// <summary>The actions the user answered "Always allow" for, each with the way to be asked again.</summary>
    public System.Collections.ObjectModel.ObservableCollection<AlwaysAllowedItem> AlwaysAllowed { get; } = [];

    /// <summary>Whether there are any.</summary>
    public bool HasAlwaysAllowed => AlwaysAllowed.Count > 0;

    internal override void Apply(AppSettings settings, bool fresh)
    {
        var kept = (settings.Permissions.AlwaysAllowed ?? [])
            .Where(Assistant.Core.Confirmation.StandingApprovals.IsWellFormed)
            .Select(Assistant.Core.Confirmation.StandingApprovals.ToolOf)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!kept.SequenceEqual(AlwaysAllowed.Select(item => item.Tool), StringComparer.Ordinal))
        {
            AlwaysAllowed.Clear();
            foreach (var tool in kept)
            {
                AlwaysAllowed.Add(new AlwaysAllowedItem(tool, AskAgain));
            }

            OnPropertyChanged(nameof(HasAlwaysAllowed));
        }

        foreach (var item in Items)
        {
            item.Show(SettingsPermissionPolicy.Decide(settings.Permissions, item.Capability).Reason switch
            {
                PermissionDecisionReason.Granted => PermissionMode.Allowed,
                PermissionDecisionReason.AskEveryTime => PermissionMode.AskEveryTime,
                _ => PermissionMode.Off,
            });
        }
    }

    private void Change(PermissionCapability capability, PermissionMode mode) =>
        Commit(settings => settings with { Permissions = settings.Permissions.WithMode(capability, mode) });

    private void AskAgain(string tool) =>
        Commit(settings => settings with { Permissions = Assistant.Core.Confirmation.StandingApprovals.Forget(settings.Permissions, tool) });
}
