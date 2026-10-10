using System.Text.Json;
using System.Text.Json.Serialization;

namespace XControl.Core;

public sealed class Profile
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "Unnamed";
    public string? Description { get; set; }
    /// <summary>Control → physical inputs ("Key:W", "Mouse:Left"). Several inputs may drive one control.</summary>
    public Dictionary<PadControl, List<string>> Bindings { get; set; } = new();
    /// <summary>Optional per-input analog scale (0..1), e.g. "Key:Shift": 0.5 for a half-pressed trigger.</summary>
    public Dictionary<string, double> InputScales { get; set; } = new();
    public MouseSettings Mouse { get; set; } = new();
    public bool BuiltIn { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Profile Clone() => JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(this, Json), Json)!;

    public IEnumerable<InputId> BoundInputs(PadControl c) =>
        Bindings.TryGetValue(c, out var l)
            ? l.Select(s => InputId.TryParse(s, out var id) ? (InputId?)id : null).Where(i => i.HasValue).Select(i => i!.Value)
            : [];

    public void Bind(PadControl c, InputId id)
    {
        if (!Bindings.TryGetValue(c, out var l)) Bindings[c] = l = [];
        var s = id.ToString();
        if (!l.Contains(s, StringComparer.OrdinalIgnoreCase)) l.Add(s);
    }

    public void Unbind(PadControl c, InputId id)
    {
        if (Bindings.TryGetValue(c, out var l))
        {
            l.RemoveAll(x => x.Equals(id.ToString(), StringComparison.OrdinalIgnoreCase));
            if (l.Count == 0) Bindings.Remove(c);
        }
    }

    /// <summary>Controls (other than <paramref name="except"/>) that already use this input.</summary>
    public IReadOnlyList<PadControl> FindConflicts(InputId id, PadControl? except = null) =>
        Bindings.Where(kv => kv.Key != except && kv.Value.Any(s => s.Equals(id.ToString(), StringComparison.OrdinalIgnoreCase)))
                .Select(kv => kv.Key).ToList();

    /// <summary>All inputs bound to more than one control: input → controls.</summary>
    public Dictionary<InputId, List<PadControl>> AllDuplicates()
    {
        var map = new Dictionary<InputId, List<PadControl>>();
        foreach (var (c, list) in Bindings)
            foreach (var s in list)
                if (InputId.TryParse(s, out var id))
                {
                    if (!map.TryGetValue(id, out var l)) map[id] = l = [];
                    if (!l.Contains(c)) l.Add(c);
                }
        return map.Where(kv => kv.Value.Count > 1).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>Returns problems found; repairs what it can (bad tokens are dropped, numbers clamped).</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) { Name = "Unnamed"; problems.Add("Missing name."); }
        Bindings ??= new();
        InputScales ??= new();
        Mouse ??= new();
        foreach (var (c, list) in Bindings.ToList())
        {
            if (!Enum.IsDefined(c)) { Bindings.Remove(c); problems.Add($"Unknown control {c}."); continue; }
            var bad = list.Where(s => !InputId.TryParse(s, out _)).ToList();
            foreach (var b in bad) problems.Add($"{c}: unknown input '{b}' removed.");
            list.RemoveAll(s => bad.Contains(s));
            if (list.Count == 0) Bindings.Remove(c);
        }
        foreach (var k in InputScales.Keys.ToList())
        {
            if (!InputId.TryParse(k, out _)) { InputScales.Remove(k); problems.Add($"Scale for unknown input '{k}' removed."); continue; }
            InputScales[k] = Math.Clamp(InputScales[k], 0, 1);
        }
        Mouse.Clamp();
        return problems;
    }
}

/// <summary>Profile resolved into a lookup table for the hot path.</summary>
public sealed class CompiledProfile
{
    readonly Dictionary<InputId, (PadControl Control, double Scale)[]> _map = new();
    public MouseSettings Mouse { get; }
    public int ActiveBindingCount { get; }
    public string Name { get; }

    public CompiledProfile(Profile p)
    {
        Name = p.Name;
        Mouse = p.Mouse.Clone();
        var tmp = new Dictionary<InputId, List<(PadControl, double)>>();
        var count = 0;
        foreach (var (c, list) in p.Bindings)
            foreach (var s in list)
            {
                if (!InputId.TryParse(s, out var id)) continue;
                var scale = p.InputScales.TryGetValue(id.ToString(), out var sc) ? sc : 1.0;
                if (!tmp.TryGetValue(id, out var l)) tmp[id] = l = [];
                l.Add((c, scale));
                count++;
            }
        foreach (var (k, v) in tmp) _map[k] = [.. v];
        ActiveBindingCount = count;
    }

    public bool Handles(InputId id) => _map.ContainsKey(id);
    public IEnumerable<InputId> Inputs => _map.Keys;

    /// <summary>Resolves held inputs plus the mouse stick (−1..1, +Y up) into a pad report.</summary>
    public PadState Resolve(IEnumerable<InputId> held, double mouseX, double mouseY)
    {
        PadButtons buttons = 0;
        double lt = 0, rt = 0, lx = 0, ly = 0, rx = 0, ry = 0;

        foreach (var id in held)
        {
            if (!_map.TryGetValue(id, out var targets)) continue;
            foreach (var (c, s) in targets)
                switch (c)
                {
                    case PadControl.A: buttons |= PadButtons.A; break;
                    case PadControl.B: buttons |= PadButtons.B; break;
                    case PadControl.X: buttons |= PadButtons.X; break;
                    case PadControl.Y: buttons |= PadButtons.Y; break;
                    case PadControl.DpadUp: buttons |= PadButtons.DpadUp; break;
                    case PadControl.DpadDown: buttons |= PadButtons.DpadDown; break;
                    case PadControl.DpadLeft: buttons |= PadButtons.DpadLeft; break;
                    case PadControl.DpadRight: buttons |= PadButtons.DpadRight; break;
                    case PadControl.LeftBumper: buttons |= PadButtons.LeftBumper; break;
                    case PadControl.RightBumper: buttons |= PadButtons.RightBumper; break;
                    case PadControl.LeftStickClick: buttons |= PadButtons.LeftStickClick; break;
                    case PadControl.RightStickClick: buttons |= PadButtons.RightStickClick; break;
                    case PadControl.Menu: buttons |= PadButtons.Menu; break;
                    case PadControl.View: buttons |= PadButtons.View; break;
                    case PadControl.LeftTrigger: lt = Math.Max(lt, s); break;
                    case PadControl.RightTrigger: rt = Math.Max(rt, s); break;
                    case PadControl.LeftStickUp: ly += s; break;
                    case PadControl.LeftStickDown: ly -= s; break;
                    case PadControl.LeftStickLeft: lx -= s; break;
                    case PadControl.LeftStickRight: lx += s; break;
                    case PadControl.RightStickUp: ry += s; break;
                    case PadControl.RightStickDown: ry -= s; break;
                    case PadControl.RightStickLeft: rx -= s; break;
                    case PadControl.RightStickRight: rx += s; break;
                }
        }

        switch (Mouse.Target)
        {
            case MouseTarget.RightStick: rx += mouseX; ry += mouseY; break;
            case MouseTarget.LeftStick: lx += mouseX; ly += mouseY; break;
        }

        (lx, ly) = Circle(lx, ly);
        (rx, ry) = Circle(rx, ry);

        return new PadState(buttons, ToByte(lt), ToByte(rt), ToShort(lx), ToShort(ly), ToShort(rx), ToShort(ry));
    }

    static (double, double) Circle(double x, double y)
    {
        var len = Math.Sqrt(x * x + y * y);
        return len > 1 ? (x / len, y / len) : (x, y);
    }

    static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    static short ToShort(double v) => (short)Math.Round(Math.Clamp(v, -1, 1) * 32767);
}

public static class DefaultProfiles
{
    public const string DefaultName = "Default";
    public const string ForzaName = "Forza Horizon 5";

    static Profile Make(string name, string desc) => new() { Name = name, Description = desc, BuiltIn = true };

    static void B(Profile p, PadControl c, params string[] inputs)
    {
        foreach (var i in inputs) p.Bind(c, InputId.TryParse(i, out var id) ? id : throw new ArgumentException(i));
    }

    public static Profile CreateDefault()
    {
        var p = Make(DefaultName, "General-purpose layout: WASD left stick, mouse right stick.");
        B(p, PadControl.LeftStickUp, "Key:W");
        B(p, PadControl.LeftStickDown, "Key:S");
        B(p, PadControl.LeftStickLeft, "Key:A");
        B(p, PadControl.LeftStickRight, "Key:D");
        B(p, PadControl.A, "Key:Space");
        B(p, PadControl.B, "Key:C");
        B(p, PadControl.X, "Key:R");
        B(p, PadControl.Y, "Key:F");
        B(p, PadControl.LeftBumper, "Key:Q");
        B(p, PadControl.RightBumper, "Key:E");
        B(p, PadControl.RightTrigger, "Mouse:Left");
        B(p, PadControl.LeftTrigger, "Mouse:Right");
        B(p, PadControl.LeftStickClick, "Key:Shift");
        B(p, PadControl.RightStickClick, "Mouse:Middle");
        B(p, PadControl.DpadUp, "Key:Up");
        B(p, PadControl.DpadDown, "Key:Down");
        B(p, PadControl.DpadLeft, "Key:Left");
        B(p, PadControl.DpadRight, "Key:Right");
        B(p, PadControl.Menu, "Key:Escape");
        B(p, PadControl.View, "Key:Tab");
        p.Mouse.Curve = 1.2;
        p.Mouse.Deadzone = 0.05;
        p.Mouse.Smoothing = 0.2;
        return p;
    }

    /// <summary>
    /// Driving layout. The in-game actions assumed here (A = handbrake, X = rewind, LB/RB = shift) are
    /// Forza's usual Xbox controller defaults but have NOT been verified in-game yet.
    /// </summary>
    public static Profile CreateForza()
    {
        var p = Make(ForzaName, "Racing layout: W/S throttle & brake, A/D steering, mouse X steers the camera.");
        B(p, PadControl.RightTrigger, "Key:W", "Mouse:Left");
        B(p, PadControl.LeftTrigger, "Key:S", "Mouse:Right");
        B(p, PadControl.LeftStickLeft, "Key:A", "Key:Left");
        B(p, PadControl.LeftStickRight, "Key:D", "Key:Right");
        B(p, PadControl.A, "Key:Space");
        B(p, PadControl.X, "Key:R", "Mouse:Side1");
        B(p, PadControl.Y, "Key:C");
        B(p, PadControl.B, "Key:B");
        B(p, PadControl.LeftBumper, "Key:Q");
        B(p, PadControl.RightBumper, "Key:E");
        B(p, PadControl.LeftStickClick, "Key:H");
        B(p, PadControl.RightStickClick, "Mouse:Middle", "Key:V");
        B(p, PadControl.DpadUp, "Key:Up");
        B(p, PadControl.DpadDown, "Key:Down");
        B(p, PadControl.DpadLeft, "Key:1");
        B(p, PadControl.DpadRight, "Key:2");
        B(p, PadControl.Menu, "Key:Escape");
        B(p, PadControl.View, "Key:Tab");
        p.Mouse.SensitivityX = 0.8;
        p.Mouse.SensitivityY = 0.6;
        p.Mouse.Smoothing = 0.3;
        p.Mouse.Curve = 1.3;
        p.Mouse.Deadzone = 0.08;
        return p;
    }

    public static IEnumerable<Profile> All() => [CreateDefault(), CreateForza()];
}

public static class MousePresets
{
    public static readonly IReadOnlyDictionary<string, Action<MouseSettings>> All =
        new Dictionary<string, Action<MouseSettings>>
        {
            ["Balanced"] = m => { m.SensitivityX = 1; m.SensitivityY = 1; m.Curve = 1.2; m.Smoothing = 0.2; m.Acceleration = 0; m.Deadzone = 0.05; m.MaxOutput = 1; },
            ["Racing camera (slow)"] = m => { m.SensitivityX = 0.7; m.SensitivityY = 0.5; m.Curve = 1.4; m.Smoothing = 0.35; m.Acceleration = 0; m.Deadzone = 0.08; m.MaxOutput = 0.9; },
            ["Shooter (fast)"] = m => { m.SensitivityX = 1.8; m.SensitivityY = 1.5; m.Curve = 1.6; m.Smoothing = 0.1; m.Acceleration = 0.4; m.Deadzone = 0.12; m.MaxOutput = 1; },
            ["Precision (aim assist off)"] = m => { m.SensitivityX = 0.9; m.SensitivityY = 0.9; m.Curve = 2.0; m.Smoothing = 0.15; m.Acceleration = 0; m.Deadzone = 0.15; m.MaxOutput = 1; },
        };
}
