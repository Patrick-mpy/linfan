# LinFan - Windows packaging

Counterpart to the Linux side (`packaging/install.sh` & co.). Installs the daemon as a
**Windows service (LocalSystem)** and the GUI as a normal Start-menu entry. **Nothing is built** on the
target PC - the build is self-contained (the .NET runtime is included), **no .NET SDK** is required.

## 1. Produce the build (on the Linux/dev machine, cross-publish)

```bash
dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.Daemon -o artifacts/LinFan-win-x64/Daemon
dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.App    -o artifacts/LinFan-win-x64/App
```

Result: `artifacts/LinFan-win-x64/{Daemon,App}` (gitignored). Copy this folder to the Windows PC.

## 2a. Install - via script (no extra tooling)

In a PowerShell started **as administrator**:

```powershell
cd <path>\LinFan-win-x64        # or into the repo: packaging\windows
.\Install-LinFan.ps1            # default source: ..\..\artifacts\LinFan-win-x64
.\Install-LinFan.ps1 -Source C:\path\to\LinFan-win-x64   # explicit source
```

The script stops any running service (upgrade-safe, ramping the fans to hardware auto in the process),
copies to `%ProgramFiles%\LinFan`, registers the service (autostart + failure restart), creates a
Start-menu shortcut, sets up the `LinFan Users` IPC access group, and starts the service.
On a first install, **log out and back in once** afterwards - otherwise the GUI cannot connect.

On an upgrade a **running GUI is closed** first (it holds `App\*.dll`, which Windows would not let the
copy overwrite); unsaved editor changes are lost. Only instances started from the install directory are
touched - a GUI running from a source tree keeps going. It is not restarted afterwards, since it would
inherit the elevated rights of the installing shell; start it again from the Start menu.

Remove: `.\Uninstall-LinFan.ps1` (closes the GUI as well; the config under `%ProgramData%\linfan` is
kept, the group is removed).

## 2b. Install - via one-click installer (Inno Setup)

Build from `linfan.iss` on a Windows PC with [Inno Setup 6](https://jrsoftware.org/isdl.php):

```powershell
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" packaging\windows\linfan.iss
```

Result: `artifacts\LinFan-Setup-0.1.0-win-x64.exe`. The installer bundles the build, calls the same
PowerShell scripts for the service logic, and ships an uninstaller.

Extras over the script path (both opt-out in the wizard):

- **GUI autostart at login** - a machine-wide `HKLM\...\CurrentVersion\Run` entry starting
  `LinFan.App.exe --minimized` (hidden in the tray; the uninstaller removes the entry). HKLM on
  purpose: the elevated installer writing HKCU would land in the *admin's* hive. The manual script
  install deliberately has no autostart - Inno owns shortcuts/autostart, the scripts own the service.
- **"Launch LinFan" on the finish page** - started `runasoriginaluser`, so the GUI runs unprivileged
  despite the elevated installer. Shown on **upgrades only**: on a first install the GUI user was just
  added to `LinFan Users`, whose membership needs a re-login - the finish page then offers a
  **restart** instead (a GUI launched with the pre-add token could not reach the pipe; the install
  script signals this via a marker file, Inno's `NeedRestart()` picks it up).

Upgrading **while LinFan runs** needs no manual step: `PrepareToInstall()` stops the service, and the
Restart Manager closes the GUI, which holds `App\*`. The GUI treats that request as a real quit instead
of hiding into the tray - `CloseApplications=force` closes an older GUI that still hides, so the upgrade
never stops at "unable to automatically close all applications". `RestartApplications=no` keeps the
Restart Manager from relaunching the GUI afterwards: it would inherit Setup's elevated token, and the GUI
must stay unprivileged (that is what the finish-page `runasoriginaluser` launch is for).

## Architecture notes

- **Code signing:** `sign-windows-binaries.sh` signs the executables and the installer through SignPath
  from CI. It stays **inert until the signing credentials are configured**, so builds are currently
  unsigned - SmartScreen warns on first run, and a download is verified against the `SHA256SUMS.txt`
  published with each release (see `docs/INSTALL.md`).
- **Service = LocalSystem.** Writing PWM via LibreHardwareMonitor/WinRing0 needs admin; the slim daemon
  encapsulates the privileged part, the GUI runs unprivileged.
- **IPC = named pipe `\\.\pipe\linfan`.** The DACL grants SYSTEM/administrators full control and the
  local group **`LinFan Users`** read/write - so only intended GUI users can talk to the privileged
  daemon, not every local account. The installer creates the group and adds the GUI user; **group
  membership only takes effect after a re-login** (counterpart to the `linfan` socket group on Linux).
  If the group is missing, the daemon logs a warning and falls back to "Authenticated Users" so the GUI
  keeps working. The group is resolved **once at service start** - create it before starting the
  service, or restart the service after creating it.
- **Machine-wide config** under `%ProgramData%\linfan\config.json`, so the SYSTEM service and the user
  GUI see the same file (the daemon is the sole writer). No `%AppData%` - that would be per-user and
  invisible to the SYSTEM service.
- **Fail-safe:** on stop (install/upgrade/uninstall, `services.msc`, `net stop`) the daemon ramps the
  fans back to hardware auto (`RestoreDefaults`) via clean service shutdown. The registered failure
  restart (failure-counter reset after 60 s) brings it back after hiccups. But: Windows has **no**
  firmware auto-reversion like `thinkpad_acpi`. If the service gives up permanently after a tight crash
  loop **or** is killed hard (no clean shutdown), the fans may stay at the last PWM set - then start the
  service again (`Start-Service LinFan`).

## Diagnostics / logs

- **File log** at `%ProgramData%\linfan\logs\linfan.log` - on by default, size-capped (~1 MB, best-effort),
  no config needed. Set the env var `LINFAN_LOG=off` (also `0`/`none`) to disable it, or to an absolute
  path to relocate it. As a LocalSystem service, that env var must be set machine-wide to take effect.
- **Startup discovery dump.** On every service start the daemon logs each detected sensor and, per fan, the
  **effective tachometer** (`assigned` / `heuristic` / `- none`). This is the first thing to check for a
  "no tacho signal" report: a fan showing `- none` has no RPM source paired - run the tacho coupling
  (Settings, or per fan in onboarding) or assign one manually.
