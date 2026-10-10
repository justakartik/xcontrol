using System.IO;
using System.Windows;
using Microsoft.Win32;
using XControl.Core;

namespace XControl.App;

/// <summary>Process-wide services. The engine lives here, not in the window, so it keeps running while the UI is hidden.</summary>
public static class Services
{
    public static ProfileStore Store = null!;
    public static AppSettings Settings = null!;
    public static ControllerEngine Engine = null!;
    public static InputHook Hook = null!;
    public static ForegroundMonitor Monitor = null!;

    public static void SaveSettings() => Store.SaveSettings(Settings);

    public static void ApplyRuntimeSettings()
    {
        Log.SetLevel(Settings.LogLevel);
        Engine.SetUpdateRate(Settings.UpdateRateHz);
        Monitor.Configure(Settings.BrowserProcesses, Settings.OnlyWhenBrowserFocused);
        Hook.SetHotkey(Settings.ToggleHotkey);
        SetRunAtStartup(Settings.RunAtStartup);
    }

    public static void SetRunAtStartup(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (on) key.SetValue("XControl", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("XControl", false);
        }
        catch (Exception ex) { Log.Warn("Could not update startup entry: " + ex.Message); }
    }

    public static bool IsDriverInstalled() =>
        Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus") != null;
}

public partial class App : Application
{
    Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, "XControl.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("XControl is already running (check the system tray).", "XControl");
            Shutdown();
            return;
        }

        var store = new ProfileStore();
        Services.Store = store;
        Log.Init(Path.Combine(store.Root, "logs"));
        Services.Settings = store.LoadSettings();
        Log.SetLevel(Services.Settings.LogLevel);
        Log.Info($"XControl starting. Windows {Environment.OSVersion.Version}, .NET {Environment.Version}");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception: " + args.Exception);
            Services.Engine?.ReleaseAll();
            MessageBox.Show("An unexpected error occurred and was logged:\n" + args.Exception.Message, "XControl");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Services.Engine?.Dispose();

        Services.Engine = new ControllerEngine(() => new ViGEmPad(), Services.Settings.UpdateRateHz);
        Services.Engine.Changed += () => { };
        Services.Engine.Start();
        Services.Monitor = new ForegroundMonitor(Services.Engine);
        Services.Hook = new InputHook(Services.Engine);
        Services.Hook.Start();
        if (!Services.Hook.IsRunning) Log.Error(Services.Hook.Error ?? "Input hook failed.");
        Services.ApplyRuntimeSettings();

        var win = new MainWindow();
        MainWindow = win;
        var minimized = e.Args.Contains("--minimized") || Services.Settings.StartMinimized;
        if (minimized && Services.Settings.MinimizeToTray) win.Hide(); else win.Show();
    }

    public void Quit()
    {
        Log.Info("Shutting down.");
        Services.Hook?.Dispose();
        Services.Monitor?.Dispose();
        Services.Engine?.Dispose();   // releases every input and removes the virtual controller
        _single?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.Engine?.Dispose();
        base.OnExit(e);
    }
}
