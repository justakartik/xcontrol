using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XControl.Core;

static class Native
{
    public delegate nint HookProc(int nCode, nint wParam, nint lParam);
    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int id, HookProc fn, nint hMod, uint tid);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint w, nint l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint tid, uint msg, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS { public uint style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClass(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint ex, string cls, string? name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint DefWindowProc(nint hwnd, uint msg, nint w, nint l);

    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort usagePage, usage; public uint flags; public nint target; }
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(nint h, uint cmd, byte[]? data, ref uint size, uint headerSize);

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, char[] sb, int max);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(nint h, uint flags, char[] name, ref uint size);

    /// <summary>Executable name without extension, or "" when the process cannot be queried.</summary>
    public static string ProcessNameOf(uint pid)
    {
        var h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (h == 0) return "";
        try
        {
            var buf = new char[512]; uint n = (uint)buf.Length;
            return QueryFullProcessImageName(h, 0, buf, ref n) ? System.IO.Path.GetFileNameWithoutExtension(new string(buf, 0, (int)n)) : "";
        }
        finally { CloseHandle(h); }
    }
}

/// <summary>Keyboard shortcut such as "Ctrl+Shift+F8".</summary>
public readonly record struct Hotkey(bool Ctrl, bool Shift, bool Alt, int Vk)
{
    public static bool TryParse(string? text, out Hotkey hk)
    {
        hk = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool c = false, s = false, a = false; var vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) c = true;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) s = true;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) a = true;
            else if (!InputNames.TryKeyCode(part, out vk)) return false;
        }
        if (vk == 0 || vk is 0x10 or 0x11 or 0x12) return false;
        hk = new Hotkey(c, s, a, vk);
        return true;
    }

    /// <summary>Combinations Windows or the browser need for themselves.</summary>
    public static bool IsReserved(Hotkey h) =>
        (h.Alt && !h.Ctrl && !h.Shift && h.Vk is 0x73 or 0x09) ||   // Alt+F4, Alt+Tab
        (h.Ctrl && h.Alt && h.Vk == 0x2E) ||                          // Ctrl+Alt+Del
        (h.Ctrl && !h.Alt && !h.Shift && h.Vk is 0x43 or 0x56 or 0x58 or 0x57 or 0x54) || // Ctrl+C/V/X/W/T
        (!h.Ctrl && !h.Alt && !h.Shift);

    public override string ToString() =>
        string.Join("+", new[] { Ctrl ? "Ctrl" : null, Shift ? "Shift" : null, Alt ? "Alt" : null, InputNames.KeyName(Vk) }.Where(x => x != null));
}

/// <summary>
/// Foreground-window tracking: decides whether translation is allowed (browser focused) and reports what is focused.
/// </summary>
public sealed class ForegroundMonitor : IDisposable
{
    readonly ControllerEngine _engine;
    readonly Timer _timer;
    HashSet<string> _browsers = new(StringComparer.OrdinalIgnoreCase);
    bool _gate = true;

    public string ProcessName { get; private set; } = "";
    public string WindowTitle { get; private set; } = "";
    public bool IsBrowser { get; private set; }
    public bool LooksLikeXboxCloud { get; private set; }

    public ForegroundMonitor(ControllerEngine engine)
    {
        _engine = engine;
        _timer = new Timer(_ => Poll(), null, 0, 150);
    }

    public void Configure(IEnumerable<string> browserProcesses, bool onlyWhenBrowser)
    {
        _browsers = new HashSet<string>(browserProcesses.Select(b => b.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        _gate = onlyWhenBrowser;
        Poll();
    }

    void Poll()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            string name = "", title = "";
            if (hwnd != 0 && Native.GetWindowThreadProcessId(hwnd, out var pid) != 0)
            {
                name = Native.ProcessNameOf(pid);
                var buf = new char[256];
                var n = Native.GetWindowText(hwnd, buf, buf.Length);
                title = new string(buf, 0, n);
            }
            ProcessName = name;
            WindowTitle = title;
            IsBrowser = _browsers.Contains(name);
            LooksLikeXboxCloud = IsBrowser && title.Contains("Xbox", StringComparison.OrdinalIgnoreCase);
            _engine.FocusActive = !_gate || IsBrowser;
        }
        catch { /* never let the poll thread die */ }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>
/// System-wide input capture on its own message-loop thread:
/// low-level keyboard and mouse-button hooks (consume only inputs the active profile binds, only while the engine
/// is running and the browser is focused) plus Raw Input for relative mouse movement.
/// </summary>
public sealed class InputHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205,
        WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C;
    const uint WM_INPUT = 0xFF, WM_QUIT = 0x12;
    const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;

    /// <summary>Test-only: XCONTROL_ACCEPT_INJECTED=1 lets synthetic (SendInput) events drive the engine. Off by default so other tools cannot feed it.</summary>
    static readonly bool AcceptInjected = Environment.GetEnvironmentVariable("XCONTROL_ACCEPT_INJECTED") == "1";

    readonly ControllerEngine _engine;
    readonly Native.HookProc _kbProc, _msProc;
    readonly Native.WndProc _wndProc;
    readonly HashSet<InputId> _swallowedDown = [];
    readonly object _gate = new();
    readonly ManualResetEventSlim _ready = new();
    Thread? _thread;
    uint _threadId;
    nint _kbHook, _msHook, _hwnd;
    Hotkey _hotkey;
    bool _hasHotkey;
    string? _startError;
    bool _ctrl, _shift, _alt;   // modifier state as seen in the hook's own event stream

    public Action? ToggleRequested;
    public bool IsRunning { get; private set; }
    public string? Error => _startError;
    public long KeyEvents, MouseEvents, RawMoves;
    /// <summary>Time spent inside the keyboard/mouse hook callbacks (these block the whole system's input while they run).</summary>
    public long HookCalls; long _hookTicks, _hookMaxTicks;
    public double HookAvgUs => HookCalls == 0 ? 0 : _hookTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / HookCalls;
    public double HookMaxUs => _hookMaxTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    void Timed(long start)
    {
        var d = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        _hookTicks += d; HookCalls++;
        if (d > _hookMaxTicks) _hookMaxTicks = d;
    }
    /// <summary>Raised for every key down/up seen while the user is capturing a rebind (see <see cref="CaptureMode"/>).</summary>
    public volatile bool CaptureMode;

    public InputHook(ControllerEngine engine)
    {
        _engine = engine;
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
        _wndProc = WndProc;
    }

    public void SetHotkey(string text)
    {
        lock (_gate) { _hasHotkey = Hotkey.TryParse(text, out _hotkey); }
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "XControl.InputHook" };
        _thread.Start();
        _ready.Wait(3000);
    }

    void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        try
        {
            var mod = Native.GetModuleHandle(null);
            _kbHook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
            if (_kbHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "keyboard hook");
            _msHook = Native.SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
            if (_msHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "mouse hook");

            var cls = new Native.WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = mod,
                lpszClassName = "XControlRawInput" + Environment.ProcessId,
            };
            if (Native.RegisterClass(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "window class");
            _hwnd = Native.CreateWindowEx(0, cls.lpszClassName, null, 0, 0, 0, 0, 0, -3 /*HWND_MESSAGE*/, 0, mod, 0);
            if (_hwnd == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "message window");
            var rid = new[] { new Native.RAWINPUTDEVICE { usagePage = 1, usage = 2, flags = 0x100 /*INPUTSINK*/, target = _hwnd } };
            if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "raw input");
            IsRunning = true;
        }
        catch (Exception ex)
        {
            _startError = "Input capture failed to start: " + ex.Message;
            Cleanup();
            _ready.Set();
            return;
        }
        _ready.Set();

        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        Cleanup();
    }

    void Cleanup()
    {
        IsRunning = false;
        if (_kbHook != 0) { Native.UnhookWindowsHookEx(_kbHook); _kbHook = 0; }
        if (_msHook != 0) { Native.UnhookWindowsHookEx(_msHook); _msHook = 0; }
        if (_hwnd != 0) { Native.DestroyWindow(_hwnd); _hwnd = 0; }
    }

    // ---- keyboard ---------------------------------------------------------------------------

    nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var r = KeyboardCore(nCode, wParam, lParam);
        if (nCode >= 0) Timed(t0);
        return r;
    }

    nint KeyboardCore(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(0, nCode, wParam, lParam);
        try
        {
            var vk = Marshal.ReadInt32(lParam, 0);
            var flags = (uint)Marshal.ReadInt32(lParam, 8);
            var w = (int)wParam;
            var down = w is WM_KEYDOWN or WM_SYSKEYDOWN;
            var up = w is WM_KEYUP or WM_SYSKEYUP;
            if ((AcceptInjected || (flags & LLKHF_INJECTED) == 0) && (down || up))
            {
                Interlocked.Increment(ref KeyEvents);
                var id = InputId.Key(vk);

                switch (id.Code)
                {
                    case 0x11: _ctrl = down; break;
                    case 0x10: _shift = down; break;
                    case 0x12: _alt = down; break;
                }

                if (down && !_swallowedDown.Contains(id) && IsToggle(id.Code))
                {
                    var cb = ToggleRequested;
                    if (cb != null) ThreadPool.QueueUserWorkItem(_ => cb());
                    _swallowedDown.Add(id);   // swallow the matching key-up too
                    return 1; // consume the shortcut
                }
                if (down)
                {
                    if (_swallowedDown.Contains(id)) return 1; // auto-repeat of a key we own
                    if (_engine.ShouldHandle(id))
                    {
                        _swallowedDown.Add(id);
                        _engine.Input(id, true);
                        return 1;
                    }
                }
                else if (_swallowedDown.Remove(id))
                {
                    _engine.Input(id, false);
                    return 1; // its key-down was ours, so swallow the matching key-up
                }
            }
        }
        catch { /* a faulting hook must never block the keyboard */ }
        return Native.CallNextHookEx(0, nCode, wParam, lParam);
    }

    bool IsToggle(int vk)
    {
        Hotkey hk; bool has;
        lock (_gate) { hk = _hotkey; has = _hasHotkey; }
        return has && vk == hk.Vk && _ctrl == hk.Ctrl && _shift == hk.Shift && _alt == hk.Alt;
    }

    // ---- mouse buttons / wheel --------------------------------------------------------------

    nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var r = MouseCore(nCode, wParam, lParam);
        if (nCode >= 0) Timed(t0);
        return r;
    }

    nint MouseCore(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(0, nCode, wParam, lParam);
        try
        {
            var data = (uint)Marshal.ReadInt32(lParam, 8);
            var flags = (uint)Marshal.ReadInt32(lParam, 12);
            var msg = (int)wParam;
            if (AcceptInjected || (flags & LLMHF_INJECTED) == 0)
            {
                if (msg == WM_MOUSEMOVE) return _engine.ShouldCaptureCursor() ? 1 : Native.CallNextHookEx(0, nCode, wParam, lParam);

                Interlocked.Increment(ref MouseEvents);
                int code = 0; bool down = false, pulse = false;
                switch (msg)
                {
                    case WM_LBUTTONDOWN: code = InputId.MouseLeft; down = true; break;
                    case WM_LBUTTONUP: code = InputId.MouseLeft; break;
                    case WM_RBUTTONDOWN: code = InputId.MouseRight; down = true; break;
                    case WM_RBUTTONUP: code = InputId.MouseRight; break;
                    case WM_MBUTTONDOWN: code = InputId.MouseMiddle; down = true; break;
                    case WM_MBUTTONUP: code = InputId.MouseMiddle; break;
                    case WM_XBUTTONDOWN: code = (data >> 16) == 1 ? InputId.MouseX1 : InputId.MouseX2; down = true; break;
                    case WM_XBUTTONUP: code = (data >> 16) == 1 ? InputId.MouseX1 : InputId.MouseX2; break;
                    case WM_MOUSEWHEEL: code = (short)(data >> 16) > 0 ? InputId.WheelUp : InputId.WheelDown; pulse = true; down = true; break;
                }
                if (code != 0)
                {
                    var id = InputId.Mouse(code);
                    if (pulse)
                    {
                        if (_engine.ShouldHandle(id))
                        {
                            _engine.Input(id, true);
                            var e = _engine;
                            Timer? t = null;
                            t = new Timer(_ => { e.Input(id, false); t?.Dispose(); }, null, 60, Timeout.Infinite);
                            return 1;
                        }
                    }
                    else if (down)
                    {
                        if (_engine.ShouldHandle(id)) { _swallowedDown.Add(id); _engine.Input(id, true); return 1; }
                    }
                    else if (_swallowedDown.Remove(id))
                    {
                        _engine.Input(id, false);
                        return 1;
                    }
                }
            }
        }
        catch { }
        return Native.CallNextHookEx(0, nCode, wParam, lParam);
    }

    // ---- raw mouse movement -----------------------------------------------------------------

    nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_INPUT)
        {
            try
            {
                uint size = 0;
                const uint RID_INPUT = 0x10000003;
                var hdr = (uint)(nint.Size == 8 ? 24 : 16);
                Native.GetRawInputData(lParam, RID_INPUT, null, ref size, hdr);
                if (size is > 0 and < 256)
                {
                    var buf = new byte[size];
                    if (Native.GetRawInputData(lParam, RID_INPUT, buf, ref size, hdr) == size && BitConverter.ToUInt32(buf, 0) == 0 /*RIM_TYPEMOUSE*/)
                    {
                        var flags = BitConverter.ToUInt16(buf, (int)hdr);
                        if ((flags & 1) == 0) // relative
                        {
                            var dx = BitConverter.ToInt32(buf, (int)hdr + 12);
                            var dy = BitConverter.ToInt32(buf, (int)hdr + 16);
                            if (dx != 0 || dy != 0) { Interlocked.Increment(ref RawMoves); _engine.MouseMove(dx, dy); }
                        }
                    }
                }
            }
            catch { }
            return 0;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread?.Join(1000);
    }
}
