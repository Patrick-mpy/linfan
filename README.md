# LinFan

Cross-platform fan control for **Linux, Windows, and macOS** — modern, minimal, open source.

LinFan reads temperature sensors and fans, calibrates them automatically on first start, and drives fan speed with freely definable curves (temperature → power). A privileged daemon runs the control loop with a fail-safe watchdog; the GUI runs without root and talks to it over local IPC.

**Status:** Linux and Windows support is complete — the solution builds on .NET 8 with 1000+ tests
green, validated on real hardware (ThinkPad/AMD on Linux, NCT6797D on Windows). macOS support is best-effort and works on **Apple Silicon** (validated live on an M2 Pro): the IOKit/SMC backend reads temperatures and fan RPM without root and **controls fans** (SMC target-RPM, `F*Md`/`F*Tg`) when the daemon runs as root — read **and** control confirmed end-to-end via the GUI. See

[Known issues](#known-issues) for the current gaps and the [CHANGELOG](CHANGELOG.md) for the release history.

## Features

- **Read** fans (RPM) and temperature sensors, live in the dashboard.
- **Auto-calibration** on first start: ramps each PWM channel 0→100 %, detects which channels actually drive a fan, the start-up/stall point, and the PWM→RPM characteristic.
- **Curve editor** as a graph: temperature → power, draggable points, hysteresis, live operating point.
  A curve can be assigned to one or more fans.
- **Profiles** (e.g. silent / performance) with their own curves, switchable from the dashboard.
- **Manual mode** per fan (slider), rename fans/sensors, maintain install position.
- **Fail-safe:** over-temperature, a crash, or shutdown → fans to hardware auto / 100 %.

## Quick start

### Install a release

Grab the asset for your platform from the
[releases page](https://github.com/Patrick-mpy/linfan/releases):

```bash
# Debian/Ubuntu
sudo apt install ./linfan_<version>_amd64.deb

# any other distro — self-extracting installer (elevates itself, no sudo needed):
chmod +x LinFan-Setup-<version>-linux-x64.run && ./LinFan-Setup-<version>-linux-x64.run

# or: unpack the tarball and run the same installer script
tar xzf linfan-<version>-linux-x64.tar.gz && ./packaging/linux/install-bin.sh
```

All three set up the systemd service and the `linfan` socket group — **log out and back in once**, then start "LinFan". On **Windows**, run `LinFan-Setup-<version>-win-x64.exe` (or unpack the ZIP and run `Install-LinFan.ps1` as admin); that sets up the service and the `LinFan Users` group and likewise needs **one log out and back in**. Details — privileges, service, uninstall — in **[docs/INSTALL.md](docs/INSTALL.md)**.

### Build from source

```bash
dotnet build                                     # build everything
dotnet test                                      # test suite

# Daemon (Linux) — reading sensors works without root:
dotnet run --project src/LinFan.Daemon -- list   # list sensors & fans
dotnet run --project src/LinFan.Daemon -- run    # control loop + IPC server (dry run without root)

# GUI (no root) — connects to a running daemon:
dotnet run --project src/LinFan.App
```

**Writing PWM for real requires root** (on Linux possibly a driver flag such as
`thinkpad_acpi fan_control=1`); in normal operation the daemon runs as a **systemd service**. The full
guide — privileges, service installation, and Windows — is in **[docs/INSTALL.md](docs/INSTALL.md)**.

**macOS** (no `launchd` service yet — start the daemon manually):

```bash
dotnet run --project src/LinFan.Daemon -- monitor   # read-only, no root, no daemon needed

# Full control: daemon as root in one terminal, GUI (as your user) in another.
sudo dotnet run --project src/LinFan.Daemon -- run   # binds /Library/Application Support/linfan/linfan.sock
dotnet run --project src/LinFan.App                  # GUI connects over IPC
```

Reading needs no root; **fan control needs `sudo`** (SMC writes are privileged). The root daemon makes the socket reachable only for the invoking user (via `SUDO_UID`, mode `0600`). Do **not** `sudo` the GUI.

## Architecture

**MVC** with process separation: the **model** (domain + hardware access) lives authoritatively in the **privileged daemon**, while **view + controller** run in the user process. Writing PWM requires
root/admin everywhere — instead of privileging the GUI, the slim daemon encapsulates the sensitive part.
The **IPC boundary is the Controller↔Model boundary**.

```
        GUI process (user, no root)                   Daemon process (privileged)
   ┌───────────────────────────────────────┐      ┌──────────────────────────────────┐
   │   VIEW                CONTROLLER       │ IPC  │   MODEL (authoritative)          │
   │   Avalonia .axaml ◄─► presentation     │◄────►│   domain + services + hardware   │
   │   (DataContext)       logic / commands │      │   curve engine, calibration,     │
   │                                        │      │   fail-safe watchdog             │
   └───────────────────────────────────────┘      └──────────────────────────────────┘
```

Layers, dependency rules, the IPC contract, and threading in detail: **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**.

## Tech stack

| Area              | Choice                                    |
|-------------------|-------------------------------------------|
| Language / runtime| C# / .NET 8 (LTS)                         |
| GUI               | Avalonia UI 11 + FluentAvalonia           |
| Controller        | CommunityToolkit.Mvvm                     |
| Hardware Linux    | direct `sysfs`/`hwmon` access             |
| Hardware Windows  | LibreHardwareMonitorLib (MPL-2.0)         |
| Persistence       | System.Text.Json                          |
| Service / logging | Microsoft.Extensions.Hosting, Serilog     |

The platform-specific hardware access (the core of the project) and the full dependency list are in **[docs/HARDWARE.md](docs/HARDWARE.md)**.

## Documentation

- **[docs/INSTALL.md](docs/INSTALL.md)** — installation & operation (Linux + Windows), privileges, service.
- **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** — MVC layers, IPC contract, fail-safe, data model.
- **[docs/HARDWARE.md](docs/HARDWARE.md)** — platform-specific hardware access, calibration, dependencies.
- **[CHANGELOG.md](CHANGELOG.md)** — release history.

## Known issues

- **macOS on Intel is untested.** The control path is the same code as on Apple Silicon, but it has only been validated live on an M2 Pro — reports from Intel Macs are welcome.
- **macOS has no `launchd` service yet** — the daemon is started manually (see Quick start); auto-start on boot is still open.
- **macOS temperature sensors come from a curated key list** — on some models sensors may be missing. Open an issue with your model and the `monitor` output.
- **Group membership needs a re-login (Linux and Windows).** The installer creates the IPC access group (`linfan` on Linux, `LinFan Users` on Windows); until you log out and back in, the GUI reports the daemon as unreachable even though it is running.
- **Some sensor channels report intermittent read errors** (e.g. ThinkPad EC, `EIO`) — they show as a missing value; the control loop and the watchdog keep running.
- **The Windows binaries/installer are unsigned** — SmartScreen warns on first run. Installation needs admin rights; the service then runs as SYSTEM.

## Contributing

Issues and pull requests are welcome — **[CONTRIBUTING](.github/CONTRIBUTING.md)** has the setup, quality gates, and conventions. Before a PR, please keep `dotnet build`, `dotnet test` and `dotnet format` green (the conventions are enforced by `.editorconfig`). Releases are cut from version tags and built/published by CI. For vulnerabilities, please follow the **[security policy](.github/SECURITY.md)** instead of opening a public issue.

## License

**GNU General Public License v3.0 or later** (`GPL-3.0-or-later`) — full text in [LICENSE](LICENSE).
Copyright © 2026 Patrick Machynia. Distributed **without any warranty**. The dependencies are GPLv3-compatible (Avalonia, CommunityToolkit.Mvvm: MIT; LibreHardwareMonitorLib: MPL-2.0).
