using System.Text.Json.Serialization;

namespace Assistant.Core.Settings;

/// <summary>Modifier keys of a <see cref="Hotkey"/>. The values match the Win32 <c>MOD_*</c> flags.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<HotkeyModifiers>))]
public enum HotkeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Either Alt key.</summary>
    Alt = 1,

    /// <summary>Either Ctrl key.</summary>
    Control = 2,

    /// <summary>Either Shift key.</summary>
    Shift = 4,

    /// <summary>Either Windows key.</summary>
    Windows = 8,
}
