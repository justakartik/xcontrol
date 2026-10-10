# 🎮 XControl

**Play Xbox Cloud Gaming with your keyboard and mouse — no controller, no wallet damage.**

XControl turns your keyboard + mouse into a fake (virtual) Xbox controller on Windows. Your browser or the Xbox app thinks you plugged in a real pad, so games like Forza that want a controller just... work with WASD and a mouse.

Made by **[justakartik](https://github.com/justakartik)**.

---

## Real talk first

- 🤖 **Yes, I made this with Claude.** It's vibe-coded. If that makes you want to cry in the comments, that's okay, there's a back button. It builds, it has 77 passing tests, and I'm telling you exactly what is and isn't tested below, which is more than a lot of "hand-crafted" stuff does.
- 💸 **This is not a rich-kid product.** Free, open source, MIT. No paywall, no "Pro" tier, no account, no telemetry. You don't need a $60 controller to play on your own PC.
- ✅ **Tested for real:** I played **Forza Horizon 5** with it and it worked in **both the Xbox app and Google Chrome**. Turn it on/off with **`Ctrl+Shift+F8`**.
- 🧪 **It's still early (v0.1.0).** That was my own hands-on testing on one PC, not a lab. Found a bug or a game that misbehaves? Open an issue and tell me what happened.

---

## What you get

- ✅ Virtual **Xbox 360 (XInput)** controller — buttons, d-pad, triggers (analog), both sticks, stick clicks, Menu/View
- ✅ **Mouse → right stick** with proper tuning: sensitivity X/Y, dead zone, max output, response curve, smoothing, acceleration, invert, presets, and a live **test mode**
- ✅ **Big ON/OFF switch** that shows the real engine state, plus a **global hotkey** (default `Ctrl+Shift+F8`)
- ✅ **Click-to-rebind** keybind editor with an on-screen keyboard, duplicate warnings and conflict handling
- ✅ **Profiles** (Default + Forza Horizon 5 built in): new, duplicate, rename, delete, import/export as JSON
- ✅ **Work in any window** checkbox — or leave it off and it only grabs input while a browser / the Xbox app is in front
- ✅ System tray, optional launch-at-startup (off by default), diagnostics, logs
- ✅ **Switching OFF releases everything** — no stuck W, no runaway stick

---

## Install (the easy way)

1. Grab the latest **`XControl-x.y.z-win-x64.zip`** from the [Releases](../../releases) page and unzip it.
2. Open PowerShell in that folder and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   It copies the app, adds a Start Menu shortcut, and — if you don't have it — offers to install the **ViGEmBus** driver (needs one admin click). The script checks the driver installer's hash and signature before running it.
3. Open **XControl** from the Start Menu.

### ⚠️ One annoying Windows thing: Smart App Control
This app isn't code-signed (certificates cost money — see "not for rich kids" above). If **Smart App Control** is on, Windows will refuse to run it. Turn it off in *Windows Security → App & browser control → Smart App Control settings*. Heads up: Windows won't let you turn it back on without a reset, so only do it if you're fine with that. If you'd rather not, build it from source yourself (below) — same story though, locally built exes are unsigned too.

Needs Windows 10/11 64-bit. Nothing else — the zip includes everything (no .NET install).

**Just want the app?** Each release also has a single `XControl-x.y.z-win-x64.exe` (no install, no .NET needed) — but you must already have the ViGEmBus driver (the zip's `install.ps1` can install it for you).

**Linux / macOS?** Sorry, no — this is Windows-only by nature (it uses a Windows kernel driver and Windows input APIs). Linux has `uinput` for virtual gamepads, which would be a different project.

---

## How to use it

1. Open Xbox Cloud Gaming (Chrome/Edge) or the Xbox app and start your game.
2. Press **`Ctrl+Shift+F8`** (or hit the big button in XControl) to turn the controller **ON**. Press **`Ctrl+Shift+F8`** again to turn it **OFF** — same shortcut works in the Xbox app and in Chrome.
3. Click into the game window and press a key (browsers only show a pad after the first input).
4. Play. Default **Forza** layout: `W` gas · `S` brake/reverse · `A`/`D` steer · `Space` handbrake · `Q`/`E` shift · mouse = camera.
5. Need to click something on the page? Hit the shortcut to turn OFF, click, turn ON again. (While ON, bound mouse buttons are taken over and the cursor is frozen so it can't wander out of the game.)

**Change keys:** *Keybinds* tab → pick an action → *Rebind* → press the key or mouse button. Click keys on the on-screen keyboard too.
**Fix mouse feel:** *Mouse* tab → move the sliders while watching the test circle. Try a preset first.
**Make profiles:** top-right profile dropdown + *Profiles* tab.

Settings live in `%APPDATA%\XControl` (`settings.json`, `profiles\*.json`, `logs\xcontrol.log`).

### It's not working?
1. Dashboard should say **Driver: Installed**, and after you switch ON, **Virtual controller: Connected**.
2. Hit **Open browser gamepad test** on the Dashboard, keep that tab focused, press a bound key — it should list `Xbox 360 Controller (XInput STANDARD GAMEPAD)`.
3. Keys do nothing? Is the controller ON? Is a browser/Xbox app in front — or tick **Work in any window**? Admin (elevated) windows can't be grabbed by a normal app.
4. Driver missing? Run `redist\ViGEmBus_1.22.0_x64_x86_arm64.exe` as admin, then press *Reconnect*.
5. Close other virtual-pad tools (DS4Windows, reWASD…) while testing.
6. *Settings → Run diagnostics* writes a report (no cookies, tokens or account info — ever).

Some Xbox Cloud games now have **native mouse & keyboard** ("Play with mouse and keyboard" on xbox.com/play). Use that when a game offers it — it feels better than fake-stick mouse.

---

## Uninstall

From the unzipped folder:
```powershell
.\uninstall.ps1                  # app, shortcut, startup entry (keeps your profiles)
.\uninstall.ps1 -RemoveSettings  # also wipes %APPDATA%\XControl
.\uninstall.ps1 -RemoveDriver    # also removes the ViGEm Bus Driver (only if nothing else uses it)
```
The driver can also be removed from *Settings → Apps → ViGEm Bus Driver*.

---

## Build it yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/justakartik/xcontrol.git
cd XControl
powershell -ExecutionPolicy Bypass -File scripts\build.ps1   # build + 77 tests + publish + zip in dist\
dotnet test tests\XControl.Tests                               # just the tests
```

```
src/XControl.Core    engine, mapping, profiles, input hooks (no UI)
src/XControl.App     WPF app + tray
src/XControl.Probe   little diagnostic console (sweep / neutral / nudge / latency)
tests/               77 tests (run without the driver)
tools/               browser gamepad test page
docs/                spec + decisions, test results, manual Forza checklist
scripts/             build, install, uninstall
```

## How it works (short version)

```
keyboard/mouse ─► hooks + Raw Input ─► engine (125 Hz) ─► ViGEmBus driver ─► virtual Xbox 360 pad ─► XInput ─► browser Gamepad API ─► xCloud
```
User-mode only for capturing input (no extra kernel driver from me). The virtual pad comes from [ViGEmBus](https://github.com/nefarius/ViGEmBus) 1.22.0 — Microsoft-signed, BSD-3, **retired by its author** but still the only public driver that gives a real XInput pad. Full reasoning and rejected alternatives: [docs/SPECIFICATION.md](docs/SPECIFICATION.md).

Measured here: key press → virtual pad report ≈ 3.4 ms average (7.9 ms worst), ~0.5–1.5 % CPU. Details and what was/wasn't tested: [docs/TEST-RESULTS.md](docs/TEST-RESULTS.md).

## Honest limitations

- **Game testing so far is the author's own:** Forza Horizon 5 played and working in the Xbox app and in Chrome. Other games, other browsers (Edge, Firefox) and other PCs haven't been tried — reports welcome. Want to run the full checklist? [docs/MANUAL-FORZA-TEST.md](docs/MANUAL-FORZA-TEST.md).
- Unsigned app (Smart App Control must be off). ViGEmBus is retired upstream — a future Windows update could break it.
- Not built yet: Shift+key style combos, hold/sequence bindings, rumble, Guide button, auto game detection, auto-updates.
- UI was checked through Windows UI Automation, not by eyeballing screenshots — expect some rough edges. Tray menu, import/export dialogs and rename/delete weren't clicked through by a human.

## Contributing

PRs and issues welcome — especially **"I tried it with game X and here's what happened"** reports. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE) © justakartik. Bundled third-party stuff (ViGEmBus, ViGEm.Client) is listed in [THIRD-PARTY.md](THIRD-PARTY.md). Not affiliated with Microsoft or Xbox; "Xbox" is their trademark.
