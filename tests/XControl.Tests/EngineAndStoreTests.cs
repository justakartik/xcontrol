using XControl.Core;

namespace XControl.Tests;

sealed class FakePad : IVirtualPad
{
    public readonly List<PadState> Reports = [];
    public bool IsConnected { get; set; }
    public bool FailNextSubmit, FailConnect, DriverMissing;
    public int ConnectCalls, DisconnectCalls;
    readonly object _l = new();

    public void Connect()
    {
        ConnectCalls++;
        if (DriverMissing) throw new PadException("driver missing", true);
        if (FailConnect) throw new PadException("cannot create");
        IsConnected = true;
    }
    public void Disconnect() { DisconnectCalls++; IsConnected = false; }
    public void Submit(PadState s)
    {
        if (FailNextSubmit) { FailNextSubmit = false; IsConnected = false; throw new PadException("device removed"); }
        lock (_l) Reports.Add(s);
    }
    public PadState? Last { get { lock (_l) return Reports.Count > 0 ? Reports[^1] : null; } }
    public void Dispose() => Disconnect();
}

public class EngineTests
{
    static InputId K(string n) { InputId.TryParse("Key:" + n, out var id); return id; }

    static bool WaitFor(Func<bool> cond, int ms = 3000)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(5); }
        return cond();
    }

    static (ControllerEngine e, FakePad pad) Make(Action<Profile>? edit = null)
    {
        var pad = new FakePad();
        var e = new ControllerEngine(() => pad, 250);
        var p = DefaultProfiles.CreateForza();
        edit?.Invoke(p);
        e.SetProfile(p);
        e.Start();
        return (e, pad);
    }

    [Fact]
    public void DisabledEngineSendsNothingAndNeverConnects()
    {
        var (e, pad) = Make();
        using (e)
        {
            e.Input(K("W"), true);
            e.MouseMove(500, 500);
            Thread.Sleep(150);
            Assert.Empty(pad.Reports);
            Assert.Equal(0, pad.ConnectCalls);
            Assert.False(e.ShouldHandle(K("W")));
        }
    }

    [Fact]
    public void ConnectSendsWakeNudgeThenNeutralToClearStaleInitialState()
    {
        var (e, pad) = Make();
        using (e)
        {
            e.SetEnabled(true);
            Assert.True(WaitFor(() => pad.Reports.Count >= 2));
            Assert.Equal(1, pad.Reports[0].LeftX);                 // 1-count nudge
            Assert.True(pad.Reports[1].IsNeutral);                 // then clean neutral
            Assert.Equal(PadButtons.None, pad.Reports[0].Buttons);
            Assert.Equal(0, pad.Reports[0].LeftTrigger);
        }
    }

    [Fact]
    public void EnablingConnectsAndKeysReachThePad()
    {
        var (e, pad) = Make();
        using (e)
        {
            e.SetEnabled(true);
            Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running));
            Assert.True(e.ShouldHandle(K("W")));
            e.Input(K("W"), true);
            e.Input(K("Space"), true);
            Assert.True(WaitFor(() => pad.Last is { RightTrigger: 255 } l && l.Buttons.HasFlag(PadButtons.A)));
        }
    }

    [Fact]
    public void DisablingReleasesEverythingAndDisconnects()
    {
        var (e, pad) = Make();
        using (e)
        {
            e.SetEnabled(true);
            Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running));
            e.Input(K("W"), true); e.Input(K("A"), true); e.Input(K("Space"), true);
            e.MouseMove(400, 0);
            Assert.True(WaitFor(() => pad.Last is { RightTrigger: 255 }));

            e.SetEnabled(false);
            Assert.True(pad.Last!.Value.IsNeutral, "last report after OFF must be neutral");
            Assert.False(pad.IsConnected);
            Assert.False(e.ShouldHandle(K("W")));

            // input after OFF must not leak
            var before = pad.Reports.Count;
            e.Input(K("W"), true);
            e.MouseMove(900, 900);
            Thread.Sleep(120);
            Assert.Equal(before, pad.Reports.Count);
        }
    }

    [Fact]
    public void TurningBackOnStartsFromNeutralNotFromStaleKeys()
    {
        var (e, pad) = Make();
        using (e)
        {
            e.SetEnabled(true);
            Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running));
            e.Input(K("W"), true);
            Assert.True(WaitFor(() => pad.Last is { RightTrigger: 255 }));
            e.SetEnabled(false);
            e.SetEnabled(true);
            Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running));
            Thread.Sleep(60);
            Assert.True(pad.Last!.Value.IsNeutral);
            e.Input(K("W"), true);
            Assert.True(WaitFor(() => pad.Last is { RightTrigger: 255 }));
        }
    }

    [Fact]
    public void LosingFocusReleasesHeldInputs()
    {
        var (e, pad) = Make();
        using (e)
        {
            e.SetEnabled(true);
            Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running));
            e.Input(K("W"), true);
            Assert.True(WaitFor(() => pad.Last is { RightTrigger: 255 }));
            e.FocusActive = false;
            Assert.True(WaitFor(() => pad.Last is { RightTrigger: 0 }));
            Assert.False(e.ShouldHandle(K("W")));
            e.Input(K("W"), true); // ignored while unfocused
            Thread.Sleep(60);
            Assert.Equal(0, pad.Last!.Value.RightTrigger);
            e.FocusActive = true;
            Thread.Sleep(60);
            Assert.Equal(0, pad.Last!.Value.RightTrigger); // key was ignored, stays released
        }
    }

    [Fact]
    public void KeyUpReleasesTheButton()
    {
        var (e, pad) = Make();
        using (e)
        {
            e.SetEnabled(true);
            Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running));
            e.Input(K("Space"), true);
            Assert.True(WaitFor(() => pad.Last is { } l && l.Buttons.HasFlag(PadButtons.A)));
            e.Input(K("Space"), false);
            Assert.True(WaitFor(() => pad.Last is { } l && !l.Buttons.HasFlag(PadButtons.A)));
        }
    }

    [Fact]
    public void MouseMovementDrivesRightStickThenRecentres()
    {
        var (e, pad) = Make(p => { p.Mouse.Smoothing = 0; p.Mouse.Deadzone = 0; });
        using (e)
        {
            e.SetEnabled(true);
            Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running));
            for (var i = 0; i < 40; i++) { e.MouseMove(40, 0); Thread.Sleep(4); }
            Assert.True(pad.Reports.Any(r => r.RightX > 3000), "right stick should move right");
            Assert.True(WaitFor(() => pad.Last is { RightX: 0, RightY: 0 }), "stick must recentre once the mouse stops");
        }
    }

    [Fact]
    public void SubmitFailureIsReportedAndEngineReconnects()
    {
        var (e, pad) = Make();
        using (e)
        {
            e.SetEnabled(true);
            Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running));
            Assert.Equal(1, pad.ConnectCalls);
            pad.FailNextSubmit = true;
            e.Input(K("W"), true);
            Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Faulted || pad.ConnectCalls >= 2, 3000));
            // reconnect attempt happens within ~2 s
            Assert.True(WaitFor(() => pad.ConnectCalls >= 2 && e.Snapshot().Status == EngineStatus.Running, 6000));
            Assert.True(WaitFor(() => pad.Last is { RightTrigger: 255 }), "held key is re-applied after recovery");
        }
    }

    [Fact]
    public void MissingDriverIsReportedNotThrown()
    {
        var pad = new FakePad { DriverMissing = true };
        using var e = new ControllerEngine(() => pad, 250);
        e.SetProfile(DefaultProfiles.CreateForza());
        e.Start();
        e.SetEnabled(true);
        Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.DriverMissing));
        Assert.NotNull(e.Snapshot().Error);
        Assert.False(e.ShouldHandle(K("W")), "keys must not be swallowed when the pad cannot work");
        pad.DriverMissing = false; // user installs the driver
        e.Reconnect();
        Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running, 6000));
    }

    [Fact]
    public void DisposeNeutralisesAndDisconnects()
    {
        var (e, pad) = Make();
        e.SetEnabled(true);
        Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running));
        e.Input(K("W"), true);
        Assert.True(WaitFor(() => pad.Last is { RightTrigger: 255 }));
        e.Dispose();
        Assert.True(pad.Last!.Value.IsNeutral);
        Assert.False(pad.IsConnected);
    }

    [Fact]
    public void ProfileSwitchChangesMappingAndReleasesInputs()
    {
        var (e, pad) = Make();
        using (e)
        {
            e.SetEnabled(true);
            Assert.True(WaitFor(() => e.Snapshot().Status == EngineStatus.Running));
            e.Input(K("W"), true);
            Assert.True(WaitFor(() => pad.Last is { RightTrigger: 255 }));
            e.SetProfile(DefaultProfiles.CreateDefault()); // W is left-stick-up here
            Assert.True(WaitFor(() => pad.Last is { RightTrigger: 0 }));
            e.Input(K("W"), true);
            Assert.True(WaitFor(() => pad.Last is { LeftY: > 30000 }));
        }
    }

    [Fact]
    public void TickRateIsMeasured()
    {
        var (e, _) = Make();
        using (e)
        {
            Thread.Sleep(1600);
            Assert.InRange(e.Snapshot().TickRateHz, 10, 40);   // OFF: idles at ~20 Hz
            e.SetEnabled(true);
            Thread.Sleep(2600);
            Assert.InRange(e.Snapshot().TickRateHz, 150, 300); // ON: configured 250 Hz
        }
    }
}

public class StoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "xctest_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void FirstRunCreatesBuiltInProfiles()
    {
        var s = new ProfileStore(_dir);
        var list = s.LoadProfiles();
        Assert.Contains(list, p => p.Name == DefaultProfiles.ForzaName);
        Assert.Contains(list, p => p.Name == DefaultProfiles.DefaultName);
    }

    [Fact]
    public void ProfilesAndSettingsPersistAcrossInstances()
    {
        var s1 = new ProfileStore(_dir);
        var p = DefaultProfiles.CreateForza();
        p.Name = "My Forza"; p.BuiltIn = false;
        p.Bind(PadControl.Y, InputId.Key(0x5A));
        p.Mouse.SensitivityX = 1.33;
        s1.Save(p);
        s1.SaveSettings(new AppSettings { ActiveProfile = "My Forza", ToggleHotkey = "Ctrl+Alt+K", UpdateRateHz = 200 });

        var s2 = new ProfileStore(_dir);
        var loaded = s2.LoadProfiles().Single(x => x.Name == "My Forza");
        Assert.Equal(1.33, loaded.Mouse.SensitivityX);
        Assert.Contains("Key:Z", loaded.Bindings[PadControl.Y]);
        var st = s2.LoadSettings();
        Assert.Equal("My Forza", st.ActiveProfile);
        Assert.Equal("Ctrl+Alt+K", st.ToggleHotkey);
        Assert.Equal(200, st.UpdateRateHz);
    }

    [Fact]
    public void CorruptProfileIsQuarantinedAndDefaultsRestored()
    {
        var s = new ProfileStore(_dir);
        s.LoadProfiles();
        var forza = Path.Combine(_dir, "profiles", "Forza Horizon 5.json");
        File.WriteAllText(forza, "{ this is not json");
        var list = new ProfileStore(_dir).LoadProfiles();
        Assert.Contains(list, p => p.Name == DefaultProfiles.ForzaName && p.Bindings.Count > 0);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "profiles"), "*.bad"));
    }

    [Fact]
    public void CorruptSettingsFallBackToDefaults()
    {
        var s = new ProfileStore(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "\0\0garbage");
        var st = s.LoadSettings();
        Assert.Equal(new AppSettings().ToggleHotkey, st.ToggleHotkey);
        Assert.NotEmpty(s.Warnings);
    }

    [Fact]
    public void ImportExportRoundTripAndRenameOnClash()
    {
        var s = new ProfileStore(_dir);
        var p = DefaultProfiles.CreateForza();
        var file = Path.Combine(_dir, "export.json");
        s.Export(p, file);
        s.LoadProfiles();
        var imported = s.Import(file);
        Assert.Equal("Forza Horizon 5 (2)", imported.Name);
        Assert.False(imported.BuiltIn);
        Assert.Equal(p.Bindings[PadControl.A], imported.Bindings[PadControl.A]);
    }

    [Fact]
    public void ImportRejectsGarbage()
    {
        var s = new ProfileStore(_dir);
        var file = Path.Combine(_dir, "bad.json");
        File.WriteAllText(file, "[1,2,3]");
        Assert.Throws<InvalidDataException>(() => s.Import(file));
    }

    [Fact]
    public void DeleteRemovesProfileFile()
    {
        var s = new ProfileStore(_dir);
        var p = new Profile { Name = "Temp" };
        s.Save(p);
        Assert.Contains(s.LoadProfiles(), x => x.Name == "Temp");
        s.Delete("Temp");
        Assert.DoesNotContain(s.LoadProfiles(), x => x.Name == "Temp");
    }

    [Fact]
    public void UnsafeProfileNamesMakeSafeFileNames()
    {
        var s = new ProfileStore(_dir);
        s.Save(new Profile { Name = "a/b:c*d" });
        Assert.Contains(s.LoadProfiles(), x => x.Name == "a/b:c*d");
    }
}
