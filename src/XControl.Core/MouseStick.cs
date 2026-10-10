namespace XControl.Core;

public enum MouseTarget { None, RightStick, LeftStick }

public sealed class MouseSettings
{
    /// <summary>Counts/second of mouse travel that gives full deflection at sensitivity 1.0.</summary>
    public const double ReferenceRate = 3000.0;

    public double SensitivityX { get; set; } = 1.0;
    public double SensitivityY { get; set; } = 1.0;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    /// <summary>Output offset (0..0.6) added as soon as the mouse moves, to defeat the game's own stick dead zone.</summary>
    public double Deadzone { get; set; } = 0.0;
    /// <summary>Largest stick magnitude the mouse may produce (0.1..1).</summary>
    public double MaxOutput { get; set; } = 1.0;
    /// <summary>Response exponent: 1 = linear, &gt;1 = finer control near centre, &lt;1 = more aggressive.</summary>
    public double Curve { get; set; } = 1.0;
    /// <summary>0 = none … 0.95 = heavy. Exponential smoothing of the mouse velocity.</summary>
    public double Smoothing { get; set; } = 0.0;
    /// <summary>Extra gain that grows with speed (0 = off).</summary>
    public double Acceleration { get; set; } = 0.0;
    public double Multiplier { get; set; } = 1.0;
    public MouseTarget Target { get; set; } = MouseTarget.RightStick;
    /// <summary>While active, freeze the OS cursor so mouse motion cannot leave the browser window.</summary>
    public bool CaptureCursor { get; set; } = true;

    public MouseSettings Clone() => (MouseSettings)MemberwiseClone();

    public void Clamp()
    {
        SensitivityX = Math.Clamp(SensitivityX, 0.01, 20);
        SensitivityY = Math.Clamp(SensitivityY, 0.01, 20);
        Deadzone = Math.Clamp(Deadzone, 0, 0.6);
        MaxOutput = Math.Clamp(MaxOutput, 0.1, 1);
        Curve = Math.Clamp(Curve, 0.3, 4);
        Smoothing = Math.Clamp(Smoothing, 0, 0.95);
        Acceleration = Math.Clamp(Acceleration, 0, 5);
        Multiplier = Math.Clamp(Multiplier, 0.05, 10);
    }
}

/// <summary>
/// Converts mouse displacement into an analog stick position. The stick is treated as a velocity:
/// moving the mouse faster pushes the stick further, and it recentres when the mouse stops.
/// Pure and deterministic so it can be unit-tested.
/// </summary>
public sealed class MouseStickTranslator
{
    double _sx, _sy;

    public MouseSettings Settings { get; set; }

    /// <summary>
    /// Fixed short averaging window (≈ 3 ticks) applied on top of the user's smoothing so that ticks with no mouse
    /// report (sparse or low-rate mice) do not snap the stick to zero between events.
    /// </summary>
    public double BaselineSmoothing { get; set; } = 0.4;

    public MouseStickTranslator(MouseSettings settings) => Settings = settings;

    public void Reset() { _sx = _sy = 0; }

    /// <param name="dx">Mouse counts since the last call (right positive).</param>
    /// <param name="dy">Mouse counts since the last call (down positive).</param>
    /// <param name="dt">Seconds since the last call.</param>
    /// <returns>Stick position, each axis in −1..1, +Y = up.</returns>
    public (double X, double Y) Update(double dx, double dy, double dt)
    {
        var s = Settings;
        if (dt <= 0) dt = 0.001;

        var gain = s.Multiplier / MouseSettings.ReferenceRate / dt;
        var nx = dx * gain * s.SensitivityX * (s.InvertX ? -1 : 1);
        var ny = -dy * gain * s.SensitivityY * (s.InvertY ? -1 : 1);

        // Smoothing is frame-rate independent: alpha is the share of the new sample kept per 8 ms.
        var keep = Math.Pow(Math.Max(s.Smoothing, BaselineSmoothing), dt / 0.008);
        _sx = keep * _sx + (1 - keep) * nx;
        _sy = keep * _sy + (1 - keep) * ny;

        var m = Math.Sqrt(_sx * _sx + _sy * _sy);
        if (m < 0.002) { _sx = _sy = 0; return (0, 0); }

        var shaped = Math.Pow(Math.Min(m, 1.5), s.Curve);
        shaped *= 1 + s.Acceleration * Math.Min(m, 1.5);
        shaped = s.Deadzone + (1 - s.Deadzone) * Math.Min(shaped, 1.0);
        shaped = Math.Min(shaped, s.MaxOutput);

        return (_sx / m * shaped, _sy / m * shaped);
    }
}
