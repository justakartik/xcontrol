# Test results (0.1.0, 2026-10-10, Windows 11 26200, AMD Ryzen 7, 16 logical cores)

Legend: **Verified** = observed on this machine · **Partial** · **Not tested** · **Blocked** = needs something only the user can provide.

## Automated (77 tests, `dotnet test tests\XControl.Tests`) — Verified, all pass
Input-name parsing, key/mouse mapping, simultaneous inputs, analog trigger scale, opposite-key cancel, diagonal clamp, mouse→stick (sensitivity, invert,
curve, dead zone, max, smoothing, acceleration, tick-length independence, sparse-report bridging), profile JSON round-trip / validation / conflicts / clone,
hotkey parsing, settings + profile persistence, corrupt-file quarantine, import/export, engine against a fake pad (disabled sends nothing, ON connects,
OFF releases and disconnects, re-enable starts neutral, focus loss releases, key-up releases, mouse recentres, submit failure → reconnect and re-apply,
missing driver reported, dispose neutralises, profile switch, wake-nudge, tick rate).

## Real-device checks

| Check | Status | Evidence |
|---|---|---|
| Driver install (signature + hash verified first) | Verified | ViGEmBus 1.22.0 running; loads with memory integrity (HVCI) enabled |
| Windows sees the pad | Verified | PnP: *Xbox 360 Controller for Windows* appears on ON, disappears on OFF |
| Browser Gamepad API sees it | Partial | Chromium pane of the Claude desktop app: `Xbox 360 Controller (XInput STANDARD GAMEPAD)`, standard mapping, 4 axes / 17 buttons, seen ≈0.56 s after creation. **Google Chrome itself and the xCloud page not checked.** |
| Keys → buttons/triggers/sticks through the real app | Verified | XInput readings + browser log: W→RT 255; W+A+Space→A + LX −32767 + RT together; D→LX +32767 |
| Mouse → right stick, recentres on stop | Verified | XInput: ±32767 pulses with injected 40-count steps; returns to 0; browser saw axis 2 = ±1, axis 3 negative for mouse-up |
| Unbound key (Shift) does nothing | Verified | XInput unchanged |
| OFF during an active W+D+mouse action | Verified | Neutral report then pad removed (≈80 ms); browser `gamepaddisconnected` fired |
| ON again resumes | Verified | RT=1 again for the 700 ms W press |
| Global shortcut Ctrl+Shift+F8 | Verified | 4/4 toggles in order after fixing modifier tracking (earlier 1 of 4 was missed) |
| Stale initial XInput state on a new pad | Found & fixed | Before: L(−3356,−1869) R(−3255,−848) until first change; after nudge: all zero |
| Keybind editor: rebind, persist across restart, Reset action | Verified (UI Automation) | A: Space → Z; after restart still Z; reset restored Space and saved |
| Conflict dialog | Partial | Appears with correct text; Move/Keep-both outcomes not exercised in UI (model logic unit-tested) |
| Tray menu, import/export dialogs, rename/delete, hotkey-change dialog, run-at-startup | Not tested | |
| Visual review of the UI | Not tested | Only inspected via UI Automation text; no screenshots possible |
| Cursor actually frozen while active | Not tested | |
| Install via `install.ps1` from the extracted zip, launch installed app | Verified | |
| `uninstall.ps1` | Not tested | |
| Xbox Cloud Gaming page recognises the pad | **Blocked** | Needs your signed-in Chrome session |
| Forza Horizon 5 launch, gameplay input, in-game default bindings | **Blocked** | Same; checklist in MANUAL-FORZA-TEST.md |
| Recovery after game/session end, browser restart | Not tested | Engine logic unit-tested; no real session |

## Performance (published ReadyToRun build, 125 Hz engine)
* Key press → virtual-pad report: avg 3.4 ms, max 7.9 ms (110 samples) — bounded by the 8 ms tick.
* Hook callbacks: avg 8.4 µs, max 760 µs. (Unoptimised build had a 26 ms first-call outlier.)
* Engine: 125 Hz sustained; 2 late ticks in ~30 s; one 47 ms worst-tick spike observed (cause not isolated).
* CPU (of total, 16 cores): hidden in tray, OFF 0.48 % (before the OFF-idle change); window open OFF ≈1.0 %; ON idle 0.72 %; ON with ~50 mouse events/s + key taps 1.5 %.
* Memory: ~90 MB hidden, 167–200 MB window open.
* "Input release reliability": every OFF/focus-loss/disconnect case above ended with a neutral report; no stuck input observed in XInput traces. Not stress-tested over long runs.
* Mouse injection in tests was ~50–250 events/s (PowerShell limit), not a 1000 Hz mouse.

## Test-environment notes
Synthetic input needs `XCONTROL_ACCEPT_INJECTED=1`. Some runs temporarily disabled the browser gate (briefly swallowing the user's own clicks); settings were restored afterwards.
