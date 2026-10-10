using System.Globalization;

namespace XControl.Core;

/// <summary>Every virtual Xbox control a physical input can be bound to.</summary>
public enum PadControl
{
    A, B, X, Y,
    DpadUp, DpadDown, DpadLeft, DpadRight,
    LeftBumper, RightBumper,
    LeftTrigger, RightTrigger,
    LeftStickClick, RightStickClick,
    Menu, View,
    LeftStickUp, LeftStickDown, LeftStickLeft, LeftStickRight,
    RightStickUp, RightStickDown, RightStickLeft, RightStickRight,
}

/// <summary>Bit values identical to the XInput button mask.</summary>
[Flags]
public enum PadButtons : ushort
{
    None = 0,
    DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008,
    Menu = 0x0010, View = 0x0020,
    LeftStickClick = 0x0040, RightStickClick = 0x0080,
    LeftBumper = 0x0100, RightBumper = 0x0200,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

public readonly record struct PadState(
    PadButtons Buttons, byte LeftTrigger, byte RightTrigger,
    short LeftX, short LeftY, short RightX, short RightY)
{
    public static readonly PadState Neutral = default;
    public bool IsNeutral => this == default;
}

public enum InputKind { Key, Mouse }

/// <summary>A physical input: a keyboard virtual-key or a mouse button / wheel direction.</summary>
public readonly record struct InputId(InputKind Kind, int Code)
{
    public const int MouseLeft = 1, MouseRight = 2, MouseMiddle = 3, MouseX1 = 4, MouseX2 = 5,
        WheelUp = 6, WheelDown = 7;

    public static InputId Key(int vk) => new(InputKind.Key, NormalizeVk(vk));
    public static InputId Mouse(int code) => new(InputKind.Mouse, code);

    /// <summary>Left/right Shift, Ctrl and Alt collapse to the generic virtual key.</summary>
    public static int NormalizeVk(int vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => vk,
    };

    public override string ToString() =>
        Kind == InputKind.Key ? "Key:" + InputNames.KeyName(Code) : "Mouse:" + InputNames.MouseName(Code);

    public string Display => Kind == InputKind.Key ? InputNames.KeyName(Code) : InputNames.MouseName(Code);

    public static bool TryParse(string? text, out InputId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(':', 2);
        if (parts.Length != 2) return false;
        if (parts[0].Equals("Key", StringComparison.OrdinalIgnoreCase) && InputNames.TryKeyCode(parts[1], out var vk))
        { id = new InputId(InputKind.Key, vk); return true; }
        if (parts[0].Equals("Mouse", StringComparison.OrdinalIgnoreCase) && InputNames.TryMouseCode(parts[1], out var mc))
        { id = new InputId(InputKind.Mouse, mc); return true; }
        return false;
    }
}

public static class InputNames
{
    static readonly Dictionary<int, string> Special = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt",
        [0x13] = "Pause", [0x14] = "CapsLock", [0x1B] = "Escape", [0x20] = "Space",
        [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x5B] = "LWin", [0x5C] = "RWin",
        [0x90] = "NumLock", [0x91] = "ScrollLock",
        [0xBA] = "Semicolon", [0xBB] = "Equals", [0xBC] = "Comma", [0xBD] = "Minus", [0xBE] = "Period",
        [0xBF] = "Slash", [0xC0] = "Backtick", [0xDB] = "LeftBracket", [0xDC] = "Backslash",
        [0xDD] = "RightBracket", [0xDE] = "Quote",
        [0x6A] = "Num*", [0x6B] = "Num+", [0x6D] = "Num-", [0x6E] = "Num.", [0x6F] = "Num/",
    };

    static readonly Dictionary<string, int> Reverse = BuildReverse();

    static Dictionary<string, int> BuildReverse()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Special) d[v] = k;
        return d;
    }

    public static string KeyName(int vk)
    {
        if (Special.TryGetValue(vk, out var s)) return s;
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60);
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        return "VK" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    public static bool TryKeyCode(string name, out int vk)
    {
        vk = 0;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) { vk = char.ToUpperInvariant(name[0]); return true; }
        if (Reverse.TryGetValue(name, out vk)) return true;
        if (name.Length > 1 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 24)
        { vk = 0x6F + f; return true; }
        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsAsciiDigit(name[3]))
        { vk = 0x60 + (name[3] - '0'); return true; }
        if (name.StartsWith("VK", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk) && vk is > 0 and < 256) return true;
        return false;
    }

    public static string MouseName(int code) => code switch
    {
        InputId.MouseLeft => "Left",
        InputId.MouseRight => "Right",
        InputId.MouseMiddle => "Middle",
        InputId.MouseX1 => "Side1",
        InputId.MouseX2 => "Side2",
        InputId.WheelUp => "WheelUp",
        InputId.WheelDown => "WheelDown",
        _ => "Button" + code,
    };

    public static bool TryMouseCode(string name, out int code)
    {
        for (var c = 1; c <= 7; c++)
            if (MouseName(c).Equals(name, StringComparison.OrdinalIgnoreCase)) { code = c; return true; }
        code = 0;
        return false;
    }
}
