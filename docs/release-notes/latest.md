## XControl v0.1.0 — first public release 🎮

Play Xbox Cloud Gaming with your **keyboard and mouse**. XControl creates a virtual Xbox 360 (XInput) controller on Windows, so your browser or the Xbox app sees a normal pad. Free, open source (MIT), no accounts, no telemetry. Made by **justakartik**, built with Claude (yes, vibe-coded — see the README).

### Which file do I download?
| File | Use it when |
|---|---|
| **`XControl-0.1.0-win-x64.zip`** | **Most people.** Contains the app, `install.ps1` / `uninstall.ps1`, the verified ViGEmBus driver installer and docs. Unzip, run `powershell -ExecutionPolicy Bypass -File .\install.ps1`. |
| `XControl-0.1.0-win-x64.exe` | You already have the ViGEmBus driver and just want the app. One file, nothing to install, no .NET needed. |
| `SHA256SUMS.txt` | Verify your download. |

**Windows only** (10/11, 64-bit). It relies on a Windows kernel driver and Windows input APIs, so there is no Linux or macOS build.

### Highlights
- Virtual Xbox 360 controller: buttons, d-pad, analog triggers, both sticks, stick clicks, Menu/View
- Mouse → right stick with sensitivity, dead zone, curve, smoothing, acceleration, presets and a live test mode
- Big ON/OFF switch showing the real engine state + global hotkey (`Ctrl+Shift+F8`)
- Click-to-rebind keybind editor with on-screen keyboard and conflict warnings
- Profiles (Default + Forza Horizon 5), import/export, tray icon, optional start-with-Windows
- **Work in any window** checkbox, or restrict to browsers / the Xbox app
- Turning OFF (or losing focus, or any device error) releases every button and stick

### Read before you install
- ⚠️ **Not code-signed.** If Windows **Smart App Control** is on it will block the app. Turn it off in Windows Security → App & browser control (Windows can't turn it back on without a reset).
- ⚠️ **Not yet verified in a real Forza Horizon 5 / Xbox Cloud Gaming session.** Verified: driver install, Windows sees the pad, a Chromium browser's Gamepad API sees it and receives keys, simultaneous inputs and mouse movement, OFF-while-held, global shortcut, keybind persistence, 77 automated tests. Not verified: Google Chrome + the xCloud page, Forza's in-game bindings (my defaults are a best guess). Reports welcome!
- The virtual pad comes from **ViGEmBus 1.22.0**, which its author has retired (it still works; it's Microsoft-signed).
- Not implemented yet: Shift+key chords, hold/sequence bindings, rumble, Guide button, auto game detection, auto-update.

### Performance (measured on the dev PC)
Key press → virtual pad report ≈ 3.4 ms average, 7.9 ms worst; ~0.5–1.5 % CPU; ~90 MB hidden in the tray.

Full details: [README](https://github.com/justakartik/xcontrol#readme) · [test results](https://github.com/justakartik/xcontrol/blob/HEAD/docs/TEST-RESULTS.md) · [manual Forza checklist](https://github.com/justakartik/xcontrol/blob/HEAD/docs/MANUAL-FORZA-TEST.md)
