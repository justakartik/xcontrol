using System.Runtime.InteropServices;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

// Diagnostic probe for the virtual pad.
//   probe sweep [seconds]    – sweep right stick/A/RT so a browser test page sees input
//   probe neutral            – connect, submit neutral reports, print what XInput reads (initial-state check)
//   probe nudge              – same, but submit a 1-count nudge first (wake-up experiment)
[StructLayout(LayoutKind.Sequential)] struct GP { public ushort B; public byte LT, RT; public short LX, LY, RX, RY; }
[StructLayout(LayoutKind.Sequential)] struct ST { public uint Packet; public GP Pad; }
static class XI { [DllImport("xinput1_4.dll")] public static extern uint XInputGetState(uint i, out ST s); }

static class Program
{
    static async Task Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "sweep";
        if (mode == "latency") { Latency(); return; }
        if (mode == "nudge" && args.Length > 1) NudgeAt = int.Parse(args[1]);
        using var client = new ViGEmClient();
        IXbox360Controller pad = client.CreateXbox360Controller();
        pad.AutoSubmitReport = false;
        pad.Connect();
        Console.WriteLine($"connected ({mode})");
        try
        {
            if (mode == "sweep") await Sweep(pad, args.Length > 1 ? int.Parse(args[1]) : 30);
            else await Neutral(pad, nudge: mode == "nudge");
        }
        finally { pad.Disconnect(); Console.WriteLine("disconnected"); }
    }

    // Times the real engine's ON (until Running) and OFF (until SetEnabled returns) with the real driver.
    static void Latency()
    {
        using var e = new XControl.Core.ControllerEngine(() => new XControl.Core.ViGEmPad(), 125);
        e.SetProfile(XControl.Core.DefaultProfiles.CreateForza());
        e.Start();
        for (var i = 1; i <= 5; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            e.SetEnabled(true);
            while (e.Snapshot().Status != XControl.Core.EngineStatus.Running && sw.ElapsedMilliseconds < 8000) Thread.Sleep(1);
            var on = sw.ElapsedMilliseconds;
            Thread.Sleep(300);
            sw.Restart();
            e.SetEnabled(false);
            Console.WriteLine($"cycle {i}: ON->Running {on} ms, OFF returned in {sw.ElapsedMilliseconds} ms, status={e.Snapshot().Status}");
            Thread.Sleep(300);
        }
    }

    static void Neutral0(IXbox360Controller pad)
    {
        pad.SetButtonsFull(0);
        pad.SetSliderValue(Xbox360Slider.LeftTrigger, 0); pad.SetSliderValue(Xbox360Slider.RightTrigger, 0);
        pad.SetAxisValue(Xbox360Axis.LeftThumbX, 0); pad.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
        pad.SetAxisValue(Xbox360Axis.RightThumbX, 0); pad.SetAxisValue(Xbox360Axis.RightThumbY, 0);
        pad.SubmitReport();
    }

    static int NudgeAt = 1000;

    static async Task Neutral(IXbox360Controller pad, bool nudge)
    {
        var t0 = Environment.TickCount64;
        var nudged = false;
        while (Environment.TickCount64 - t0 < 3000)
        {
            var t = Environment.TickCount64 - t0;
            if (nudge && !nudged && t > NudgeAt)
            {
                pad.SetAxisValue(Xbox360Axis.LeftThumbX, 1); pad.SubmitReport();
                await Task.Delay(30);
                nudged = true; Console.WriteLine("  (nudge sent)");
            }
            Neutral0(pad);
            for (uint i = 0; i < 4; i++)
                if (XI.XInputGetState(i, out var s) == 0 && t % 200 < 60)
                    Console.WriteLine($"{t,5} ms slot{i} pkt={s.Packet} L=({s.Pad.LX},{s.Pad.LY}) R=({s.Pad.RX},{s.Pad.RY}) LT={s.Pad.LT} RT={s.Pad.RT} B=0x{s.Pad.B:X4}");
            await Task.Delay(20);
        }
    }

    static async Task Sweep(IXbox360Controller pad, int seconds)
    {
        var t0 = Environment.TickCount64;
        while (Environment.TickCount64 - t0 < seconds * 1000L)
        {
            var t = (Environment.TickCount64 - t0) / 1000.0;
            pad.SetAxisValue(Xbox360Axis.RightThumbX, (short)(Math.Sin(t * 2) * 30000));
            pad.SetButtonState(Xbox360Button.A, ((int)t % 2) == 0);
            pad.SetSliderValue(Xbox360Slider.RightTrigger, (byte)((t % 2) / 2 * 255));
            pad.SubmitReport();
            await Task.Delay(8);
        }
        Neutral0(pad);
    }
}
