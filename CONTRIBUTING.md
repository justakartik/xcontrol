# Contributing

Thanks for poking at this. It's a small, vibe-coded (with Claude) project, so keep it friendly and practical.

## Most useful thing you can do
Run it with a real game and open an issue: Windows version, browser/Xbox app, game, what worked, what didn't. Attach `%APPDATA%\XControl\diagnostics.txt`
(Settings → Run diagnostics; it contains no account data). Say whether you used *Work in any window*.

## Dev setup
* .NET 10 SDK, Windows 10/11 x64.
* `dotnet test tests\XControl.Tests` — must stay green (77 tests, no driver needed).
* `scripts\build.ps1` — build, test, publish, zip.
* The ViGEmBus driver is only needed for the real-device checks (`src/XControl.Probe`, the app itself).

## Ground rules
* Keep `XControl.Core` UI-free and testable; put device access behind `IVirtualPad`.
* Anything that can leave a button pressed needs a test (OFF / focus loss / device error must release everything).
* Don't claim a feature works unless you ran it; say what you tested in the PR.
* Never read or log cookies, tokens or account data.

## Releasing
`scripts\build.ps1` produces `dist\XControl-<version>-win-x64.zip` (+ `.sha256`). Attach both to a GitHub Release.
