using Raylib_cs;

namespace Pre2Port.Game.Options;

public enum PadType { Compatible, Sony, Microsoft }

/// <summary>
/// Single place for gamepad classification and button names (ESC overlay > Controller).
/// The class comes from the HID vendor id of the Raw Input device associated with the Raylib pad (see <see cref="RawInputPads"/>), falling back to the Raylib device name (<c>GetGamepadName</c>); no name -> Compatible. Button indices are Raylib's GamepadButton (1..17);
/// only the displayed name changes with the class, never the index stored in Controller.cfg.
/// </summary>
public static class PadInfo
{
    static readonly string[] SonyIds = { "wireless controller", "playstation", "dualshock", "dualsense", "ps3", "ps4", "ps5", "054c" };
    static readonly string[] MsIds = { "xbox", "x-box", "xinput", "045e" };

    /// <summary>Class from the device name (case-insensitive substring match); null/empty/unknown -> Compatible.</summary>
    public static PadType Detect(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return PadType.Compatible;
        string n = name.ToLowerInvariant();
        foreach (var s in SonyIds) if (n.Contains(s)) return PadType.Sony;
        foreach (var s in MsIds) if (n.Contains(s)) return PadType.Microsoft;
        return PadType.Compatible;
    }

    public const ushort VendorSony = 0x054C, VendorMicrosoft = 0x045E;

    /// <summary>HID vendor id -> class; null when the vendor is neither Sony nor Microsoft (the name decides then).</summary>
    public static PadType? FromVendor(ushort vid) => vid == VendorSony ? PadType.Sony : vid == VendorMicrosoft ? PadType.Microsoft : null;

    static string Norm(string s) => string.Join(' ', s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Associates a Raylib pad name (GLFW: the DirectInput product name) with Raw Input devices by comparing it with each HID product string
    /// (equal, or one contains the other; both at least 4 chars). Returns the matching devices; the vendor is only trusted when all of them agree.
    /// </summary>
    public static HidPad[] Associate(string? raylibName, IEnumerable<HidPad> devices)
    {
        if (string.IsNullOrWhiteSpace(raylibName)) return Array.Empty<HidPad>();
        string a = Norm(raylibName);
        return devices.Where(d =>
        {
            string b = Norm(d.ProductName);
            return a.Length >= 4 && b.Length >= 4 && (a == b || a.Contains(b) || b.Contains(a));
        }).ToArray();
    }

    /// <summary>
    /// Class of a pad: the HID vendor id of the associated Raw Input device when it is Sony (0x054C) or Microsoft (0x045E); otherwise (no association,
    /// ambiguous association, other vendor) the device name via <see cref="Detect"/>.
    /// </summary>
    public static PadType Classify(string? raylibName, IEnumerable<HidPad> devices, out bool byVendor)
    {
        byVendor = false;
        var m = Associate(raylibName, devices);
        if (m.Length > 0 && m.Select(d => d.VendorId).Distinct().Count() == 1)
        {
            var t = FromVendor(m[0].VendorId);
            if (t is not null) { byVendor = true; return t.Value; }
        }
        return Detect(raylibName);
    }

    public static string Label(PadType t) => t switch { PadType.Sony => "SONY", PadType.Microsoft => "MICROSOFT", _ => "COMPATIBLE" };

    /// <summary>Menu device name: "Joy Microsoft #1", "Joy Sony #2", "Compatible #3" (number = Raylib slot + 1).</summary>
    public static string PadLabel(PadType t, int slot) => t switch
    {
        PadType.Sony => $"Joy Sony #{slot + 1}",
        PadType.Microsoft => $"Joy Microsoft #{slot + 1}",
        _ => $"Compatible #{slot + 1}",
    };

    public static string ButtonName(GamepadButton b, PadType t) => t switch
    {
        PadType.Sony => SonyButtonName(b),
        PadType.Microsoft => XButtonName(b),
        _ => "BTN " + (int)b,
    };

    public static string SonyButtonName(GamepadButton b) => b switch
    {
        GamepadButton.RightFaceDown => "CROSS",
        GamepadButton.RightFaceRight => "CIRCLE",
        GamepadButton.RightFaceLeft => "SQUARE",
        GamepadButton.RightFaceUp => "TRIANGLE",
        GamepadButton.LeftTrigger1 => "L1",
        GamepadButton.LeftTrigger2 => "L2",
        GamepadButton.RightTrigger1 => "R1",
        GamepadButton.RightTrigger2 => "R2",
        GamepadButton.MiddleLeft => "SHARE",
        GamepadButton.Middle => "PS",
        GamepadButton.MiddleRight => "OPTIONS",
        GamepadButton.LeftThumb => "L3",
        GamepadButton.RightThumb => "R3",
        _ => DPadName(b) ?? "BTN " + (int)b,
    };

    public static string XButtonName(GamepadButton b) => b switch
    {
        GamepadButton.RightFaceDown => "A",
        GamepadButton.RightFaceRight => "B",
        GamepadButton.RightFaceLeft => "X",
        GamepadButton.RightFaceUp => "Y",
        GamepadButton.LeftTrigger1 => "LB",
        GamepadButton.LeftTrigger2 => "LT",
        GamepadButton.RightTrigger1 => "RB",
        GamepadButton.RightTrigger2 => "RT",
        GamepadButton.MiddleLeft => "BACK",
        GamepadButton.Middle => "VIEW",
        GamepadButton.MiddleRight => "MENU",
        GamepadButton.LeftThumb => "L3",
        GamepadButton.RightThumb => "R3",
        _ => DPadName(b) ?? "BTN " + (int)b,
    };

    static string? DPadName(GamepadButton b) => b switch
    {
        GamepadButton.LeftFaceUp => "UP",
        GamepadButton.LeftFaceRight => "RIGHT",
        GamepadButton.LeftFaceDown => "DOWN",
        GamepadButton.LeftFaceLeft => "LEFT",
        _ => null,
    };

    // ---------------------------------------------------------------- Raylib queries (need the window open)
    public static PadType TypeOfSlot(int slot) => Raylib.IsGamepadAvailable(slot) ? Classify(Raylib.GetGamepadName_(slot), RawInputPads.Enumerate(), out _) : PadType.Compatible;

    /// <summary>Connected gamepads (Raylib slots 0..3): (slot, class, menu label).</summary>
    public static List<(int Index, PadType Type, string Text)> DetectPads()
    {
        var l = new List<(int, PadType, string)>();
        for (int i = 0; i < 4; i++)
            if (Raylib.IsGamepadAvailable(i))
            {
                var t = Classify(Raylib.GetGamepadName_(i), RawInputPads.Enumerate(), out _);
                l.Add((i, t, PadLabel(t, i)));
            }
        return l;
    }
}
