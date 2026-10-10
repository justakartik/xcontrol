# Changelog

## 0.1.1 — installer fix
- `install.ps1`: running it from GitHub's "Download ZIP" (source code, no `app\`) no longer ends in a bare error. It explains the mistake, opens the Releases page, and supports `-Download` to fetch and SHA-256-verify the latest release. `-NoOpen` suppresses opening the browser.
- README: warning about Download ZIP vs the release zip; troubleshooting entry.
- No application code changes.

## 0.1.0 — first public release
- Virtual Xbox 360 (XInput) controller via ViGEmBus; keyboard/mouse → pad; mouse → stick with tuning and test mode.
- ON/OFF switch, global shortcut (Ctrl+Shift+F8), tray, "Work in any window" option.
- Keybind editor with on-screen keyboard and conflict handling; profiles (Default, Forza Horizon 5), import/export.
- Release workflow: zip + single-file exe + checksums.
