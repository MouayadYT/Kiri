using System.Globalization;

namespace Assistant.Windows.Hotkeys;

/// <summary>Converts settings key names to Win32 virtual keys without a desktop UI dependency.</summary>
internal static class HotkeyKey
{
    public static bool TryParse(string? name, out uint key)
    {
        key = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = name.ToUpperInvariant();
        if (normalized.Length == 1 && (normalized[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            key = normalized[0];
            return true;
        }

        if (normalized.StartsWith('F') && int.TryParse(normalized.AsSpan(1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var function) && function is >= 1 and <= 24)
        {
            key = (uint)(0x70 + function - 1);
            return true;
        }

        key = normalized switch
        {
            "BACKSPACE" => 0x08, "TAB" => 0x09, "ENTER" => 0x0D, "ESCAPE" => 0x1B,
            "SPACE" => 0x20, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, "END" => 0x23,
            "HOME" => 0x24, "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27,
            "DOWN" => 0x28, "INSERT" => 0x2D, "DELETE" => 0x2E,
            _ => 0,
        };
        return key != 0;
    }
}
