using XControl.Core;

namespace XControl.Tests;

public class InputIdTests
{
    [Theory]
    [InlineData("Key:W", 0x57)]
    [InlineData("Key:Space", 0x20)]
    [InlineData("Key:F5", 0x74)]
    [InlineData("Key:Shift", 0x10)]
    [InlineData("Key:Num3", 0x63)]
    [InlineData("Key:7", 0x37)]
    public void ParsesAndRoundTripsKeys(string text, int vk)
    {
        Assert.True(InputId.TryParse(text, out var id));
        Assert.Equal(vk, id.Code);
        Assert.True(InputId.TryParse(id.ToString(), out var again));
        Assert.Equal(id, again);
    }

    [Theory]
    [InlineData("Mouse:Left", 1)]
    [InlineData("Mouse:Side2", 5)]
    [InlineData("Mouse:WheelUp", 6)]
    public void ParsesMouse(string text, int code)
    {
        Assert.True(InputId.TryParse(text, out var id));
        Assert.Equal(InputKind.Mouse, id.Kind);
        Assert.Equal(code, id.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("W")]
    [InlineData("Key:NotAKey")]
    [InlineData("Mouse:Nope")]
    [InlineData("Pad:A")]
    public void RejectsGarbage(string text) => Assert.False(InputId.TryParse(text, out _));

    [Fact]
    public void LeftAndRightModifiersCollapse()
    {
        Assert.Equal(InputId.Key(0x10), InputId.Key(0xA0));
        Assert.Equal(InputId.Key(0x10), InputId.Key(0xA1));
        Assert.Equal(InputId.Key(0x11), InputId.Key(0xA3));
        Assert.Equal(InputId.Key(0x12), InputId.Key(0xA5));
    }

    [Fact]
    public void UnknownVkRoundTripsThroughHexFallback()
    {
        var id = InputId.Key(0xE2);
        Assert.True(InputId.TryParse(id.ToString(), out var back));
        Assert.Equal(id, back);
    }
}

public class MappingTests
{
    static CompiledProfile Compile(Action<Profile> edit)
    {
        var p = new Profile { Name = "t" };
        edit(p);
        return new CompiledProfile(p);
    }

    static InputId K(string n) { InputId.TryParse("Key:" + n, out var id); return id; }

    [Fact]
    public void ButtonsMapToExpectedBits()
    {
        var c = Compile(p => { p.Bind(PadControl.A, K("Space")); p.Bind(PadControl.Y, K("F")); p.Bind(PadControl.LeftBumper, K("Q")); });
        var s = c.Resolve([K("Space"), K("Q")], 0, 0);
        Assert.Equal(PadButtons.A | PadButtons.LeftBumper, s.Buttons);
    }

    [Fact]
    public void SimultaneousInputsAreAllReported()
    {
        var c = Compile(p =>
        {
            p.Bind(PadControl.RightTrigger, K("W")); p.Bind(PadControl.LeftStickLeft, K("A")); p.Bind(PadControl.A, K("Space"));
        });
        var s = c.Resolve([K("W"), K("A"), K("Space")], 0, 0);
        Assert.Equal(255, s.RightTrigger);
        Assert.Equal(-32767, s.LeftX);
        Assert.True(s.Buttons.HasFlag(PadButtons.A));
    }

    [Fact]
    public void TriggerScaleGivesAnalogValue()
    {
        var c = Compile(p => { p.Bind(PadControl.RightTrigger, K("W")); p.InputScales["Key:W"] = 0.5; });
        var s = c.Resolve([K("W")], 0, 0);
        Assert.InRange(s.RightTrigger, 127, 128);
    }

    [Fact]
    public void OppositeKeysCancel()
    {
        var c = Compile(p => { p.Bind(PadControl.LeftStickLeft, K("A")); p.Bind(PadControl.LeftStickRight, K("D")); });
        Assert.Equal(0, c.Resolve([K("A"), K("D")], 0, 0).LeftX);
    }

    [Fact]
    public void DiagonalKeyboardStickIsClampedToUnitCircle()
    {
        var c = Compile(p =>
        {
            p.Bind(PadControl.LeftStickUp, K("W")); p.Bind(PadControl.LeftStickRight, K("D"));
        });
        var s = c.Resolve([K("W"), K("D")], 0, 0);
        var len = Math.Sqrt((double)s.LeftX * s.LeftX + (double)s.LeftY * s.LeftY);
        Assert.InRange(len, 32700, 32800);
        Assert.Equal(s.LeftX, s.LeftY);
    }

    [Fact]
    public void MultipleInputsCanDriveOneControl()
    {
        var c = Compile(p => { p.Bind(PadControl.A, K("Space")); p.Bind(PadControl.A, InputId.Mouse(InputId.MouseX1)); });
        Assert.True(c.Resolve([InputId.Mouse(InputId.MouseX1)], 0, 0).Buttons.HasFlag(PadButtons.A));
        Assert.True(c.Resolve([K("Space")], 0, 0).Buttons.HasFlag(PadButtons.A));
    }

    [Fact]
    public void MouseDrivesRightStickWhenTargeted()
    {
        var c = Compile(p => p.Mouse.Target = MouseTarget.RightStick);
        var s = c.Resolve([], 0.5, -0.25);
        Assert.InRange(s.RightX, 16300, 16500);
        Assert.InRange(s.RightY, -8300, -8100);
    }

    [Fact]
    public void MouseIgnoredWhenTargetNone()
    {
        var c = Compile(p => p.Mouse.Target = MouseTarget.None);
        var s = c.Resolve([], 1, 1);
        Assert.Equal(0, s.RightX);
        Assert.Equal(0, s.RightY);
    }

    [Fact]
    public void UnboundInputsDoNothing()
    {
        var c = Compile(p => p.Bind(PadControl.A, K("Space")));
        Assert.True(c.Resolve([K("Z")], 0, 0).IsNeutral);
    }

    [Fact]
    public void RightStickKeysBindToRightStick()
    {
        var c = Compile(p => p.Bind(PadControl.RightStickUp, K("I")));
        Assert.Equal(32767, c.Resolve([K("I")], 0, 0).RightY);
    }
}

public class MouseStickTests
{
    static MouseStickTranslator T(Action<MouseSettings>? edit = null)
    {
        var s = new MouseSettings { Deadzone = 0, Smoothing = 0, Curve = 1, Acceleration = 0 };
        edit?.Invoke(s);
        return new MouseStickTranslator(s) { BaselineSmoothing = 0 };
    }

    const double Dt = 0.008;
    static double CountsFor(double fraction) => fraction * MouseSettings.ReferenceRate * Dt;

    [Fact]
    public void NoMovementGivesCentre()
    {
        var (x, y) = T().Update(0, 0, Dt);
        Assert.Equal(0, x); Assert.Equal(0, y);
    }

    [Fact]
    public void LinearResponseIsProportionalToSpeed()
    {
        var (x, _) = T().Update(CountsFor(0.5), 0, Dt);
        Assert.InRange(x, 0.49, 0.51);
    }

    [Fact]
    public void SensitivityScalesOutput()
    {
        var (x, _) = T(s => s.SensitivityX = 0.5).Update(CountsFor(0.5), 0, Dt);
        Assert.InRange(x, 0.24, 0.26);
    }

    [Fact]
    public void MultiplierScalesOutput()
    {
        var (x, _) = T(s => s.Multiplier = 0.5).Update(CountsFor(0.5), 0, Dt);
        Assert.InRange(x, 0.24, 0.26);
    }

    [Fact]
    public void MouseDownMeansStickDownAndInvertFlips()
    {
        var (_, y) = T().Update(0, CountsFor(0.5), Dt);
        Assert.True(y < 0);
        var (_, yi) = T(s => s.InvertY = true).Update(0, CountsFor(0.5), Dt);
        Assert.True(yi > 0);
        var (xi, _) = T(s => s.InvertX = true).Update(CountsFor(0.5), 0, Dt);
        Assert.True(xi < 0);
    }

    [Fact]
    public void FastMovementIsClampedAndNeverExceedsMax()
    {
        var (x, y) = T().Update(CountsFor(10), CountsFor(10), Dt);
        Assert.InRange(Math.Sqrt(x * x + y * y), 0.999, 1.0001);
        var (mx, _) = T(s => s.MaxOutput = 0.6).Update(CountsFor(10), 0, Dt);
        Assert.InRange(mx, 0.599, 0.601);
    }

    [Fact]
    public void DeadzoneOffsetAppliesOnlyWhileMoving()
    {
        var t = T(s => s.Deadzone = 0.2);
        var (small, _) = t.Update(CountsFor(0.01), 0, Dt);
        Assert.True(small >= 0.2);
        var (zero, _) = t.Update(0, 0, Dt);
        Assert.Equal(0, zero);
    }

    [Fact]
    public void CurveAboveOneReducesSmallMovements()
    {
        var linear = T().Update(CountsFor(0.3), 0, Dt).X;
        var curved = T(s => s.Curve = 2).Update(CountsFor(0.3), 0, Dt).X;
        Assert.True(curved < linear);
        Assert.InRange(curved, 0.085, 0.095);
    }

    [Fact]
    public void AccelerationBoostsFastMovement()
    {
        var plain = T().Update(CountsFor(0.5), 0, Dt).X;
        var accel = T(s => s.Acceleration = 1).Update(CountsFor(0.5), 0, Dt).X;
        Assert.True(accel > plain);
    }

    [Fact]
    public void SmoothingRampsUpAndDecaysToZero()
    {
        var t = T(s => s.Smoothing = 0.8);
        var first = t.Update(CountsFor(1), 0, Dt).X;
        Assert.InRange(first, 0.15, 0.25);
        double v = first;
        for (var i = 0; i < 30; i++) v = t.Update(CountsFor(1), 0, Dt).X;
        Assert.InRange(v, 0.95, 1.0);
        for (var i = 0; i < 200; i++) v = t.Update(0, 0, Dt).X;
        Assert.Equal(0, v);
    }

    [Fact]
    public void BaselineAveragingBridgesTicksWithoutMouseReports()
    {
        // mouse reports arrive only every other tick; the stick must not fall to zero in between
        var t = new MouseStickTranslator(new MouseSettings { Deadzone = 0, Smoothing = 0, Curve = 1 });
        double min = 1;
        for (var i = 0; i < 60; i++)
        {
            var x = t.Update(i % 2 == 0 ? CountsFor(1.0) : 0, 0, Dt).X;
            if (i > 20) min = Math.Min(min, x);
        }
        Assert.True(min > 0.2, $"stick dipped to {min:F2} between reports");
    }

    [Fact]
    public void BaselineAveragingStillRecentresQuicklyWhenMouseStops()
    {
        var t = new MouseStickTranslator(new MouseSettings { Deadzone = 0, Smoothing = 0, Curve = 1 });
        for (var i = 0; i < 30; i++) t.Update(CountsFor(1.0), 0, Dt);
        double v = 1; var ticks = 0;
        while (v > 0 && ticks < 100) { v = t.Update(0, 0, Dt).X; ticks++; }
        Assert.True(ticks <= 12, $"took {ticks} ticks (~{ticks * 8} ms) to recentre");
    }

    [Fact]
    public void DiagonalDirectionIsPreserved()
    {
        var (x, y) = T().Update(CountsFor(0.3), -CountsFor(0.3), Dt);
        Assert.InRange(x / y, 0.99, 1.01);
    }

    [Fact]
    public void OutputIsIndependentOfTickLength()
    {
        // same physical speed measured over 4 ms and 16 ms
        var a = T().Update(MouseSettings.ReferenceRate * 0.5 * 0.004, 0, 0.004).X;
        var b = T().Update(MouseSettings.ReferenceRate * 0.5 * 0.016, 0, 0.016).X;
        Assert.InRange(Math.Abs(a - b), 0, 0.001);
    }

    [Fact]
    public void SettingsAreClamped()
    {
        var s = new MouseSettings { SensitivityX = 999, Deadzone = 5, MaxOutput = -1, Smoothing = 3, Curve = 0 };
        s.Clamp();
        Assert.Equal(20, s.SensitivityX);
        Assert.Equal(0.6, s.Deadzone);
        Assert.Equal(0.1, s.MaxOutput);
        Assert.Equal(0.95, s.Smoothing);
        Assert.Equal(0.3, s.Curve);
    }
}

public class ProfileTests
{
    [Fact]
    public void BuiltInProfilesAreValidAndConflictFreeWhereExpected()
    {
        foreach (var p in DefaultProfiles.All())
        {
            Assert.Empty(p.Validate());
            Assert.NotEmpty(p.Bindings);
        }
    }

    [Fact]
    public void ForzaDefaultsCoverDrivingActions()
    {
        var p = DefaultProfiles.CreateForza();
        Assert.Contains("Key:W", p.Bindings[PadControl.RightTrigger]);
        Assert.Contains("Key:S", p.Bindings[PadControl.LeftTrigger]);
        Assert.Contains("Key:A", p.Bindings[PadControl.LeftStickLeft]);
        Assert.Contains("Key:D", p.Bindings[PadControl.LeftStickRight]);
        Assert.Contains("Key:Space", p.Bindings[PadControl.A]);
        Assert.Equal(MouseTarget.RightStick, p.Mouse.Target);
    }

    [Fact]
    public void SerializationRoundTrips()
    {
        var p = DefaultProfiles.CreateForza();
        p.InputScales["Key:W"] = 0.7;
        p.Mouse.SensitivityX = 1.7;
        var json = System.Text.Json.JsonSerializer.Serialize(p, Profile.Json);
        Assert.Contains("\"RightTrigger\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<Profile>(json, Profile.Json)!;
        Assert.Equal(p.Name, back.Name);
        Assert.Equal(1.7, back.Mouse.SensitivityX);
        Assert.Equal(0.7, back.InputScales["Key:W"]);
        Assert.Equal(p.Bindings[PadControl.A], back.Bindings[PadControl.A]);
    }

    [Fact]
    public void ValidateDropsUnknownInputsAndClampsValues()
    {
        var p = new Profile { Name = "x" };
        p.Bindings[PadControl.A] = ["Key:Space", "Key:Bogus"];
        p.Bindings[PadControl.B] = ["Nonsense"];
        p.InputScales["Key:Space"] = 5;
        p.Mouse.SensitivityX = 1000;
        var problems = p.Validate();
        Assert.Equal(2, problems.Count);
        Assert.Single(p.Bindings[PadControl.A]);
        Assert.False(p.Bindings.ContainsKey(PadControl.B));
        Assert.Equal(1, p.InputScales["Key:Space"]);
        Assert.Equal(20, p.Mouse.SensitivityX);
    }

    [Fact]
    public void DetectsBindingConflicts()
    {
        var p = new Profile { Name = "x" };
        InputId.TryParse("Key:Space", out var space);
        p.Bind(PadControl.A, space);
        Assert.Empty(p.FindConflicts(space, PadControl.A));
        Assert.Equal([PadControl.A], p.FindConflicts(space, PadControl.B));
        p.Bind(PadControl.B, space);
        Assert.Single(p.AllDuplicates());
        p.Unbind(PadControl.A, space);
        Assert.Empty(p.AllDuplicates());
    }

    [Fact]
    public void BindingTwiceDoesNotDuplicate()
    {
        var p = new Profile { Name = "x" };
        InputId.TryParse("Key:E", out var e);
        p.Bind(PadControl.X, e); p.Bind(PadControl.X, e);
        Assert.Single(p.Bindings[PadControl.X]);
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var a = DefaultProfiles.CreateDefault();
        var b = a.Clone();
        b.Bindings.Clear();
        b.Mouse.SensitivityX = 9;
        Assert.NotEmpty(a.Bindings);
        Assert.NotEqual(9, a.Mouse.SensitivityX);
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F8", true, true, false, 0x77)]
    [InlineData("alt+F9", false, false, true, 0x78)]
    [InlineData("Ctrl+Alt+K", true, false, true, 0x4B)]
    public void Parses(string text, bool ctrl, bool shift, bool alt, int vk)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(new Hotkey(ctrl, shift, alt, vk), h);
    }

    [Theory]
    [InlineData("")] [InlineData("Ctrl+Shift")] [InlineData("Ctrl+Bogus")]
    public void RejectsInvalid(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void ReservedCombosAreFlagged()
    {
        Assert.True(Hotkey.IsReserved(new Hotkey(false, false, true, 0x73)));   // Alt+F4
        Assert.True(Hotkey.IsReserved(new Hotkey(true, false, false, 0x43)));   // Ctrl+C
        Assert.True(Hotkey.IsReserved(new Hotkey(false, false, false, 0x77)));  // bare F8
        Assert.False(Hotkey.IsReserved(new Hotkey(true, true, false, 0x77)));
    }
}
