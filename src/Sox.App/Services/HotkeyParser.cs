using Windows.System;

namespace Sox.App.Services;

/// <summary>
/// Parses and formats the flat hotkey text the settings page records ("Ctrl+Shift+D", "Alt+Space").
/// One modifier plus one key minimum, so a hotkey is always a real combination. Mirrors the recorder
/// used by the hotkey page; kept separate from the Win32 registration in <see cref="HotkeyService"/>.
/// </summary>
internal static class HotkeyParser
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                    modifiers |= ModWin;
                    break;
                default:
                    if (virtualKey != 0 || !TryKeyToVk(raw, out virtualKey))
                        return false;
                    break;
            }
        }

        // Require at least one modifier and one key: a bare key would swallow ordinary typing.
        return modifiers != 0 && virtualKey != 0;
    }

    private static bool TryKeyToVk(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z')
            {
                vk = c;
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }

        if (key.Length >= 2 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        vk = key.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "esc" or "escape" => 0x1B,
            "`" or "backtick" => 0xC0,
            "-" => 0xBD,
            "=" => 0xBB,
            "[" => 0xDB,
            "]" => 0xDD,
            "\\" => 0xDC,
            ";" => 0xBA,
            "'" => 0xDE,
            "," => 0xBC,
            "." => 0xBE,
            "/" => 0xBF,
            _ => 0,
        };
        return vk != 0;
    }

    /// <summary>Readable text for a recorded key press, used to fill the settings box.</summary>
    public static string Format(VirtualKey key, bool ctrl, bool alt, bool shift, bool win)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    private static string KeyName(VirtualKey key) => key switch
    {
        VirtualKey.Space => "Space",
        VirtualKey.Tab => "Tab",
        VirtualKey.Enter => "Enter",
        VirtualKey.Back => "Backspace",
        VirtualKey.Delete => "Delete",
        VirtualKey.Insert => "Insert",
        VirtualKey.Home => "Home",
        VirtualKey.End => "End",
        VirtualKey.PageUp => "PageUp",
        VirtualKey.PageDown => "PageDown",
        VirtualKey.Up => "Up",
        VirtualKey.Down => "Down",
        VirtualKey.Left => "Left",
        VirtualKey.Right => "Right",
        VirtualKey.Escape => "Esc",
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (key - VirtualKey.Number0))).ToString(),
        >= VirtualKey.A and <= VirtualKey.Z => key.ToString(),
        >= VirtualKey.F1 and <= VirtualKey.F24 => key.ToString(),
        _ => key.ToString(),
    };
}
