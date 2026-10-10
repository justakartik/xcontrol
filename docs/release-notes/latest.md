## XControl v0.1.1 — installer fix 🛠️

Patch release. **No change to the app itself** (same controller engine as v0.1.0) — this fixes a confusing install failure.

### Fixed
- **`install.ps1` failed with `app\XControl.exe not found next to install.ps1`** when people ran it from GitHub's *Code → Download ZIP* (the source code, which contains no built app).
  The script now explains what happened, tells you exactly which file to grab, and **opens the Releases page**. Pass `-Download` and it fetches the latest release itself and checks its SHA-256 before installing.
- Docs: README now warns about *Download ZIP* vs the release zip and adds a troubleshooting entry.

### Which file do I download?
| File | Use it when |
|---|---|
| **`XControl-0.1.1-win-x64.zip`** | **Most people.** App + `install.ps1` / `uninstall.ps1` + the verified ViGEmBus driver installer + docs. Extract it, then run `powershell -ExecutionPolicy Bypass -File .\install.ps1`. |
| `XControl-0.1.1-win-x64.exe` | You already have the ViGEmBus driver and just want the app (one file, no .NET needed). |
| `SHA256SUMS.txt` | Verify your download. |

Windows only (10/11 x64). Unsigned: turn off **Smart App Control** if Windows blocks it. Played and working with Forza Horizon 5 in the Xbox app and Chrome (author-tested); toggle with **`Ctrl+Shift+F8`**.

**Upgrading from 0.1.0:** just run the new `install.ps1`; your profiles and settings in `%APPDATA%\XControl` are kept.

Full details: [README](https://github.com/justakartik/xcontrol#readme) · [test results](https://github.com/justakartik/xcontrol/blob/HEAD/docs/TEST-RESULTS.md)
