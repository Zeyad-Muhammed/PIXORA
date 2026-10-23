namespace PIXORA.Core;

[Flags]
public enum HotkeyModifiers : uint { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

public static class HotkeyParser
{
    /// <summary>Parses text such as "Ctrl+Shift+S" into modifiers and a Win32 virtual-key code. Empty text means unassigned.</summary>
    public static bool TryParse(string? text, out HotkeyModifiers mods, out uint vk)
    {
        mods = HotkeyModifiers.None; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= HotkeyModifiers.Ctrl; break;
                case "ALT": mods |= HotkeyModifiers.Alt; break;
                case "SHIFT": mods |= HotkeyModifiers.Shift; break;
                case "WIN" or "WINDOWS": mods |= HotkeyModifiers.Win; break;
                default: return false;
            }
        }
        var key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' || key[0] is >= '0' and <= '9')) { vk = key[0]; return true; }
        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24)
        { vk = (uint)(0x70 + f - 1); return true; }
        switch (key)
        {
            case "PRINTSCREEN" or "PRTSC": vk = 0x2C; return true;
            case "SPACE": vk = 0x20; return true;
            default: return false;
        }
    }
}
