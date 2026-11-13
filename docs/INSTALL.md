# LinFan - Installation & Operation

Build, run locally, and install as a service - Linux, Windows, and macOS. Overview and architecture:
[README.md](../README.md) · [docs/ARCHITECTURE.md](ARCHITECTURE.md).

## Prerequisites

- **.NET 8 SDK** to build (the target framework is `net8.0`, LTS). Thanks to `RollForward=Major`
  (`Directory.Build.props`), the daemon **and** GUI also run on a newer major runtime - a **.NET 10 SDK
  from the Microsoft apt feed** builds and starts everything; a dedicated net8 runtime is not required.
- Do **not** use the **snap** SDK for the GUI: it bakes the old `core20` glibc loader into every apphost,
  which makes SkiaSharp (Avalonia) fail against the system `libfontconfig` (`GLIBC … not found`). The
  daemon (no Skia) would run under snap.
- Linux hardware access (hwmon modules, driver flags) is covered in [docs/HARDWARE.md](HARDWARE.md).

## Build & run locally

```bash
dotnet build                                        # build everything
dotnet test                                         # test suite

# Daemon CLI (Linux):
dotnet run --project src/LinFan.Daemon -- list      # list sensors & fans
dotnet run --project src/LinFan.Daemon -- monitor   # live RPM/temperatures (Ctrl+C)
dotnet run --project src/LinFan.Daemon -- init      # generate a starter config from the hardware (not while a daemon runs)
dotnet run --project src/LinFan.Daemon -- run       # control loop + IPC server (dry run without root)
# As root: build first (dotnet build), then run the DLL against the service's configuration (see below).
cli="dotnet src/LinFan.Daemon/bin/Debug/net8.0/LinFan.Daemon.dll"
sudo LINFAN_CONFIG=/etc/linfan/config.json $cli calibrate <fanId>  # measure start-up point (root)
sudo $cli set <fanId> 128                                          # set PWM (with watchdog)
sudo $cli auto <fanId>                                             # back to hardware auto mode
sudo $cli restore                                                  # every fan back to hardware auto mode
# 'calibrate' and 'set' refuse (exit 3) while a daemon runs - stop the service first. 'auto'/'restore' always run,
# but next to a daemon they only last until its next write.

# IPC client (talks to a running 'run'):
dotnet run --project src/LinFan.Daemon -- monitor-ipc  # live snapshots from the daemon (Ctrl+C)
dotnet run --project src/LinFan.Daemon -- reload       # make the daemon re-read the config

# GUI (no root) - connects to the running daemon ('run'); otherwise shows it as unreachable:
dotnet run --project src/LinFan.App
```

## Enabling PWM writes (Linux: root + possibly a driver flag)

Reading sensors/speeds works without root; **writing PWM requires root**. Some drivers additionally
lock writes by default:

- **ThinkPad (`thinkpad_acpi`):** unlock fan control first:
  ```bash
  # permanent (survives reboots):
  echo 'options thinkpad_acpi fan_control=1' | sudo tee /etc/modprobe.d/thinkpad_acpi.conf
  # active immediately without a reboot:
  sudo modprobe -r thinkpad_acpi && sudo modprobe thinkpad_acpi fan_control=1
  # verify (must be 'Y'):
  cat /sys/module/thinkpad_acpi/parameters/fan_control
  ```

**Start the daemon as root** - build as your user first, then run the DLL with `sudo` (not
`sudo dotnet run`, which creates root-owned `obj/bin`). Point it at the service's configuration with
`LINFAN_CONFIG`; do not pass your `HOME` along - root would then write its config and log into a folder your account
controls:

```bash
dotnet build
sudo LINFAN_CONFIG=/etc/linfan/config.json dotnet src/LinFan.Daemon/bin/Debug/net8.0/LinFan.Daemon.dll run
```

The loop then reports `dryRun=False` and writes PWM for real (`[Applied]` instead of `[DryRun]`). Config and log
(`/etc/linfan/config.json`, `/etc/linfan/logs/`) are the ones the installed service uses; stop the service first,
next to it the manual daemon exits with code 3 (see "One daemon at a time" below).

**Fail-safe / back to auto:** Ctrl+C restores hardware auto mode; `restore` does it from the CLI (the systemd
unit runs it after every stop, a crash or kill included). As a last resort,
`echo 2 | sudo tee /sys/class/hwmon/hwmon7/pwm1_enable`; `thinkpad_acpi` also reverts to auto on its own
after ~120 s without an update.

## Install as a service (Linux, autostart at boot)

Instead of starting the daemon by hand, a script installs it as a **systemd service** (runs as root at
boot) and adds a **GUI entry** to the app menu:

```bash
./packaging/install.sh        # call without sudo - it elevates the necessary steps itself
```

The script publishes the daemon + GUI to `/opt/linfan`, installs the unit, enables it (`enable --now`),
and adopts any existing configuration. Then:

```bash
journalctl -u linfan-daemon -f          # live service logs
systemctl status linfan-daemon          # status
sudo systemctl stop linfan-daemon       # stop
./packaging/uninstall.sh                # remove again (config stays)
```

- **Shared config path:** the service uses `LINFAN_CONFIG=/etc/linfan/config.json` (set by the unit).
  `LINFAN_CONFIG` overrides the path in general; without it the dev default
  `~/.config/linfan/config.json` applies. Daemon, GUI (via IPC), and CLI thus use the same file.
- **Socket:** the service listens on `/run/linfan/linfan.sock`; the GUI probes the socket paths
  automatically (`LINFAN_SOCKET` → `$XDG_RUNTIME_DIR/linfan.sock` → `/run/linfan/linfan.sock`), so it
  finds the root daemon without any configuration.
- **Access control:** the root daemon restricts the socket to the **`linfan` group** (mode `0660`), so
  only its members - not every local account - can send control commands. `install.sh` creates the group
  and adds you automatically; **log out and back in** (or `newgrp linfan`) for it to take effect. To grant
  another user later: `sudo usermod -aG linfan <user>`. If the group is missing the socket stays
  root-only and the GUI reports that access to the background service is denied.
- **ThinkPad prerequisite:** `thinkpad_acpi fan_control=1` (see above), otherwise the service runs
  read-only; the app's overview, the service log and `LinFan.Daemon list` then show a notice with the fix.
- **GUI autostart at login (optional):** `cp /usr/share/applications/linfan.desktop ~/.config/autostart/`.
- **Sandbox:** the unit confines the root daemon to what it needs - sysfs, `/run/linfan`, `/etc/linfan` - with no
  network and only `CAP_CHOWN`/`CAP_DAC_OVERRIDE` (`systemd-analyze security linfan-daemon` shows the rest). `/home`
  and `/root` are hidden from it, so the .NET runtime the framework-dependent unit starts must live outside them
  (`/usr/bin/dotnet` from the distribution or Microsoft feed; not a snap and not `~/.dotnet`). If a hwmon driver of yours
  refuses a write under the sandbox, the journal shows it - please report it with the driver name.
- **One daemon at a time:** a second daemon that finds the socket held exits with code 3 and leaves the fans to the
  running one - `sudo … run` while the service runs, or the service started while a manual root daemon runs (the unit
  then neither restores nor restarts). If the running daemon is stopped or stuck, the fans go to hardware auto first.

## Install as a service (Windows)

The daemon runs as a **Windows service (LocalSystem)** - writing PWM via LibreHardwareMonitor/WinRing0
requires admin. **Nothing is built** on the target PC: the `win-x64` build is self-contained (runtime
included, no .NET SDK needed). Build from the Linux/dev machine (cross-publish):

```bash
dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.Daemon -o artifacts/LinFan-win-x64/Daemon
dotnet publish -c Release -r win-x64 --self-contained true src/LinFan.App    -o artifacts/LinFan-win-x64/App
```

Copy the `artifacts/LinFan-win-x64` folder to the Windows PC, then install from a PowerShell started
**as administrator**. Without `-Source` the script only installs from a repository checkout's
`artifacts\LinFan-win-x64` (or, in a release ZIP, from the folder it sits in), so point it at a copied build:

```powershell
.\packaging\windows\Install-LinFan.ps1 -Source C:\path\to\LinFan-win-x64   # service + GUI Start-menu entry
.\packaging\windows\Uninstall-LinFan.ps1          # remove again (config stays)
```

Alternatively, a **one-click installer** from `packaging/windows/linfan.iss` (Inno Setup 6, compiled on
Windows) - it bundles the build and calls the same scripts. Details: `packaging/windows/README.md`.

- Both scripts **close a running GUI** from the install directory first - it holds `App\*.dll`, which
  Windows would not let them overwrite or delete. Unsaved editor changes are lost; start the GUI again
  from the Start menu afterwards.
- **Log out and back in once after the first install.** Access to the pipe is restricted to the local
  group `LinFan Users`, which the installer creates and adds you to - and Windows only applies group
  membership at sign-in. Until then the GUI reports that access to the service is denied, although it is
  running (same situation as the `linfan` group on Linux).
- Start the **GUI** as a **normal user** (Start menu → "LinFan"), **not** as admin - it connects to the
  service over the named pipe `\\.\pipe\linfan`.
- **Machine-wide config** under `%ProgramData%\linfan\config.json`, so the SYSTEM service and the user
  GUI see the same file (daemon = sole writer).

### "Windows protected your PC" (SmartScreen)

The Windows binaries are **not code-signed yet**, so SmartScreen shows a blue dialog on the first run of
a download and the UAC prompt says *Unknown publisher*. That is a statement about the missing signature,
not about the file - an unsigned installer has no reputation to look up, and every new release starts
from zero.

To continue: **More info → Run anyway**. Nothing is silently blocked; the dialog only hides the button
behind that link.

Verify the download first - the release page carries a `SHA256SUMS.txt` next to the assets:

```powershell
Get-FileHash .\LinFan-Setup-<version>-win-x64.exe -Algorithm SHA256   # compare with SHA256SUMS.txt
```

```bash
sha256sum -c SHA256SUMS.txt --ignore-missing   # Linux/macOS, or Git Bash on Windows
```

A matching hash proves the download arrived intact - it rules out a corrupted transfer or a tampered
mirror, but not a compromised release page itself. Only a signature would additionally prove **who**
built the file; that is planned (SignPath for open-source projects).

## Run on macOS (no service yet)

There is no `launchd` daemon yet - the daemon is started by hand:

```bash
dotnet run --project src/LinFan.Daemon -- monitor   # read-only, no root, no daemon needed

# Full control: daemon as root in one terminal, GUI (as your user) in another.
sudo dotnet run --project src/LinFan.Daemon -- run   # binds /Library/Application Support/linfan/linfan.sock
dotnet run --project src/LinFan.App                  # GUI connects over IPC
```

Reading needs no root; **fan control needs `sudo`** (SMC writes are privileged). The root daemon makes the
socket reachable only for the invoking user (via `SUDO_UID`, mode `0600`). Do **not** `sudo` the GUI.

`sudo dotnet run` leaves `obj/bin` owned by root here just as it does on Linux - build as your user
first and run the built DLL with `sudo` instead (see the note under
[Enabling PWM writes](#enabling-pwm-writes-linux-root--possibly-a-driver-flag)).

## Packaging

| Platform  | Format                                                                  | Privilege setup               |
|-----------|-------------------------------------------------------------------------|-------------------------------|
| Linux ✅  | binary tarball, `.deb`, self-extracting `.run` + systemd unit (`packaging/linux/`) | systemd service    |
| Windows ✅| Inno Setup `.exe`, ZIP + PowerShell service scripts (`packaging/windows/`) | Windows Service (LocalSystem) |
| macOS     | *(target)* `.app` + LaunchDaemon, signed/notarized                      | SMJobBless / XPC              |

Releases (tarball, ZIP, and the installers) are built by CI from a version tag and published on the
GitHub releases page.
