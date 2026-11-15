using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Layts.Core;

/// <summary>One "wheel input → keyboard key" rule. Saved in settings.json.</summary>
public sealed class WheelBinding
{
    public string Label { get; set; } = "";

    /// <summary>Device id (VID/PID if known, else "JOY&lt;n&gt;").</summary>
    public string Device { get; set; } = "";

    /// <summary>"B5" = button 5, "POV:U/D/L/R" = hat direction, "A:Z+" / "A:Z-" = axis pushed up/down.</summary>
    public string Input { get; set; } = "";

    /// <summary>Resting value of an axis (pedals rest at one end).</summary>
    public int AxisRest { get; set; }

    /// <summary>Windows virtual-key code of the keyboard key that is pressed.</summary>
    public int Key { get; set; }
}

public sealed record WheelDevice(int Index, string Id, string Name);

/// <summary>
/// Reads steering wheels, pedals and joysticks through the Windows joystick API (winmm) and turns
/// their buttons, hat and pedals into keyboard presses (SendInput with scan codes, which games read).
/// Runs on a background thread while layts is open.
/// </summary>
public sealed class WheelMapper : IDisposable
{
    public static WheelMapper Instance { get; } = new();

    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _enabled;
    private WheelBinding[] _bindings = Array.Empty<WheelBinding>();
    private readonly HashSet<string> _held = new();
    private Action<WheelBinding>? _capture;
    private readonly Dictionary<int, JOYINFOEX> _baseline = new();

    /// <summary>Raised (on the polling thread) with a human readable text of what is pressed right now.</summary>
    public event Action<string>? LiveInput;

    public bool Enabled => _enabled;

    private WheelMapper() { }

    public void Configure(bool enabled, IEnumerable<WheelBinding> bindings)
    {
        lock (_gate)
        {
            _bindings = bindings.Where(b => b.Key > 0 && b.Input.Length > 0).ToArray();
            _enabled = enabled;
        }
        if (!enabled) ReleaseAll();
        EnsureThread();
    }

    /// <summary>The next wheel input that is pressed is handed to <paramref name="onCaptured"/> instead of being mapped.</summary>
    public void Capture(Action<WheelBinding>? onCaptured)
    {
        lock (_gate)
        {
            _capture = onCaptured;
            _baseline.Clear();
        }
        EnsureThread();
    }

    private void EnsureThread()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "layts wheel mapper", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public static List<WheelDevice> Devices()
    {
        var list = new List<WheelDevice>();
        int count = (int)joyGetNumDevs();
        for (int i = 0; i < Math.Min(count, 16); i++)
        {
            var info = NewInfo();
            if (joyGetPosEx(i, ref info) != 0) continue;
            list.Add(new WheelDevice(i, DeviceId(i), DeviceName(i)));
        }
        return list;
    }

    private void Loop()
    {
        var lastLive = "";
        while (_running)
        {
            WheelBinding[] bindings;
            Action<WheelBinding>? capture;
            bool enabled;
            lock (_gate)
            {
                bindings = _bindings;
                capture = _capture;
                enabled = _enabled;
            }

            if (!enabled && capture == null && LiveInput == null)
            {
                Thread.Sleep(250);
                continue;
            }

            var active = new HashSet<string>();
            var live = new List<string>();
            int count = Math.Min((int)joyGetNumDevs(), 16);
            for (int i = 0; i < count; i++)
            {
                var info = NewInfo();
                if (joyGetPosEx(i, ref info) != 0) continue;
                string id = DeviceId(i);

                lock (_gate)
                    if (!_baseline.ContainsKey(i)) _baseline[i] = info;
                var rest = _baseline[i];

                foreach (var input in ActiveInputs(info, rest))
                {
                    active.Add(id + "|" + input.Input);
                    live.Add(Describe(input.Input));
                    if (capture != null)
                    {
                        input.Device = id;
                        lock (_gate) _capture = null;
                        capture(input);
                        capture = null;
                    }
                }

                if (!enabled) continue;
                foreach (var b in bindings.Where(b => b.Device == id))
                {
                    bool down = IsDown(info, b);
                    string key = id + "|" + b.Input + "|" + b.Key;
                    lock (_held)
                    {
                        if (down && _held.Add(key)) SendKey(b.Key, true);
                        else if (!down && _held.Remove(key)) SendKey(b.Key, false);
                    }
                }
            }

            var text = string.Join(", ", live.Distinct());
            if (text != lastLive)
            {
                lastLive = text;
                LiveInput?.Invoke(text);
            }
            Thread.Sleep(8);
        }
    }

    /// <summary>All inputs that are currently pressed (buttons, hat, axes moved far from rest).</summary>
    private static IEnumerable<WheelBinding> ActiveInputs(JOYINFOEX info, JOYINFOEX rest)
    {
        for (int b = 0; b < 32; b++)
            if ((info.dwButtons & (1u << b)) != 0)
                yield return new WheelBinding { Input = "B" + (b + 1) };

        foreach (var dir in PovDirections(info.dwPOV))
            yield return new WheelBinding { Input = "POV:" + dir };

        foreach (var (name, value, restValue) in Axes(info, rest))
        {
            int delta = (int)value - (int)restValue;
            if (Math.Abs(delta) > 26000) // pushed more than ~40 % of the range
                yield return new WheelBinding { Input = "A:" + name + (delta > 0 ? "+" : "-"), AxisRest = (int)restValue };
        }
    }

    private static bool IsDown(JOYINFOEX info, WheelBinding b)
    {
        if (b.Input.StartsWith('B') && int.TryParse(b.Input.AsSpan(1), out var n) && n is >= 1 and <= 32)
            return (info.dwButtons & (1u << (n - 1))) != 0;
        if (b.Input.StartsWith("POV:"))
            return PovDirections(info.dwPOV).Contains(b.Input[4..]);
        if (b.Input.StartsWith("A:") && b.Input.Length == 4)
        {
            uint value = b.Input[2] switch
            {
                'X' => info.dwXpos, 'Y' => info.dwYpos, 'Z' => info.dwZpos,
                'R' => info.dwRpos, 'U' => info.dwUpos, 'V' => info.dwVpos, _ => 0,
            };
            int delta = (int)value - b.AxisRest;
            return b.Input[3] == '+' ? delta > 32000 : delta < -32000; // half the range
        }
        return false;
    }

    private static IEnumerable<(string, uint, uint)> Axes(JOYINFOEX i, JOYINFOEX r)
    {
        yield return ("X", i.dwXpos, r.dwXpos);
        yield return ("Y", i.dwYpos, r.dwYpos);
        yield return ("Z", i.dwZpos, r.dwZpos);
        yield return ("R", i.dwRpos, r.dwRpos);
        yield return ("U", i.dwUpos, r.dwUpos);
        yield return ("V", i.dwVpos, r.dwVpos);
    }

    private static IEnumerable<string> PovDirections(uint pov)
    {
        if (pov == 0xFFFF || pov > 36000) yield break;
        if (pov >= 31500 || pov <= 4500) yield return "U";
        if (pov is >= 4500 and <= 13500) yield return "R";
        if (pov is >= 13500 and <= 22500) yield return "D";
        if (pov is >= 22500 and <= 31500) yield return "L";
    }

    public static string Describe(string input)
    {
        if (input.StartsWith('B')) return Loc.F("Wh_Button", input[1..]);
        if (input.StartsWith("POV:"))
            return Loc.Get("Wh_Pov") + " " + input[4] switch { 'U' => "↑", 'D' => "↓", 'L' => "←", _ => "→" };
        if (input.StartsWith("A:") && input.Length == 4) return Loc.F("Wh_Axis", input[2]) + (input[3] == '+' ? " +" : " −");
        return input;
    }

    public void ReleaseAll()
    {
        lock (_held)
        {
            foreach (var key in _held)
                if (int.TryParse(key[(key.LastIndexOf('|') + 1)..], out var vk)) SendKey(vk, false);
            _held.Clear();
        }
    }

    public void Dispose()
    {
        _running = false;
        ReleaseAll();
    }

    // ---------- Device names ----------

    private static string DeviceId(int index)
    {
        var caps = new JOYCAPSW();
        if (joyGetDevCapsW((IntPtr)index, ref caps, Marshal.SizeOf<JOYCAPSW>()) == 0 && caps.wMid != 0)
            return $"VID_{caps.wMid:X4}&PID_{caps.wPid:X4}";
        return "JOY" + index;
    }

    private static string DeviceName(int index)
    {
        var caps = new JOYCAPSW();
        if (joyGetDevCapsW((IntPtr)index, ref caps, Marshal.SizeOf<JOYCAPSW>()) != 0) return "Controller " + (index + 1);
        try
        {
            var oem = Registry.GetValue(
                $@"HKEY_CURRENT_USER\System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{caps.wMid:X4}&PID_{caps.wPid:X4}",
                "OEMName", null) as string;
            if (!string.IsNullOrWhiteSpace(oem)) return oem;
        }
        catch
        {
            // ignore
        }
        return string.IsNullOrWhiteSpace(caps.szPname) ? "Controller " + (index + 1) : caps.szPname;
    }

    // ---------- Keyboard ----------

    public static string KeyName(int vk)
    {
        if (vk <= 0) return "—";
        uint sc = MapVirtualKeyW((uint)vk, 0);
        int lParam = (int)(sc << 16);
        if (IsExtended(vk)) lParam |= 1 << 24;
        var sb = new StringBuilder(64);
        return GetKeyNameTextW(lParam, sb, sb.Capacity) > 0 ? sb.ToString() : "Key " + vk;
    }

    private static bool IsExtended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
        or 0x2D or 0x2E or 0x6F or 0x90 or 0xA3 or 0xA5 or 0x5B or 0x5C or 0x5D;

    private static void SendKey(int vk, bool down)
    {
        ushort scan = (ushort)MapVirtualKeyW((uint)vk, 0);
        uint flags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP);
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        var input = new INPUT
        {
            type = 1,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = flags } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ---------- Native ----------

    private const uint JOY_RETURNALL = 0x000000FF;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;

    private static JOYINFOEX NewInfo() => new() { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOYCAPSW
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax,
            wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("winmm.dll")] private static extern uint joyGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint joyGetPosEx(int uJoyID, ref JOYINFOEX pji);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint joyGetDevCapsW(IntPtr uJoyID, ref JOYCAPSW pjc, int cbjc);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetKeyNameTextW(int lParam, StringBuilder lpString, int cchSize);
}
