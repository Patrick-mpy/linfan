# LinFan — Windows packaging

Counterpart to the Linux side (`packaging/install.sh` & co.). Installs the daemon as a
**Windows service (LocalSystem)** and the GUI as a normal Start-menu entry. **Nothing is built** on the
target PC — the build is self-contained (the .NET runtime is included), **no .NET SDK** is required.

## 1. Produce the build (on the Linux/dev machine, cross-publish)

```bash
dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.Daemon -o artifacts/LinFan-win-x64/Daemon
dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.App    -o artifacts/LinFan-win-x64/App
```

Result: `artifacts/LinFan-win-x64/{Daemon,App}` (gitignored). Copy this folder to the Windows PC.

## 2a. Install — via script (no extra tooling)

In a PowerShell started **as administrator**:

```powershell
cd <path>\LinFan-win-x64        # or into the repo: packaging\windows
.\Install-LinFan.ps1            # default source: ..\..\artifacts\LinFan-win-x64
.\Install-LinFan.ps1 -Source C:\path\to\LinFan-win-x64   # explicit source
```

The script stops any running service (upgrade-safe, ramping the fans to hardware auto in the process),
copies to `%ProgramFiles%\LinFan`, registers the service (autostart + failure restart), creates a
Start-menu shortcut, and starts the service.

Remove: `.\Uninstall-LinFan.ps1` (the config under `%ProgramData%\linfan` is kept).

## 2b. Install — via one-click installer (Inno Setup)

Build from `linfan.iss` on a Windows PC with [Inno Setup 6](https://jrsoftware.org/isdl.php):

```powershell
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" packaging\windows\linfan.iss
```

Result: `artifacts\LinFan-Setup-0.1.0-win-x64.exe`. The installer bundles the build, calls the same
PowerShell scripts for the service logic, and ships an uninstaller.

## Architecture notes

- **Service = LocalSystem.** Writing PWM via LibreHardwareMonitor/WinRing0 needs admin; the slim daemon
  encapsulates the privileged part, the GUI runs unprivileged.
- **IPC = named pipe `\\.\pipe\linfan`.** The DACL lets the daemon (SYSTEM) create it and grants the
  unprivileged GUI read/write access — no env/setup needed, default name on both sides.
- **Machine-wide config** under `%ProgramData%\linfan\config.json`, so the SYSTEM service and the user
  GUI see the same file (the daemon is the sole writer). No `%AppData%` — that would be per-user and
  invisible to the SYSTEM service.
- **Fail-safe:** on stop (install/upgrade/uninstall, `services.msc`, `net stop`) the daemon ramps the
  fans back to hardware auto (`RestoreDefaults`) via clean service shutdown. The registered failure
  restart (failure-counter reset after 60 s) brings it back after hiccups. But: Windows has **no**
  firmware auto-reversion like `thinkpad_acpi`. If the service gives up permanently after a tight crash
  loop **or** is killed hard (no clean shutdown), the fans may stay at the last PWM set — then start the
  service again (`Start-Service LinFan`).

## Diagnostics / logs

- **File log** at `%ProgramData%\linfan\logs\linfan.log` — on by default, size-capped (~1 MB, best-effort),
  no config needed. Set the env var `LINFAN_LOG=off` (also `0`/`none`) to disable it, or to an absolute
  path to relocate it. As a LocalSystem service, that env var must be set machine-wide to take effect.
- **Startup discovery dump.** On every service start the daemon logs each detected sensor and, per fan, the
  **effective tachometer** (`assigned` / `heuristic` / `— none`). This is the first thing to check for a
  "no tacho signal" report: a fan showing `— none` has no RPM source paired — run the tacho coupling
  (Settings, or per fan in onboarding) or assign one manually.
