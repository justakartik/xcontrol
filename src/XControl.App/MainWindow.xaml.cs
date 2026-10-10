using System.IO;
using System.Windows.Automation;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using XControl.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Ellipse = System.Windows.Shapes.Ellipse;
using MessageBox = System.Windows.MessageBox;
using Path = System.IO.Path;
using TextBox = System.Windows.Controls.TextBox;

namespace XControl.App;

public sealed record ActionRow(PadControl Control, string Label, string Bound)
{
    /// <summary>Read by screen readers and UI Automation as the row's name.</summary>
    public override string ToString() => $"{Label}: {Bound}";
}

public partial class MainWindow : Window
{
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B));
    static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xD9, 0xA4, 0x00));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xD1, 0x43, 0x43));
    static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    static readonly Brush BoundBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x2E, 0x9E, 0x5B));
    static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x3A, 0x96, 0xDD));
    static readonly Brush HeldBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xE0, 0x8A, 0x00));

    static readonly Dictionary<PadControl, string> Labels = new()
    {
        [PadControl.A] = "A", [PadControl.B] = "B", [PadControl.X] = "X", [PadControl.Y] = "Y",
        [PadControl.DpadUp] = "D-pad Up", [PadControl.DpadDown] = "D-pad Down", [PadControl.DpadLeft] = "D-pad Left", [PadControl.DpadRight] = "D-pad Right",
        [PadControl.LeftBumper] = "Left Bumper (LB)", [PadControl.RightBumper] = "Right Bumper (RB)",
        [PadControl.LeftTrigger] = "Left Trigger (LT)", [PadControl.RightTrigger] = "Right Trigger (RT)",
        [PadControl.LeftStickClick] = "Left Stick Click (L3)", [PadControl.RightStickClick] = "Right Stick Click (R3)",
        [PadControl.Menu] = "Menu", [PadControl.View] = "View",
        [PadControl.LeftStickUp] = "Left Stick Up", [PadControl.LeftStickDown] = "Left Stick Down",
        [PadControl.LeftStickLeft] = "Left Stick Left", [PadControl.LeftStickRight] = "Left Stick Right",
        [PadControl.RightStickUp] = "Right Stick Up", [PadControl.RightStickDown] = "Right Stick Down",
        [PadControl.RightStickLeft] = "Right Stick Left", [PadControl.RightStickRight] = "Right Stick Right",
    };

    readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _commitDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Dictionary<string, (TextBlock Value, Ellipse Dot)> _status = [];
    readonly Dictionary<InputId, Button> _keyButtons = [];
    readonly Dictionary<InputId, Button> _mouseButtons = [];
    readonly HashSet<InputId> _pvHeld = [];
    readonly List<Action> _sliderRefresh = [];
    System.Windows.Forms.NotifyIcon? _tray;
    System.Windows.Forms.ToolStripMenuItem? _trayToggle;
    List<Profile> _profiles = [];
    Profile _active = null!;
    CompiledProfile _compiled = null!;
    bool _loading, _exiting;
    Action<InputId>? _captureInput;
    Action<Hotkey>? _captureHotkey;
    Ellipse _lsDot = null!, _rsDot = null!, _testDot = null!;
    Line _testRaw = null!;

    public MainWindow()
    {
        InitializeComponent();
        AboutText.Text = $"XControl {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}\n" +
                         "Virtual controller driver: ViGEmBus 1.22.0 (retired upstream, signed by Nefarius Software Solutions e.U.).\n" +
                         "Updates: none automatic — check the project folder for new releases.";
        BuildStatusRows();
        BuildPadCanvases();
        BuildKeyboard();
        BuildMouseButtons();
        BuildMouseSettingsUi();
        BuildSettingsUi();
        BuildTray();

        _profiles = Services.Store.LoadProfiles();
        _loading = true;
        ProfileCombo.ItemsSource = _profiles.Select(p => p.Name).ToList();
        var chosen = _profiles.FirstOrDefault(p => p.Name == Services.Settings.ActiveProfile) ?? _profiles[0];
        ProfileCombo.SelectedItem = chosen.Name;
        _loading = false;
        SelectProfile(chosen, persist: false);
        ProfileWarn.Text = string.Join("\n", Services.Store.Warnings);

        Services.Hook.ToggleRequested = () =>
        {
            Services.Engine.SetEnabled(!Services.Engine.Enabled);
            Log.Info("Toggled via global shortcut → " + (Services.Engine.Enabled ? "ON" : "OFF"));
            (Services.Engine.Enabled ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand).Play();
        };

        _ui.Tick += (_, _) => UpdateUi();
        _ui.Start();
        _commitDebounce.Tick += (_, _) => { _commitDebounce.Stop(); CommitProfile(); };

        if (Services.Settings.EnableOnLaunch) Services.Engine.SetEnabled(true);
    }

    // =========================================================================================
    // Header / dashboard
    // =========================================================================================

    void Toggle_Click(object sender, RoutedEventArgs e) => SetEnabled(!Services.Engine.Enabled);

    bool _syncing;

    void AnyWindow_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var any = AnyWindowBox.IsChecked == true;
        Services.Settings.OnlyWhenBrowserFocused = !any;
        Services.SaveSettings();
        Services.ApplyRuntimeSettings();
        Log.Info("Work in any window: " + (any ? "ON" : "OFF"));
        if (OnlyBrowserBox != null) OnlyBrowserBox.IsChecked = !any;
    }

    void SetEnabled(bool on)
    {
        Services.Engine.SetEnabled(on);
        Log.Info("Controller toggled " + (on ? "ON" : "OFF") + " from the UI.");
        UpdateUi();
    }

    void BuildStatusRows()
    {
        string[] names = ["Virtual controller", "Driver (ViGEmBus)", "Input capture", "Foreground window", "Xbox Cloud page",
            "Active profile", "Input mode", "Active bindings", "Input activity", "Engine timing"];
        for (var i = 0; i < names.Length; i++)
        {
            StatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = names[i], Margin = new Thickness(0, 4, 8, 4), Opacity = 0.75 };
            var dot = new Ellipse { Width = 10, Height = 10, Fill = Grey, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var val = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(dot); sp.Children.Add(val);
            Grid.SetRow(label, i); Grid.SetRow(sp, i); Grid.SetColumn(sp, 1);
            StatusGrid.Children.Add(label); StatusGrid.Children.Add(sp);
            _status[names[i]] = (val, dot);
        }
    }

    void Set(string key, string text, Brush dot)
    {
        var (v, d) = _status[key];
        if (v.Text != text) v.Text = text;
        d.Fill = dot;
    }

    void BuildPadCanvases()
    {
        Ellipse Ring(Canvas c)
        {
            c.Children.Add(new Ellipse { Width = c.Width - 4, Height = c.Height - 4, Stroke = Grey, StrokeThickness = 1.5, Margin = new Thickness(2) });
            var dot = new Ellipse { Width = 14, Height = 14, Fill = SelectedBrush };
            c.Children.Add(dot);
            return dot;
        }
        _lsDot = Ring(LsCanvas);
        _rsDot = Ring(RsCanvas);
        _testDot = Ring(TestCanvas);
        TestCanvas.Children.Add(new Line { X1 = 120, Y1 = 4, X2 = 120, Y2 = 236, Stroke = Grey, Opacity = 0.4 });
        TestCanvas.Children.Add(new Line { X1 = 4, Y1 = 120, X2 = 236, Y2 = 120, Stroke = Grey, Opacity = 0.4 });
        _testRaw = new Line { X1 = 120, Y1 = 120, X2 = 120, Y2 = 120, Stroke = Amber, StrokeThickness = 3 };
        TestCanvas.Children.Add(_testRaw);
    }

    static void Place(Ellipse dot, Canvas c, double x, double y)
    {
        var r = (c.Width - 4) / 2 - 7;
        Canvas.SetLeft(dot, c.Width / 2 - 7 + x * r);
        Canvas.SetTop(dot, c.Height / 2 - 7 - y * r);
    }

    void UpdateUi()
    {
        // nothing to draw while hidden in the tray or minimised
        if (!IsVisible || WindowState == WindowState.Minimized) { if (_trayToggle != null) UpdateTrayOnly(); return; }
        var s = Services.Engine.Snapshot();
        var mon = Services.Monitor;
        var hook = Services.Hook;

        var anyWin = !Services.Settings.OnlyWhenBrowserFocused;
        if (AnyWindowBox.IsChecked != anyWin) { _syncing = true; AnyWindowBox.IsChecked = anyWin; _syncing = false; }
        ToggleBtn.Content = s.Enabled ? "ON" : "OFF";
        var running = s.Enabled && s.Status == EngineStatus.Running;
        ToggleBtn.Background = running ? Green : s.Enabled ? Amber : Grey;
        ToggleBtn.Foreground = Brushes.White;
        HeaderDot.Fill = running ? Green : s.Enabled ? Amber : Grey;
        HeaderStatus.Text = !s.Enabled ? "Controller is OFF"
            : running ? (s.FocusActive ? "Controller is ON — translating input" : "Controller is ON — paused (browser not focused)")
            : $"Controller requested ON — {StatusName(s.Status)}";
        HeaderHint.Text = !s.Enabled ? $"Nothing is sent to the game. Press {Services.Settings.ToggleHotkey} anywhere to switch it on."
            : s.Error ?? (s.FocusActive ? $"Press {Services.Settings.ToggleHotkey} to switch off. Mapped keys are hidden from other apps while ON."
                                        : "Focus your browser or the Xbox app (or tick \"Work in any window\") to start translating.");
        if (_trayToggle != null) _trayToggle.Text = s.Enabled ? "Turn controller OFF" : "Turn controller ON";
        if (_tray != null) _tray.Text = "XControl — " + (s.Enabled ? "ON" : "OFF");

        var driver = Services.IsDriverInstalled();
        Set("Virtual controller", s.PadConnected ? "Connected — Xbox 360 controller (XInput)"
            : s.Enabled ? StatusName(s.Status) : "Not connected (created when switched ON)",
            s.PadConnected ? Green : s.Enabled ? Red : Grey);
        Set("Driver (ViGEmBus)", driver ? "Installed" : "NOT installed — see Troubleshooting", driver ? Green : Red);
        Set("Input capture", hook.IsRunning ? $"Hooks active ({hook.KeyEvents} key, {hook.MouseEvents} button, {hook.RawMoves} move events seen)"
            : "FAILED — " + hook.Error, hook.IsRunning ? Green : Red);
        Set("Foreground window", string.IsNullOrEmpty(mon.ProcessName) ? "—" : $"{mon.ProcessName} — {Trunc(mon.WindowTitle, 48)}",
            mon.IsBrowser ? Green : Grey);
        Set("Xbox Cloud page", mon.LooksLikeXboxCloud ? "Browser window title mentions Xbox"
            : mon.IsBrowser ? "Browser focused (page not confirmed — titles vary)" : "No browser focused",
            mon.LooksLikeXboxCloud ? Green : Grey);
        Set("Active profile", _active.Name, Green);
        Set("Input mode", Services.Settings.OnlyWhenBrowserFocused ? "Keyboard + mouse → Xbox 360 pad, browser only" : "Keyboard + mouse → Xbox 360 pad, any window", Green);
        Set("Active bindings", s.ActiveBindings.ToString(), s.ActiveBindings > 0 ? Green : Amber);
        var activity = !s.Pad.IsNeutral || Math.Abs(s.MouseRawX) + Math.Abs(s.MouseRawY) > 1;
        Set("Input activity", activity ? "Active" : "Idle", activity ? Green : Grey);
        Set("Engine timing", $"{s.TickRateHz:F0} Hz, worst tick {s.MaxTickMs:F2} ms, late ticks {s.LateTicks}, reports {s.Submits}; key→report avg {s.InputLatencyAvgMs:F1} / max {s.InputLatencyMaxMs:F1} ms ({s.InputLatencySamples} samples); hook avg {hook.HookAvgUs:F1} / max {hook.HookMaxUs:F0} µs", Grey);

        ErrorBox.Visibility = s.Error != null ? Visibility.Visible : Visibility.Collapsed;
        if (s.Error != null) ErrorText.Text = s.Error + (s.Status == EngineStatus.DriverMissing ? "\nSee Troubleshooting for how to install the driver." : "");

        var p = s.Pad;
        Place(_lsDot, LsCanvas, p.LeftX / 32767.0, p.LeftY / 32767.0);
        Place(_rsDot, RsCanvas, p.RightX / 32767.0, p.RightY / 32767.0);
        LtBar.Value = p.LeftTrigger; RtBar.Value = p.RightTrigger;
        var held = Enum.GetValues<PadButtons>().Where(b => b != PadButtons.None && p.Buttons.HasFlag(b)).Select(b => b.ToString());
        ButtonsText.Text = held.Any() ? string.Join("  ", held) : "—";
        AxesText.Text = $"LS ({p.LeftX,6},{p.LeftY,6})  RS ({p.RightX,6},{p.RightY,6})\nLT {p.LeftTrigger,3}  RT {p.RightTrigger,3}";

        var mouseTab = Tabs.SelectedItem is TabItem { Header: "Mouse" };
        Services.Engine.PreviewMouse = mouseTab;
        if (mouseTab)
        {
            Place(_testDot, TestCanvas, s.MouseStickX, s.MouseStickY);
            var k = 100.0 / MouseSettings.ReferenceRate;
            _testRaw.X2 = 120 + Math.Clamp(s.MouseRawX * k, -110, 110);
            _testRaw.Y2 = 120 + Math.Clamp(s.MouseRawY * k, -110, 110);
            TestRaw.Text = $"Raw mouse   X {s.MouseRawX,8:F0}  Y {s.MouseRawY,8:F0} counts/s";
            TestStick.Text = $"Right stick X {s.MouseStickX,7:F3}  Y {s.MouseStickY,7:F3}";
        }
    }

    void UpdateTrayOnly()
    {
        var on = Services.Engine.Enabled;
        if (_trayToggle != null) _trayToggle.Text = on ? "Turn controller OFF" : "Turn controller ON";
        if (_tray != null) _tray.Text = "XControl - " + (on ? "ON" : "OFF");
    }

    static string StatusName(EngineStatus s) => s switch
    {
        EngineStatus.Off => "off",
        EngineStatus.Starting => "connecting to the virtual controller…",
        EngineStatus.Running => "running",
        EngineStatus.Paused => "paused",
        EngineStatus.DriverMissing => "ViGEmBus driver missing",
        EngineStatus.Faulted => "device error (retrying)",
        _ => s.ToString(),
    };

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("Manual reconnect requested.");
        Services.Engine.Reconnect();
    }

    void Troubleshoot_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, TroubleshootText(), "Troubleshooting", MessageBoxButton.OK, MessageBoxImage.Information);

    string TroubleshootText()
    {
        var s = Services.Engine.Snapshot();
        var sb = new StringBuilder();
        if (!Services.IsDriverInstalled())
            sb.AppendLine("• The ViGEmBus driver is not installed. Run redist\\ViGEmBus_1.22.0_x64_x86_arm64.exe (approve the administrator prompt), then press Reconnect.");
        if (s.Status == EngineStatus.DriverMissing) sb.AppendLine("• Driver present on disk but not loaded: reboot once, then press Reconnect.");
        if (s.Status == EngineStatus.Faulted) sb.AppendLine($"• Device error: {s.Error}. Press Reconnect; if it repeats, reinstall ViGEmBus.");
        if (!Services.Hook.IsRunning) sb.AppendLine("• Keyboard/mouse capture failed to start. Another tool may block global hooks, or Windows policy forbids it.");
        sb.AppendLine("• Browser does not see the pad: the page must be focused and a button pressed once (browsers hide gamepads until first input). Use 'Open browser gamepad test' on the Dashboard — it should list \"Xbox 360 Controller (XInput STANDARD GAMEPAD)\".");
        sb.AppendLine("• Keys do nothing: the controller must be ON and the browser focused (Settings → 'Only translate while a browser is focused'). Elevated (admin) windows cannot be captured by a normal-user app.");
        sb.AppendLine("• Mouse moves the cursor instead of the camera: leave 'Freeze cursor while active' enabled on the Mouse tab.");
        sb.AppendLine("• Stuck input: switch the controller OFF (or press the global shortcut) — all buttons and sticks are released immediately.");
        sb.AppendLine("• Xbox Cloud Gaming may prefer a physical pad's prompts; start the game once with the controller ON so it detects the pad.");
        return sb.ToString();
    }

    void OpenGamepadTest_Click(object sender, RoutedEventArgs e)
    {
        // the page ships inside the exe; write it out so the default browser can open it
        var f = Path.Combine(Services.Store.Root, "gamepad-test.html");
        using (var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("gamepad-test.html"))
        {
            if (rs == null) { MessageBox.Show(this, "The gamepad test page is missing from this build."); return; }
            using var fs = File.Create(f);
            rs.CopyTo(fs);
        }
        Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
    }

    void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(Log.Directory ?? Services.Store.Root);
    void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Services.Store.Root);
    static void OpenFolder(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    // =========================================================================================
    // Profiles
    // =========================================================================================

    void ProfileCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileCombo.SelectedItem is not string name) return;
        var p = _profiles.FirstOrDefault(x => x.Name == name);
        if (p != null) SelectProfile(p, persist: true);
    }

    void SelectProfile(Profile p, bool persist)
    {
        _active = p;
        _compiled = new CompiledProfile(p);
        Services.Engine.SetProfile(p);
        if (persist) { Services.Settings.ActiveProfile = p.Name; Services.SaveSettings(); Log.Info("Profile switched to " + p.Name); }
        ProfileTitle.Text = p.Name + (p.BuiltIn ? "  (built-in)" : "");
        ProfileDesc.Text = p.Description ?? "Custom profile.";
        RefreshAll();
    }

    void RefreshProfileCombo(string select)
    {
        _loading = true;
        ProfileCombo.ItemsSource = _profiles.Select(p => p.Name).ToList();
        ProfileCombo.SelectedItem = select;
        _loading = false;
    }

    void CommitProfile()
    {
        var problems = _active.Validate();
        Services.Store.Save(_active);
        _compiled = new CompiledProfile(_active);
        Services.Engine.SetProfile(_active);
        RefreshAll(skipSliders: true);
        if (problems.Count > 0) Log.Warn("Profile repaired: " + string.Join("; ", problems));
    }

    void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("New profile", "Name for the new profile (starts from the Default layout):", "My profile");
        if (name == null) return;
        var p = DefaultProfiles.CreateDefault();
        p.Name = UniqueName(name); p.BuiltIn = false; p.Description = "Custom profile.";
        AddProfile(p);
    }

    void DupProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Duplicate profile", "Name for the copy:", _active.Name + " copy");
        if (name == null) return;
        var p = _active.Clone();
        p.Name = UniqueName(name); p.BuiltIn = false;
        AddProfile(p);
    }

    void AddProfile(Profile p)
    {
        Services.Store.Save(p);
        _profiles.Add(p);
        RefreshProfileCombo(p.Name);
        SelectProfile(p, persist: true);
    }

    string UniqueName(string name)
    {
        name = name.Trim();
        var n = name;
        for (var i = 2; _profiles.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); i++) n = $"{name} ({i})";
        return n;
    }

    void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_active.BuiltIn) { MessageBox.Show(this, "Built-in profiles cannot be renamed. Duplicate it instead."); return; }
        var name = Prompt("Rename profile", "New name:", _active.Name);
        if (name == null || name.Trim() == _active.Name) return;
        Services.Store.Delete(_active.Name);
        _active.Name = UniqueName(name);
        Services.Store.Save(_active);
        RefreshProfileCombo(_active.Name);
        SelectProfile(_active, persist: true);
    }

    void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_active.BuiltIn) { MessageBox.Show(this, "Built-in profiles cannot be deleted (use 'Restore defaults')."); return; }
        if (MessageBox.Show(this, $"Delete profile '{_active.Name}'?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Services.Store.Delete(_active.Name);
        _profiles.Remove(_active);
        var next = _profiles[0];
        RefreshProfileCombo(next.Name);
        SelectProfile(next, persist: true);
    }

    void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "XControl profile (*.json)|*.json", Title = "Import profile" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var p = Services.Store.Import(dlg.FileName);
            _profiles.Add(p);
            RefreshProfileCombo(p.Name);
            SelectProfile(p, persist: true);
            Log.Info("Imported profile " + p.Name);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "XControl profile (*.json)|*.json", FileName = ProfileStore.FileSafe(_active.Name) + ".json", Title = "Export profile" };
        if (dlg.ShowDialog(this) != true) return;
        try { Services.Store.Export(_active, dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }

    void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var factory = DefaultProfiles.All().FirstOrDefault(p => p.Name == _active.Name) ?? DefaultProfiles.CreateDefault();
        if (MessageBox.Show(this, $"Reset all bindings and mouse settings of '{_active.Name}' to the {(factory.Name == _active.Name ? "factory" : "Default")} layout?",
                "Restore defaults", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _active.Bindings = factory.Bindings; _active.InputScales = factory.InputScales; _active.Mouse = factory.Mouse;
        CommitProfile();
        RefreshAll();
    }

    // =========================================================================================
    // Keybinds
    // =========================================================================================

    void RefreshAll(bool skipSliders = false)
    {
        var sel = (ActionList.SelectedItem as ActionRow)?.Control;
        ActionList.ItemsSource = Enum.GetValues<PadControl>()
            .Select(c => new ActionRow(c, Labels[c],
                _active.Bindings.TryGetValue(c, out var l) && l.Count > 0
                    ? string.Join(", ", _active.BoundInputs(c).Select(i => i.Display)) : "(unbound)")).ToList();
        if (sel != null) ActionList.SelectedItem = ((IEnumerable<ActionRow>)ActionList.ItemsSource).First(r => r.Control == sel);
        RefreshKeyColors();
        var dups = _active.AllDuplicates();
        WarnText.Text = dups.Count == 0 ? "" : "⚠ Shared inputs: " + string.Join("; ",
            dups.Select(kv => $"{kv.Key.Display} → {string.Join(" + ", kv.Value.Select(c => Labels[c]))}"));
        if (!skipSliders) foreach (var r in _sliderRefresh) r();
        UpdatePreview();
    }

    PadControl? SelectedControl => (ActionList.SelectedItem as ActionRow)?.Control;

    void ActionList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedControl != null) StartCapture(replace: true);
    }

    void Rebind_Click(object sender, RoutedEventArgs e) => StartCapture(replace: true);
    void AddInput_Click(object sender, RoutedEventArgs e) => StartCapture(replace: false);

    void StartCapture(bool replace)
    {
        if (SelectedControl is not { } c) { MessageBox.Show(this, "Select an action in the list first."); return; }
        CaptureTitle.Text = $"{(replace ? "Rebind" : "Add input to")} “{Labels[c]}”";
        CaptureSub.Text = "Press a key or click a mouse button (side buttons and wheel work too).";
        _captureHotkey = null;
        _captureInput = id => { EndCapture(); ApplyBinding(c, id, replace); };
        CaptureOverlay.Visibility = Visibility.Visible;
        CaptureOverlay.Focus();
    }

    void EndCapture()
    {
        _captureInput = null; _captureHotkey = null;
        CaptureOverlay.Visibility = Visibility.Collapsed;
    }

    void CancelCapture_Click(object sender, RoutedEventArgs e) => EndCapture();

    void ApplyBinding(PadControl c, InputId id, bool replace)
    {
        var conflicts = _active.FindConflicts(id, c);
        if (conflicts.Count > 0)
        {
            var names = string.Join(", ", conflicts.Select(x => Labels[x]));
            var r = MessageBox.Show(this,
                $"“{id.Display}” is already bound to: {names}.\n\nYes = move it to “{Labels[c]}” (remove from the others)\nNo = keep both (one input drives several actions)\nCancel = don't change anything",
                "Binding conflict", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes) foreach (var x in conflicts) _active.Unbind(x, id);
        }
        if (replace) _active.Bindings.Remove(c);
        _active.Bind(c, id);
        CommitProfile();
    }

    void ClearBinding_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedControl is not { } c) return;
        _active.Bindings.Remove(c);
        CommitProfile();
    }

    void ResetAction_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedControl is not { } c) return;
        var factory = DefaultProfiles.All().FirstOrDefault(p => p.Name == _active.Name) ?? DefaultProfiles.CreateDefault();
        if (factory.Bindings.TryGetValue(c, out var l)) _active.Bindings[c] = [.. l]; else _active.Bindings.Remove(c);
        CommitProfile();
    }

    // visual keyboard --------------------------------------------------------------------------

    static readonly (string Key, double W)[][] Rows =
    [
        [("Escape", 1.2), ("F1", 1), ("F2", 1), ("F3", 1), ("F4", 1), ("F5", 1), ("F6", 1), ("F7", 1), ("F8", 1), ("F9", 1), ("F10", 1), ("F11", 1), ("F12", 1)],
        [("Backtick", 1), ("1", 1), ("2", 1), ("3", 1), ("4", 1), ("5", 1), ("6", 1), ("7", 1), ("8", 1), ("9", 1), ("0", 1), ("Minus", 1), ("Equals", 1), ("Backspace", 1.8)],
        [("Tab", 1.4), ("Q", 1), ("W", 1), ("E", 1), ("R", 1), ("T", 1), ("Y", 1), ("U", 1), ("I", 1), ("O", 1), ("P", 1), ("LeftBracket", 1), ("RightBracket", 1), ("Backslash", 1.4)],
        [("CapsLock", 1.7), ("A", 1), ("S", 1), ("D", 1), ("F", 1), ("G", 1), ("H", 1), ("J", 1), ("K", 1), ("L", 1), ("Semicolon", 1), ("Quote", 1), ("Enter", 2.1)],
        [("Shift", 2.3), ("Z", 1), ("X", 1), ("C", 1), ("V", 1), ("B", 1), ("N", 1), ("M", 1), ("Comma", 1), ("Period", 1), ("Slash", 1)],
        [("Ctrl", 1.5), ("Alt", 1.5), ("Space", 6), ("Left", 1), ("Up", 1), ("Down", 1), ("Right", 1)],
        [("Insert", 1), ("Delete", 1), ("Home", 1), ("End", 1), ("PageUp", 1.3), ("PageDown", 1.5), ("Num7", 1), ("Num8", 1), ("Num9", 1), ("Num4", 1), ("Num5", 1), ("Num6", 1)],
    ];

    static string KeyCap(string key) => key switch
    {
        "Escape" => "Esc", "Backtick" => "`", "Minus" => "-", "Equals" => "=", "Backspace" => "Bksp", "LeftBracket" => "[", "RightBracket" => "]",
        "Backslash" => "\\", "CapsLock" => "Caps", "Semicolon" => ";", "Quote" => "'", "Comma" => ",", "Period" => ".", "Slash" => "/",
        "Left" => "←", "Up" => "↑", "Down" => "↓", "Right" => "→", "PageUp" => "PgUp", "PageDown" => "PgDn", "Insert" => "Ins", "Delete" => "Del",
        _ => key,
    };

    void BuildKeyboard()
    {
        foreach (var row in Rows)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            foreach (var (key, w) in row)
            {
                if (!InputId.TryParse("Key:" + key, out var id)) continue;
                var b = new Button { Content = KeyCap(key), Width = 38 * w + (w - 1) * 3, Height = 34, Margin = new Thickness(0, 0, 3, 0), Padding = new Thickness(0), FontSize = 12, Tag = id };
                b.Click += KeyButton_Click;
                AutomationProperties.SetName(b, "Key " + key);
                sp.Children.Add(b);
                _keyButtons[id] = b;
            }
            KeyboardHost.Children.Add(sp);
        }
    }

    void BuildMouseButtons()
    {
        foreach (var code in new[] { 1, 3, 2, 4, 5, 6, 7 })
        {
            var id = InputId.Mouse(code);
            var b = new Button { Width = 110, Height = 46, Margin = new Thickness(0, 0, 6, 6), Tag = id, FontSize = 12 };
            b.Click += KeyButton_Click;
            AutomationProperties.SetName(b, "Mouse " + id.Display);
            MouseHost.Children.Add(b);
            _mouseButtons[id] = b;
        }
    }

    void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedControl is not { } c) { MessageBox.Show(this, "Select an action in the list first."); return; }
        var id = (InputId)((Button)sender).Tag;
        if (_active.BoundInputs(c).Contains(id)) { _active.Unbind(c, id); CommitProfile(); }
        else ApplyBinding(c, id, replace: false);
    }

    void RefreshKeyColors()
    {
        var sel = SelectedControl;
        var owners = new Dictionary<InputId, List<PadControl>>();
        foreach (var (c, _) in _active.Bindings)
            foreach (var id in _active.BoundInputs(c))
            {
                if (!owners.TryGetValue(id, out var l)) owners[id] = l = [];
                l.Add(c);
            }
        void Paint(Button b, InputId id, bool mouse)
        {
            owners.TryGetValue(id, out var list);
            b.Background = _pvHeld.Contains(id) ? HeldBrush
                : sel != null && list?.Contains(sel.Value) == true ? SelectedBrush
                : list is { Count: > 0 } ? BoundBrush : null;
            b.ToolTip = list is { Count: > 0 } ? string.Join(", ", list.Select(x => Labels[x])) : "Unbound";
            if (mouse) b.Content = id.Display + (list is { Count: > 0 } ? "\n→ " + Labels[list[0]] + (list.Count > 1 ? " +" : "") : "");
        }
        foreach (var (id, b) in _keyButtons) Paint(b, id, false);
        foreach (var (id, b) in _mouseButtons) Paint(b, id, true);
    }

    void UpdatePreview()
    {
        PreviewHeld.Text = "Held: " + (_pvHeld.Count == 0 ? "—" : string.Join(" + ", _pvHeld.Select(i => i.Display)));
        var st = _compiled.Resolve(_pvHeld, 0, 0);
        var parts = new List<string>();
        foreach (var b in Enum.GetValues<PadButtons>()) if (b != PadButtons.None && st.Buttons.HasFlag(b)) parts.Add(b.ToString());
        if (st.LeftTrigger > 0) parts.Add($"LT {st.LeftTrigger}");
        if (st.RightTrigger > 0) parts.Add($"RT {st.RightTrigger}");
        if (st.LeftX != 0 || st.LeftY != 0) parts.Add($"LS ({st.LeftX},{st.LeftY})");
        if (st.RightX != 0 || st.RightY != 0) parts.Add($"RS ({st.RightX},{st.RightY})");
        PreviewPad.Text = "Pad:  " + (parts.Count == 0 ? "—" : string.Join("  ", parts));
    }

    void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        _pvHeld.Clear();
        UpdatePreview();
        RefreshKeyColors();
        if (Tabs.SelectedItem is TabItem { Header: "Settings" }) RefreshSettingsUi();
    }

    // window-level input: rebind capture + live preview ------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk != 0)
        {
            if (_captureHotkey != null)
            {
                if (vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C) { e.Handled = true; return; }
                var m = Keyboard.Modifiers;
                var hk = new Hotkey(m.HasFlag(ModifierKeys.Control), m.HasFlag(ModifierKeys.Shift), m.HasFlag(ModifierKeys.Alt), InputId.NormalizeVk(vk));
                e.Handled = true;
                _captureHotkey(hk);
                return;
            }
            if (_captureInput != null) { e.Handled = true; _captureInput(InputId.Key(vk)); return; }
            if (Tabs.SelectedItem is TabItem { Header: "Keybinds" } && !e.IsRepeat)
            {
                _pvHeld.Add(InputId.Key(vk));
                UpdatePreview(); RefreshKeyColors();
            }
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (_pvHeld.Remove(InputId.Key(vk))) { UpdatePreview(); RefreshKeyColors(); }
        base.OnPreviewKeyUp(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (_captureInput != null && _captureHotkey == null)
        {
            // clicks on the overlay's Cancel button must still work
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d) is { } p ? p : LogicalTreeHelper.GetParent(d))
                if (d is Button) { base.OnPreviewMouseDown(e); return; }
            var code = e.ChangedButton switch
            {
                MouseButton.Left => InputId.MouseLeft, MouseButton.Right => InputId.MouseRight, MouseButton.Middle => InputId.MouseMiddle,
                MouseButton.XButton1 => InputId.MouseX1, MouseButton.XButton2 => InputId.MouseX2, _ => 0,
            };
            if (code != 0) { e.Handled = true; _captureInput(InputId.Mouse(code)); return; }
        }
        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (_captureInput != null && _captureHotkey == null && e.Delta != 0)
        {
            e.Handled = true;
            _captureInput(InputId.Mouse(e.Delta > 0 ? InputId.WheelUp : InputId.WheelDown));
            return;
        }
        base.OnPreviewMouseWheel(e);
    }

    // =========================================================================================
    // Mouse tab
    // =========================================================================================

    void BuildMouseSettingsUi()
    {
        TargetCombo.ItemsSource = new[] { "Right stick", "Left stick", "Nothing" };
        PresetCombo.ItemsSource = MousePresets.All.Keys.ToList();
        PresetCombo.SelectedIndex = 0;

        void Slider(string label, string hint, double min, double max, double step, Func<MouseSettings, double> get, Action<MouseSettings, double> set)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, ToolTip = hint };
            var sl = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, ToolTip = hint };
            AutomationProperties.SetName(sl, label);
            var val = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = Avalon.Right, FontFamily = new FontFamily("Consolas") };
            Grid.SetColumn(sl, 1); Grid.SetColumn(val, 2);
            row.Children.Add(name); row.Children.Add(sl); row.Children.Add(val);
            SliderHost.Children.Add(row);
            sl.ValueChanged += (_, _) =>
            {
                val.Text = sl.Value.ToString("0.00");
                if (_loading) return;
                set(_active.Mouse, sl.Value);
                _commitDebounce.Stop(); _commitDebounce.Start();
            };
            _sliderRefresh.Add(() => { _loading = true; sl.Value = get(_active.Mouse); val.Text = sl.Value.ToString("0.00"); _loading = false; });
        }

        Slider("Horizontal sensitivity", "How far the stick moves for a given horizontal mouse speed.", 0.05, 5, 0.05, m => m.SensitivityX, (m, v) => m.SensitivityX = v);
        Slider("Vertical sensitivity", "How far the stick moves for a given vertical mouse speed.", 0.05, 5, 0.05, m => m.SensitivityY, (m, v) => m.SensitivityY = v);
        Slider("Sensitivity multiplier", "Overall multiplier applied to both axes.", 0.1, 4, 0.05, m => m.Multiplier, (m, v) => m.Multiplier = v);
        Slider("Dead zone", "Minimum stick output as soon as the mouse moves (beats the game's own dead zone).", 0, 0.5, 0.01, m => m.Deadzone, (m, v) => m.Deadzone = v);
        Slider("Maximum output", "Largest stick deflection the mouse may produce.", 0.1, 1, 0.05, m => m.MaxOutput, (m, v) => m.MaxOutput = v);
        Slider("Response curve", "1 = linear. Higher = finer control for small movements, fast turns still reach full.", 0.5, 3, 0.05, m => m.Curve, (m, v) => m.Curve = v);
        Slider("Smoothing", "Averages mouse speed over time; higher = smoother but laggier.", 0, 0.9, 0.05, m => m.Smoothing, (m, v) => m.Smoothing = v);
        Slider("Acceleration", "Extra gain for fast movement.", 0, 3, 0.05, m => m.Acceleration, (m, v) => m.Acceleration = v);

        _sliderRefresh.Add(() =>
        {
            _loading = true;
            TargetCombo.SelectedIndex = _active.Mouse.Target switch { MouseTarget.RightStick => 0, MouseTarget.LeftStick => 1, _ => 2 };
            InvertXBox.IsChecked = _active.Mouse.InvertX; InvertYBox.IsChecked = _active.Mouse.InvertY; CaptureBox.IsChecked = _active.Mouse.CaptureCursor;
            _loading = false;
        });
    }

    void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _active == null) return;
        _active.Mouse.Target = TargetCombo.SelectedIndex switch { 0 => MouseTarget.RightStick, 1 => MouseTarget.LeftStick, _ => MouseTarget.None };
        CommitProfile();
    }

    void MouseCheck_Click(object sender, RoutedEventArgs e)
    {
        _active.Mouse.InvertX = InvertXBox.IsChecked == true;
        _active.Mouse.InvertY = InvertYBox.IsChecked == true;
        _active.Mouse.CaptureCursor = CaptureBox.IsChecked == true;
        CommitProfile();
    }

    void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not string n) return;
        MousePresets.All[n](_active.Mouse);
        CommitProfile();
        foreach (var r in _sliderRefresh) r();
    }

    // =========================================================================================
    // Settings
    // =========================================================================================

    void BuildSettingsUi()
    {
        RateCombo.ItemsSource = new[] { 60, 125, 250, 500 };
        LogCombo.ItemsSource = new[] { "Debug", "Info", "Warn", "Error" };
    }

    void RefreshSettingsUi()
    {
        var s = Services.Settings;
        _loading = true;
        HotkeyBox.Text = s.ToggleHotkey;
        HotkeyNote.Text = Hotkey.TryParse(s.ToggleHotkey, out var hk) && Hotkey.IsReserved(hk)
            ? "⚠ This shortcut collides with an essential Windows/browser shortcut." : "Works from any window while XControl runs. The shortcut is consumed so the browser does not see it.";
        OnlyBrowserBox.IsChecked = s.OnlyWhenBrowserFocused;
        BrowsersBox.Text = string.Join(", ", s.BrowserProcesses);
        TrayBox.IsChecked = s.MinimizeToTray; StartMinBox.IsChecked = s.StartMinimized;
        StartupBox.IsChecked = s.RunAtStartup; EnableOnLaunchBox.IsChecked = s.EnableOnLaunch;
        RateCombo.SelectedItem = s.UpdateRateHz; LogCombo.SelectedItem = s.LogLevel;
        DriverInfo.Text = Services.IsDriverInstalled()
            ? "ViGEmBus driver: installed. To remove it, uninstall “Nefarius Virtual Gamepad Emulation Bus Driver” from Windows Settings → Apps."
            : "ViGEmBus driver: NOT installed. Run redist\\ViGEmBus_1.22.0_x64_x86_arm64.exe (requires administrator approval).";
        _loading = false;
    }

    void Setting_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var s = Services.Settings;
        s.OnlyWhenBrowserFocused = OnlyBrowserBox.IsChecked == true;
        s.BrowserProcesses = BrowsersBox.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        s.MinimizeToTray = TrayBox.IsChecked == true;
        s.StartMinimized = StartMinBox.IsChecked == true;
        s.RunAtStartup = StartupBox.IsChecked == true;
        s.EnableOnLaunch = EnableOnLaunchBox.IsChecked == true;
        if (RateCombo.SelectedItem is int hz) s.UpdateRateHz = hz;
        if (LogCombo.SelectedItem is string lv) s.LogLevel = lv;
        s.Clamp();
        Services.SaveSettings();
        Services.ApplyRuntimeSettings();
    }

    void ChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        CaptureTitle.Text = "New global shortcut";
        CaptureSub.Text = "Press the combination, e.g. Ctrl+Shift+F8 (must include Ctrl, Shift or Alt).";
        _captureInput = _ => { };
        _captureHotkey = hk =>
        {
            if (!(hk.Ctrl || hk.Shift || hk.Alt) || Hotkey.IsReserved(hk))
            {
                CaptureSub.Text = "That combination is reserved or too easy to hit by accident — try another.";
                return;
            }
            EndCapture();
            Services.Settings.ToggleHotkey = hk.ToString();
            Services.SaveSettings(); Services.ApplyRuntimeSettings();
            RefreshSettingsUi();
        };
        CaptureOverlay.Visibility = Visibility.Visible;
        CaptureOverlay.Focus();
    }

    void ResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        Services.Settings.ToggleHotkey = new AppSettings().ToggleHotkey;
        Services.SaveSettings(); Services.ApplyRuntimeSettings(); RefreshSettingsUi();
    }

    void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reset application settings (not profiles) to defaults?", "Reset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var active = Services.Settings.ActiveProfile;
        Services.Settings = new AppSettings { ActiveProfile = active };
        Services.SaveSettings(); Services.ApplyRuntimeSettings(); RefreshSettingsUi();
    }

    void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Engine.Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine($"XControl diagnostics {DateTime.Now:u}");
        sb.AppendLine($"OS: {Environment.OSVersion}  .NET: {Environment.Version}  64-bit: {Environment.Is64BitProcess}");
        sb.AppendLine($"ViGEmBus installed: {Services.IsDriverInstalled()}");
        sb.AppendLine($"Engine: enabled={s.Enabled} status={s.Status} pad={s.PadConnected} focusActive={s.FocusActive} error={s.Error}");
        sb.AppendLine($"Timing: {s.TickRateHz:F1} Hz, worst tick {s.MaxTickMs:F2} ms, late {s.LateTicks}, submits {s.Submits}, reconnects {s.Reconnects}");
        sb.AppendLine($"Key->report latency: avg {s.InputLatencyAvgMs:F2} ms, max {s.InputLatencyMaxMs:F2} ms over {s.InputLatencySamples} samples; hook callbacks avg {Services.Hook.HookAvgUs:F1} us, max {Services.Hook.HookMaxUs:F0} us over {Services.Hook.HookCalls} calls");
        sb.AppendLine($"Hooks running: {Services.Hook.IsRunning} err={Services.Hook.Error}; key events {Services.Hook.KeyEvents}, button events {Services.Hook.MouseEvents}, raw moves {Services.Hook.RawMoves}");
        sb.AppendLine($"Foreground: {Services.Monitor.ProcessName}");
        sb.AppendLine($"Profile: {_active.Name}, bindings {s.ActiveBindings}");
        sb.AppendLine($"Settings: {System.Text.Json.JsonSerializer.Serialize(Services.Settings)}");
        sb.AppendLine("--- log tail ---");
        try
        {
            var lf = Path.Combine(Log.Directory ?? "", "xcontrol.log");
            if (File.Exists(lf)) foreach (var l in File.ReadLines(lf).TakeLast(40)) sb.AppendLine(l);
        }
        catch (IOException) { }
        var path = Path.Combine(Services.Store.Root, "diagnostics.txt");
        File.WriteAllText(path, sb.ToString());
        MessageBox.Show(this, $"Saved to {path}\n\n(no account data, cookies or tokens are ever collected)\n\n" + TroubleshootText(), "Diagnostics");
        OpenFolder(Services.Store.Root);
    }

    // =========================================================================================
    // Tray / lifecycle
    // =========================================================================================

    void BuildTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true, Text = "XControl" };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show XControl", null, (_, _) => ShowFromTray());
        _trayToggle = new System.Windows.Forms.ToolStripMenuItem("Turn controller ON");
        _trayToggle.Click += (_, _) => Dispatcher.Invoke(() => SetEnabled(!Services.Engine.Enabled));
        menu.Items.Add(_trayToggle);
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    void ShowFromTray()
    {
        Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized && Services.Settings.MinimizeToTray) Hide();
        base.OnStateChanged(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && Services.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
        if (!e.Cancel) ExitApp();
    }

    void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _ui.Stop();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        ((App)Application.Current).Quit();
    }

    // =========================================================================================
    // tiny input dialog
    // =========================================================================================

    string? Prompt(string title, string message, string initial)
    {
        var win = new Window
        {
            Title = title, Owner = this, Width = 400, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        ok.Click += (_, _) => win.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box); panel.Children.Add(buttons);
        win.Content = panel;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }
}

static class Avalon
{
    public const HorizontalAlignment Right = HorizontalAlignment.Right;
}
