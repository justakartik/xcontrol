# Manual end-to-end test: Xbox Cloud Gaming + Forza Horizon 5

Not automated because it needs a signed-in Xbox session (no credentials or cookies are ever read by XControl or by the test tooling).
The author has played Forza Horizon 5 with XControl in both the Xbox app and Chrome (overall result: working); this checklist is for itemised results.
Record results in the table at the bottom.

**Before you start:** driver installed; XControl running; profile *Forza Horizon 5*; Chrome/Edge open on xbox.com/play, signed in.

1. **Recognition** – ON via Ctrl+Shift+F8. Dashboard: *Virtual controller: Connected*. Windows → Settings → Bluetooth & devices → Devices (or `joy.cpl`) lists *Xbox 360 Controller for Windows*.
2. **Browser** – browsers only give a pad to the *focused* tab. Click the xCloud tab, press **Space**, then press F12 → Console and run
   `navigator.getGamepads()`; expect an entry `Xbox 360 Controller (XInput STANDARD GAMEPAD)`. (Or use the Dashboard's *Open browser gamepad test* page in its own tab and keep that tab focused.)
3. **Launch** – choose Forza Horizon 5, *Play with Cloud Gaming*. Wait for the game to load (requires your subscription / queue).
4. **Gameplay checks** (switch the controller ON first; if the page needs a click, switch OFF, click, switch ON):

   | # | Action | Expected | Result |
   |---|---|---|---|
   | a | Hold **W** | accelerate (RT) | |
   | b | Hold **S** | brake / reverse (LT) | |
   | c | Hold **A** / **D** | steer left / right (left stick, analog 100 %) | |
   | d | **Space** | handbrake (A) — verify; change the profile if Forza maps it elsewhere | |
   | e | **R** / mouse side button | rewind (X) — verify | |
   | f | **Q / E** | shift down / up (LB / RB) | |
   | g | Move mouse | camera (right stick); tune on the Mouse tab | |
   | h | **W + D + mouse** together | all three at once | |
   | i | Ctrl+Shift+F8 while holding W | car stops accelerating, no stuck input, mouse cursor free | |
   | j | ON again, press W | accelerates again | |
   | k | Switch tabs / minimise browser while holding W | input stops (focus gate) | |
   | l | End the session / exit to menu | XControl stays ON/Running, no error | |
   | m | Quit and restart XControl | profile and mouse settings preserved | |

5. Attach `%APPDATA%\XControl\diagnostics.txt` (Settings → Run diagnostics) when reporting problems.

| Date | Tester | Browser / version | a–m pass? | Notes |
|---|---|---|---|---|
| | | | | |
