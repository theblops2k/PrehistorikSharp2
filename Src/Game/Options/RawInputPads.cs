using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pre2Port.Game.Options;

/// <summary>A game controller seen by Windows Raw Input: HID vendor/product ids plus the HID product string.</summary>
public readonly record struct HidPad(ushort VendorId, ushort ProductId, string ProductName, string Path);

/// <summary>
/// Windows Raw Input enumeration of HID joysticks/gamepads (GetRawInputDeviceList + GetRawInputDeviceInfo, RIDI_DEVICEINFO -> vendorId/productId).
/// Raylib only gives a name (GLFW: the DirectInput product name, or "XInput ..."), and the Raw Input RIDI_DEVICENAME is a device path
/// (\\?\HID#VID_054C&amp;PID_09CC#...), not a readable name; so the readable HID product string is read with HidD_GetProductString on that path
/// and compared with the Raylib name (see <see cref="PadInfo.Associate"/>). Everything is best effort: any failure -> empty list -> name fallback.
/// </summary>
public static class RawInputPads
{
    const uint RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000B, RIM_TYPEHID = 2;
    const uint HidInfoSize = 32;   // RID_DEVICE_INFO: cbSize, dwType, union(mouse 16 / keyboard 24 / hid 16 bytes) = 32 on 32 and 64 bit

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceList(IntPtr pList, ref uint puiNumDevices, uint cbSize);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, uint bufferLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    static List<HidPad> _cache = new();
    static long _cacheAt = long.MinValue;

    /// <summary>HID joysticks (usage page 1, usage 4) and gamepads (usage 5) known to Raw Input. Cached for 2 s (the menu asks every frame).</summary>
    public static IReadOnlyList<HidPad> Enumerate()
    {
        long now = Environment.TickCount64;
        if (_cacheAt != long.MinValue && now - _cacheAt < 2000) return _cache;
        _cacheAt = now;
        _cache = EnumerateNow();
        return _cache;
    }

    public static List<HidPad> EnumerateNow()
    {
        var res = new List<HidPad>();
        if (!OperatingSystem.IsWindows()) return res;
        try
        {
            uint n = 0, sz = (uint)(IntPtr.Size * 2);   // RAWINPUTDEVICELIST { HANDLE hDevice; DWORD dwType; } padded to 2 pointers
            if (GetRawInputDeviceList(IntPtr.Zero, ref n, sz) == uint.MaxValue || n == 0) return res;
            IntPtr list = Marshal.AllocHGlobal((int)(n * sz));
            try
            {
                uint got = GetRawInputDeviceList(list, ref n, sz);
                if (got == uint.MaxValue) return res;
                for (int i = 0; i < got; i++)
                {
                    IntPtr entry = list + (int)(i * sz);
                    IntPtr hDev = Marshal.ReadIntPtr(entry);
                    if ((uint)Marshal.ReadInt32(entry, IntPtr.Size) != RIM_TYPEHID) continue;
                    var info = ReadInfo(hDev);
                    if (info is null) continue;
                    var (vid, pid, page, usage) = info.Value;
                    if (page != 1 || (usage != 4 && usage != 5)) continue;
                    string path = ReadPath(hDev);
                    res.Add(new HidPad(vid, pid, ReadProductString(path), path));
                }
            }
            finally { Marshal.FreeHGlobal(list); }
        }
        catch (Exception) { res.Clear(); }   // missing DLL / odd driver: name fallback
        return res;
    }

    static (ushort Vid, ushort Pid, ushort Page, ushort Usage)? ReadInfo(IntPtr hDev)
    {
        IntPtr buf = Marshal.AllocHGlobal((int)HidInfoSize);
        try
        {
            for (int i = 0; i < (int)HidInfoSize; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt32(buf, 0, (int)HidInfoSize);
            uint size = HidInfoSize;
            if (GetRawInputDeviceInfoW(hDev, RIDI_DEVICEINFO, buf, ref size) == uint.MaxValue) return null;
            if ((uint)Marshal.ReadInt32(buf, 4) != RIM_TYPEHID) return null;
            // union at +8: dwVendorId, dwProductId, dwVersionNumber, usUsagePage, usUsage
            return ((ushort)Marshal.ReadInt32(buf, 8), (ushort)Marshal.ReadInt32(buf, 12), (ushort)Marshal.ReadInt16(buf, 20), (ushort)Marshal.ReadInt16(buf, 22));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static string ReadPath(IntPtr hDev)
    {
        uint chars = 0;
        GetRawInputDeviceInfoW(hDev, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return "";
        IntPtr buf = Marshal.AllocHGlobal((int)(chars * 2 + 2));
        try
        {
            if (GetRawInputDeviceInfoW(hDev, RIDI_DEVICENAME, buf, ref chars) == uint.MaxValue) return "";
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Readable product string via HidD_GetProductString (opened with access 0: no read/write rights needed, works while the game owns the device).</summary>
    static string ReadProductString(string path)
    {
        if (path.Length == 0) return "";
        using var h = CreateFileW(path, 0, 3 /* FILE_SHARE_READ|WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h.IsInvalid) return "";
        var b = new byte[256];
        if (!HidD_GetProductString(h, b, (uint)b.Length)) return "";
        return Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
    }
}
