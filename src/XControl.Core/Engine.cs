using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace XControl.Core;

public interface IVirtualPad : IDisposable
{
    bool IsConnected { get; }
    /// <summary>Throws <see cref="PadException"/> when the driver is missing or the device cannot be created.</summary>
    void Connect();
    void Disconnect();
    void Submit(PadState state);
}

public sealed class PadException(string message, bool driverMissing = false, Exception? inner = null)
    : Exception(message, inner)
{
    public bool DriverMissing { get; } = driverMissing;
}

/// <summary>Virtual Xbox 360 controller through the ViGEmBus driver.</summary>
public sealed class ViGEmPad : IVirtualPad
{
    ViGEmClient? _client;
    IXbox360Controller? _pad;

    public bool IsConnected { get; private set; }

    public void Connect()
    {
        if (IsConnected) return;
        try
        {
            _client ??= new ViGEmClient();
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;
            _pad.Connect();
            IsConnected = true;
        }
        catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException ex)
        {
            Teardown();
            throw new PadException("ViGEmBus driver is not installed.", true, ex);
        }
        catch (Exception ex)
        {
            Teardown();
            throw new PadException("Could not create the virtual controller: " + ex.Message, false, ex);
        }
    }

    public void Submit(PadState s)
    {
        if (!IsConnected || _pad == null) throw new PadException("Virtual controller is not connected.");
        try
        {
            _pad.SetButtonsFull((ushort)s.Buttons);
            _pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.LeftTrigger);
            _pad.SetSliderValue(Xbox360Slider.RightTrigger, s.RightTrigger);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.LeftX);
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, s.LeftY);
            _pad.SetAxisValue(Xbox360Axis.RightThumbX, s.RightX);
            _pad.SetAxisValue(Xbox360Axis.RightThumbY, s.RightY);
            _pad.SubmitReport();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            throw new PadException("Submitting to the virtual controller failed: " + ex.Message, false, ex);
        }
    }

    public void Disconnect()
    {
        try { if (IsConnected) _pad?.Disconnect(); } catch { /* device already gone */ }
        Teardown();
    }

    void Teardown()
    {
        IsConnected = false;
        _pad = null;
        _client?.Dispose();
        _client = null;
    }

    public void Dispose() => Disconnect();
}

public enum EngineStatus { Off, Starting, Running, Paused, DriverMissing, Faulted }

public sealed record EngineSnapshot(
    bool Enabled, EngineStatus Status, string? Error, bool PadConnected, bool FocusActive,
    PadState Pad, double MouseRawX, double MouseRawY, double MouseStickX, double MouseStickY,
    int HeldInputs, int ActiveBindings, string ProfileName,
    double TickRateHz, double MaxTickMs, long LateTicks, long Submits, long Reconnects,
    double InputLatencyAvgMs, double InputLatencyMaxMs, long InputLatencySamples);

/// <summary>
/// Owns the virtual pad and the translation loop. Independent of any UI: input arrives through
/// <see cref="Input"/>/<see cref="MouseMove"/>, output leaves through the <see cref="IVirtualPad"/>.
/// </summary>
public sealed class ControllerEngine : IDisposable
{
    readonly Func<IVirtualPad> _padFactory;
    readonly object _lock = new();
    readonly HashSet<InputId> _held = [];
    readonly MouseStickTranslator _translator = new(new MouseSettings());
    readonly Thread _thread;
    readonly CancellationTokenSource _stop = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();

    IVirtualPad? _pad;
    CompiledProfile _profile = new(new Profile());
    bool _enabled, _focus = true, _preview, _disposed;
    double _mdx, _mdy;
    PadState _last;
    long _lastSubmitTicks;
    string? _error;
    EngineStatus _status = EngineStatus.Off;
    double _rawX, _rawY, _stickX, _stickY;
    long _lateTicks, _submits, _reconnects, _tickCount;
    double _maxTickMs, _rateHz;
    long _rateWindowStart, _rateWindowCount;
    long _nextConnectTry;
    int _periodMs = 8;
    long _inputStamp;                    // clock ticks when the oldest not-yet-reported key/button change arrived
    double _latSumMs, _latMaxMs; long _latCount;

    static readonly PadState WakeNudge = new(PadButtons.None, 0, 0, 1, 0, 0, 0);

    public event Action? Changed;

    public ControllerEngine(Func<IVirtualPad> padFactory, int updateRateHz = 125)
    {
        _padFactory = padFactory;
        SetUpdateRate(updateRateHz);
        _thread = new Thread(Loop) { IsBackground = true, Name = "XControl.Engine", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    public void SetUpdateRate(int hz) => _periodMs = Math.Max(1, 1000 / Math.Clamp(hz, 30, 500));

    public bool Enabled { get { lock (_lock) return _enabled; } }

    /// <summary>True when the foreground window allows translation (browser gate). Losing it releases everything.</summary>
    public bool FocusActive
    {
        get { lock (_lock) return _focus; }
        set
        {
            lock (_lock)
            {
                if (_focus == value) return;
                _focus = value;
                if (!value) ReleaseLocked();
            }
            Changed?.Invoke();
        }
    }

    /// <summary>When true the mouse stick is computed even while disabled (for the Test Mode UI) but never sent.</summary>
    public bool PreviewMouse
    {
        get { lock (_lock) return _preview; }
        set { lock (_lock) { _preview = value; if (!value) { _mdx = _mdy = 0; _translator.Reset(); } } }
    }

    public void SetProfile(Profile p)
    {
        var c = new CompiledProfile(p);
        lock (_lock)
        {
            _profile = c;
            _translator.Settings = c.Mouse;
            _translator.Reset();
            ReleaseLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>True when this input should be consumed by the engine (and hidden from other applications).</summary>
    public bool ShouldHandle(InputId id)
    {
        lock (_lock) return _enabled && _focus && _status == EngineStatus.Running && _profile.Handles(id);
    }

    public bool ShouldCaptureCursor()
    {
        lock (_lock) return _enabled && _focus && _status == EngineStatus.Running && _profile.Mouse.CaptureCursor
                            && _profile.Mouse.Target != MouseTarget.None;
    }

    public void Input(InputId id, bool down)
    {
        lock (_lock)
        {
            var changed = down ? _enabled && _focus && _held.Add(id) : _held.Remove(id);
            if (changed && _inputStamp == 0) _inputStamp = _clock.ElapsedTicks;
        }
    }

    public void MouseMove(int dx, int dy)
    {
        lock (_lock)
        {
            if (!(_enabled && _focus) && !_preview) return;
            _mdx += dx; _mdy += dy;
        }
    }

    public void ReleaseAll()
    {
        lock (_lock) ReleaseLocked();
    }

    void ReleaseLocked()
    {
        _held.Clear();
        _inputStamp = 0;
        _mdx = _mdy = 0;
        _translator.Reset();
        _stickX = _stickY = 0;
    }

    public void SetEnabled(bool on)
    {
        lock (_lock)
        {
            if (_enabled == on) return;
            _enabled = on;
            ReleaseLocked();
            if (on)
            {
                _error = null;
                _status = EngineStatus.Starting;
                _nextConnectTry = 0;
            }
        }
        if (!on) NeutraliseAndDisconnect();
        Changed?.Invoke();
    }

    /// <summary>Force a fresh connection attempt (dashboard "Reconnect").</summary>
    public void Reconnect()
    {
        lock (_lock) { _nextConnectTry = 0; _error = null; }
        DropPad();
    }

    public EngineSnapshot Snapshot()
    {
        lock (_lock)
            return new EngineSnapshot(_enabled, _status, _error, _pad?.IsConnected == true, _focus, _last,
                _rawX, _rawY, _stickX, _stickY, _held.Count, _profile.ActiveBindingCount, _profile.Name,
                _rateHz, _maxTickMs, _lateTicks, _submits, _reconnects,
                _latCount == 0 ? 0 : _latSumMs / _latCount, _latMaxMs, _latCount);
    }

    // ---- loop -------------------------------------------------------------------------------

    void Loop()
    {
        TimerResolution.Begin();
        long prev = _clock.ElapsedTicks;
        long next = prev;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // Nothing to translate while OFF (and no Test Mode preview): idle at 20 Hz to save CPU.
                bool busy; lock (_lock) busy = _enabled || _preview;
                var period = busy ? _periodMs : 50;
                next += period * Stopwatch.Frequency / 1000;
                SleepUntil(next);
                var now = _clock.ElapsedTicks;
                var dt = (now - prev) / (double)Stopwatch.Frequency;
                prev = now;
                if (now - next > period * Stopwatch.Frequency / 1000) { _lateTicks++; next = now; }
                Tick(dt, now);
            }
        }
        finally { TimerResolution.End(); }
    }

    void SleepUntil(long target)
    {
        while (true)
        {
            var remaining = (target - _clock.ElapsedTicks) * 1000.0 / Stopwatch.Frequency;
            if (remaining <= 0 || _stop.IsCancellationRequested) return;
            if (remaining > 1.5) Thread.Sleep(1); else Thread.Yield();
        }
    }

    void Tick(double dt, long nowTicks)
    {
        PadState state;
        bool send, enabled;
        lock (_lock)
        {
            enabled = _enabled;
            var dx = _mdx; var dy = _mdy;
            _mdx = _mdy = 0;
            _rawX = dx / dt; _rawY = dy / dt;

            if (_enabled || _preview)
            {
                var (sx, sy) = _profile.Mouse.Target == MouseTarget.None && !_preview
                    ? (0.0, 0.0)
                    : _translator.Update(dx, dy, dt);
                _stickX = sx; _stickY = sy;
            }
            else { _stickX = _stickY = 0; }

            state = _enabled && _focus && _status == EngineStatus.Running
                ? _profile.Resolve(_held, _profile.Mouse.Target == MouseTarget.None ? 0 : _stickX, _stickY)
                : PadState.Neutral;
            // heartbeat every 250 ms so the pad never looks stale
            send = state != _last || nowTicks - _lastSubmitTicks > Stopwatch.Frequency / 4;
        }

        if (enabled) EnsureConnected(nowTicks);

        IVirtualPad? pad;
        lock (_lock) pad = _enabled && _pad?.IsConnected == true ? _pad : null;

        if (send && pad != null)
        {
            try
            {
                var changed = state != _last;
                pad.Submit(state);
                lock (_lock)
                {
                    _last = state; _lastSubmitTicks = nowTicks; _submits++;
                    if (changed && _inputStamp != 0)
                    {
                        var ms = (_clock.ElapsedTicks - _inputStamp) * 1000.0 / Stopwatch.Frequency;
                        _latSumMs += ms; _latCount++; _latMaxMs = Math.Max(_latMaxMs, ms);
                        _inputStamp = 0;
                    }
                }
            }
            catch (PadException ex)
            {
                lock (_lock) { _error = ex.Message; _status = EngineStatus.Faulted; _last = PadState.Neutral; }
                DropPad();
                Changed?.Invoke();
            }
        }

        var tickMs = (_clock.ElapsedTicks - nowTicks) * 1000.0 / Stopwatch.Frequency;
        _maxTickMs = Math.Max(_maxTickMs * 0.999, tickMs);
        _rateWindowCount++;
        if (nowTicks - _rateWindowStart >= Stopwatch.Frequency)
        {
            _rateHz = _rateWindowCount * Stopwatch.Frequency / (double)(nowTicks - _rateWindowStart);
            _rateWindowStart = nowTicks; _rateWindowCount = 0;
        }
        _tickCount++;
    }

    void EnsureConnected(long nowTicks)
    {
        lock (_lock)
        {
            if (_pad?.IsConnected == true)
            {
                if (_status is EngineStatus.Starting or EngineStatus.Faulted or EngineStatus.Paused) _status = EngineStatus.Running;
                return;
            }
            if (nowTicks < _nextConnectTry) return;
            _nextConnectTry = nowTicks + 2 * Stopwatch.Frequency;
        }

        try
        {
            var pad = _padFactory();
            pad.Connect();
            lock (_lock)
            {
                _pad = pad; _status = EngineStatus.Running; _error = null; _reconnects++;
                _last = PadState.Neutral; _lastSubmitTicks = 0;
            }
            // Windows exposes a stale initial XInput state (non-zero sticks, packet 1) until a report that
            // differs from all-zero arrives. A 1-count left-stick nudge (far below any dead zone) followed by
            // a neutral report replaces it with a clean state. Verified with xinput1_4 readings.
            try
            {
                pad.Submit(WakeNudge);
                Thread.Sleep(30);
                pad.Submit(PadState.Neutral);
            }
            catch (PadException) { }
        }
        catch (PadException ex)
        {
            lock (_lock) { _error = ex.Message; _status = ex.DriverMissing ? EngineStatus.DriverMissing : EngineStatus.Faulted; }
        }
        Changed?.Invoke();
    }

    void DropPad()
    {
        IVirtualPad? p;
        lock (_lock) { p = _pad; _pad = null; }
        try { p?.Dispose(); } catch { /* device gone */ }
    }

    void NeutraliseAndDisconnect()
    {
        IVirtualPad? p;
        lock (_lock) { p = _pad; _pad = null; _last = PadState.Neutral; _status = EngineStatus.Off; _stickX = _stickY = 0; }
        if (p != null)
        {
            try { p.Submit(PadState.Neutral); } catch { /* already gone */ }
            try { p.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock) { _enabled = false; ReleaseLocked(); }
        _stop.Cancel();
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(1000);
        NeutraliseAndDisconnect();
    }
}

static class TimerResolution
{
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    public static void Begin() { try { timeBeginPeriod(1); } catch { } }
    public static void End() { try { timeEndPeriod(1); } catch { } }
}
