using Assistant.Core.Domain;
using Assistant.Core.Settings;

namespace Assistant.Core.Permissions;

/// <summary>Reading and setting the switches of <see cref="PermissionSettings"/> by <see cref="PermissionCapability"/>.</summary>
public static class PermissionSettingsExtensions
{
    /// <summary>The switch of <paramref name="capability"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is not a known capability.</exception>
    public static bool IsOn(this PermissionSettings settings, PermissionCapability capability)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return capability switch
        {
            PermissionCapability.Files => settings.Files,
            PermissionCapability.ScreenCapture => settings.ScreenCapture,
            PermissionCapability.SelectedText => settings.SelectedText,
            PermissionCapability.SelectedTextByCopy => settings.SelectedTextByCopy,
            PermissionCapability.ClipboardHistory => settings.ClipboardHistory,
            PermissionCapability.Calendar => settings.Calendar,
            PermissionCapability.Messaging => settings.Messaging,
            PermissionCapability.ExternalSearch => settings.ExternalSearch,
            PermissionCapability.DestructiveActions => settings.DestructiveActions,
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Not a known capability."),
        };
    }

    /// <summary><paramref name="settings"/> with the switch of <paramref name="capability"/> set to <paramref name="on"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is not a known capability.</exception>
    public static PermissionSettings With(this PermissionSettings settings, PermissionCapability capability, bool on)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return capability switch
        {
            PermissionCapability.Files => settings with { Files = on },
            PermissionCapability.ScreenCapture => settings with { ScreenCapture = on },
            PermissionCapability.SelectedText => settings with { SelectedText = on },
            PermissionCapability.SelectedTextByCopy => settings with { SelectedTextByCopy = on },
            PermissionCapability.ClipboardHistory => settings with { ClipboardHistory = on },
            PermissionCapability.Calendar => settings with { Calendar = on },
            PermissionCapability.Messaging => settings with { Messaging = on },
            PermissionCapability.ExternalSearch => settings with { ExternalSearch = on },
            PermissionCapability.DestructiveActions => settings with { DestructiveActions = on },
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Not a known capability."),
        };
    }

    /// <summary>
    /// What the user has chosen for <paramref name="capability"/>: off, asked each time, or allowed. It is only the choice as saved; whether the Assistant may
    /// use the capability is <c>SettingsPermissionPolicy.Decide</c>. A capability whose switch is on and is marked to ask but cannot ask
    /// (<see cref="PermissionDefinition.SupportsAskEveryTime"/>) reads as <see cref="PermissionMode.Off"/>, since a use that cannot be asked about is not allowed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is not a known capability.</exception>
    public static PermissionMode ModeOf(this PermissionSettings settings, PermissionCapability capability)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsOn(capability))
        {
            return PermissionMode.Off;
        }

        if (!AsksEveryTime(settings.Ask ?? new PermissionAskSettings(), capability))
        {
            return PermissionMode.Allowed;
        }

        return PermissionCatalog.Get(capability).SupportsAskEveryTime ? PermissionMode.AskEveryTime : PermissionMode.Off;
    }

    /// <summary>
    /// <paramref name="settings"/> with <paramref name="capability"/> set to <paramref name="mode"/>: the switch is on for <see cref="PermissionMode.AskEveryTime"/>
    /// and <see cref="PermissionMode.Allowed"/>, and the capability asks only for the first. Asking is not offered to a capability that cannot ask, and is then refused.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is not a known capability, or <paramref name="mode"/> is not a mode.</exception>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is <see cref="PermissionMode.AskEveryTime"/> and the capability cannot ask.</exception>
    public static PermissionSettings WithMode(this PermissionSettings settings, PermissionCapability capability, PermissionMode mode)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a known mode.");
        }

        if (mode == PermissionMode.AskEveryTime && !PermissionCatalog.Get(capability).SupportsAskEveryTime)
        {
            throw new ArgumentException($"{capability} cannot be asked about each time.", nameof(mode));
        }

        var switched = settings.With(capability, mode != PermissionMode.Off);
        var ask = switched.Ask ?? new PermissionAskSettings();
        var asking = mode == PermissionMode.AskEveryTime;
        return PermissionCatalog.Get(capability).SupportsAskEveryTime
            ? switched with { Ask = WithAsk(ask, capability, asking) }
            : switched;
    }

    private static bool AsksEveryTime(PermissionAskSettings ask, PermissionCapability capability) => capability switch
    {
        PermissionCapability.ScreenCapture => ask.ScreenCapture,
        PermissionCapability.SelectedText => ask.SelectedText,
        PermissionCapability.SelectedTextByCopy => ask.SelectedTextByCopy,
        PermissionCapability.Calendar => ask.Calendar,
        PermissionCapability.Messaging => ask.Messaging,
        PermissionCapability.ExternalSearch => ask.ExternalSearch,
        _ => false,
    };

    private static PermissionAskSettings WithAsk(PermissionAskSettings ask, PermissionCapability capability, bool value) => capability switch
    {
        PermissionCapability.ScreenCapture => ask with { ScreenCapture = value },
        PermissionCapability.SelectedText => ask with { SelectedText = value },
        PermissionCapability.SelectedTextByCopy => ask with { SelectedTextByCopy = value },
        PermissionCapability.Calendar => ask with { Calendar = value },
        PermissionCapability.Messaging => ask with { Messaging = value },
        PermissionCapability.ExternalSearch => ask with { ExternalSearch = value },
        _ => ask,
    };
}
