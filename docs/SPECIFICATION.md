# Engineering specification and decision record

## 1. Goal
Let a user play Xbox Cloud Gaming in an already-open, signed-in browser with a physical keyboard and mouse, by exposing a
**virtual Xbox-compatible gamepad** that the browser's Gamepad API reports as a standard controller. Native Windows app, global input,
editable bindings, profiles, ON/OFF switch, tray, hotkey. No cookie/token access, no authentication bypass.

## 2. Research findings (what the decisions rest on)

| Question | Finding | Source / evidence |
|---|---|---|
| How does Better xCloud do it? | It does **not** create a device. `mkb-handler.ts` replaces `navigator.getGamepads` in the page with one that appends a synthetic gamepad object, dispatches `gamepadconnected`, locks the pointer, and converts `movementX/Y` to stick values: scale by per-axis sensitivity, apply a "dead-zone counter-weight" (boost small vectors up to a minimum length), clamp to a maximum stick range, and zero the stick 50 ms after the mouse stops. MIT licensed. | Read `src/modules/mkb/mkb-handler.ts` in redphx/better-xcloud (MIT). Ideas reused, code not copied. |
| How does the browser receive controllers on Windows? | Chromium reads XInput / Windows.Gaming.Input; Xbox 360-class XInput devices get the *standard* mapping. | Chromium `device/gamepad` sources; confirmed empirically: `Xbox 360 Controller (XInput STANDARD GAMEPAD)`, mapping `standard`, 4 axes, 17 buttons. |
| ViGEmBus status | Final release v1.22.0 (2023-11-02), repository archived, BSD-3-Clause, WHQL-signed; installer signed by *Nefarius Software Solutions e.U.* (DigiCert), SHA-256 `89220A78…014C6A`. Existing installs keep working. | GitHub API, author's end-of-life notice (docs.nefarius.at), `Get-AuthenticodeSignature`. |
| Successor | *Nefarius VirtualPad* is a commercial framework offered to business partners only; no public download or SDK. | docs.nefarius.at/projects/VirtualPad. **Unusable.** |
| Windows `InputInjector` gamepad injection | Needs the restricted `inputInjectionBrokered` capability in a packaged app; whether the injected pad reaches XInput on desktop is undocumented. | Microsoft Learn (Windows.UI.Input.Preview.Injection). **Rejected: not shippable / unproven.** |
| vJoy / custom HID | Generic DirectInput joystick: no XInput, no standard mapping, drivers poorly maintained. | General knowledge, not tested. **Rejected.** |
| Memory Integrity (HVCI) | On the dev machine memory integrity is enabled (`HypervisorEnforcedCodeIntegrity\Enabled = 1`, `SecurityServicesRunning = {2}`) and ViGEmBus 1.22.0 is `Running` — i.e. it loads under HVCI here. | `Win32_DeviceGuard`, registry, `Win32_SystemDriver`. |

## 3. Architecture decision

**ViGEmBus + `Nefarius.ViGEm.Client` (1.21.256) from a native WPF/.NET 10 app**, user-mode hooks for input.

* *Why:* the only publicly available option that produces a genuine XInput device; verified in the real browser. Limitation: retired upstream.
* *Why .NET/WPF:* the client library is .NET; WPF gives native Windows integration (tray via WinForms `NotifyIcon`), UI Automation accessibility, and
  self-contained publishing. A C++ app would give lower memory but no benefit to latency (measured key→report is bounded by the 8 ms tick, not the runtime).
* *Why no kernel driver of our own:* keyboard/mouse capture is done in user mode (`SetWindowsHookEx` low-level hooks + Raw Input), which is sufficient and needs no extra privileges.
* *Browser companion extension:* **not needed** — the virtual pad is visible to the browser through Windows. No extension was built.
* *Dependency choices were not assumed from old tutorials:* ViGEmBus's archived status was checked and is documented as a risk, not hidden.

### Components
* `XControl.Core.ControllerEngine` — owns the pad, 125 Hz loop (20 Hz when OFF), thread-safe input state, releases everything on disable/focus-loss/error/dispose, reconnects after device failure, reports driver-missing separately. UI-independent.
* `MouseStickTranslator` — pure function: counts/s → velocity → EMA smoothing (+ fixed 3-tick baseline) → response curve → acceleration → dead-zone offset → clamp. 1.0 sensitivity = full deflection at 3000 counts/s.
* `Profile` / `CompiledProfile` — control → inputs, per-input analog scale, mouse settings; JSON; validation drops bad tokens and clamps numbers.
* `ProfileStore` — `%APPDATA%\XControl`, atomic writes, corrupt files quarantined, built-ins recreated.
* `InputHook` — hooks + Raw Input on one message-loop thread; consumes only bound inputs while engine Running and focus gate open; key-ups are consumed only if the key-down was; modifier state tracked from the hook's own stream.
* `ForegroundMonitor` — 150 ms poll of the foreground process name/title; gates translation to browser processes.
* WPF app: dashboard, keybind editor (list + on-screen keyboard + mouse buttons + live preview), mouse tuning + test mode, profiles, settings, tray.

## 4. Risks and how they were handled

| Risk | Handling / outcome |
|---|---|
| Stuck inputs | Neutral report on OFF / focus loss / error / exit; swallowed key-ups tracked; unit-tested and observed through XInput. |
| Windows shows stale non-zero stick values on a freshly created pad | **Found by measurement** (`pkt=1`, L=(−3356,−1869)). Fixed with a 1-count wake nudge + neutral on connect; regression test added. |
| Hook callbacks blocking system input | Cost measured: avg 8 µs, max 760 µs in the published ReadyToRun build (a 26 ms first-call JIT outlier in the unoptimised build led to enabling ReadyToRun). |
| Hotkey missed intermittently | Polling `GetAsyncKeyState` disagreed with the hook's event stream; replaced with hook-tracked modifier state. 4/4 toggles then registered. |
| Browser ignores unfocused pages | Chromium only delivers gamepad data to the focused window; documented in troubleshooting. |
| Unsigned app vs Smart App Control | Blocker on the dev machine; user disabled SAC. Documented; needs a trusted signing certificate for distribution. |
| ViGEmBus retired | Documented; driver hash/signature pinned in `install.ps1`. |
