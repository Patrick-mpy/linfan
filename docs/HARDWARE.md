# LinFan - Hardware Access & Calibration

All platform code lives behind `ISensorBackend` / `IFanController` in
`LinFan.Hardware.{Linux,Windows,Mac}` (contract: [docs/ARCHITECTURE.md](ARCHITECTURE.md) §6).
Installation & operation: [docs/INSTALL.md](INSTALL.md).

## 1. Platform-specific access

### 1.1 Linux (priority)

- **Interface:** the kernel `hwmon` subsystem via `sysfs` (`/sys/class/hwmon/hwmonX/`).
  - `fanN_input` → current speed (RPM), **world-readable** (reading without root).
  - `pwmN` → control value 0-255 (writing → **root**).
  - `pwmN_enable` → 1 = manual mode (must be set to be able to control).
  - `tempN_input` → temperature (m°C).
- **Required kernel modules** (depending on mainboard/CPU):
  - Super-I/O chips: `nct6775` (Nuvoton), `it87` (ITE), `f71882fg` (Fintek).
  - CPU: `coretemp` (Intel), `k10temp` / `zenpower` (AMD).
  - Drives: `drivetemp` (SATA), NVMe via `nvme` sysfs.
  - GPU: `amdgpu` (hwmon). NVIDIA's proprietary driver has no hwmon interface and LinFan does not use NVML, so with
    that driver it sees neither the card's fans nor its temperature.
- **lm-sensors** (`sensors-detect`, `sensors -j`) helps find and load the right modules and to compare readings.
- **Pitfalls:**
  - Many newer boards (Gigabyte/ASUS/MSI) need the **out-of-tree driver** `it87` (frankcrawford/it87)
    or `nct6687d`, because the mainline driver does not know the chip.
  - Sometimes `acpi_enforce_resources=lax` is required as a kernel parameter so the module may claim the
    chip (ACPI conflict).
  - `thinkpad_acpi` without `fan_control=1` refuses every fan write with EPERM, root's included - `access(2)`
    still reports the file as writable. LinFan reads the module parameter and shows the fan as read-only, with the
    reason and the fix.
- **Privilege model:** the daemon runs as a root systemd service; the GUI runs as the user and reaches it over a socket
  restricted to the `linfan` group. (No setuid, no polkit.)

### 1.2 Windows

- **No** public API for Super-I/O chips → you need a **kernel driver** for ring-0 access to I/O ports &
  PCI config.
- **LibreHardwareMonitorLib** solves this: it contains chip-specific drivers for ITE/Nuvoton/Fintek, CPU
  and GPU temps, and ships the required kernel driver (WinRing0-based). Fan control via
  `Control.SetSoftware(percent)`.
- **Requirements/risks:**
  - The app/service must run **as administrator** (to load the driver).
  - Microsoft's *vulnerable-driver blocklist* / HVCI (Memory Integrity) can block the old WinRing0
    driver → use a current, signed LHM version.
- **Discovery filter:** LHM's Intel "… Distance to TjMax" sensors are skipped - their value is TjMax − T and
  falls as the CPU heats up, so as temperatures they would pull CPU averages (and fan speeds) down.
- Covers ~95 % of desktop mainboards.

### Summary

| Platform | Read        | Control (PWM)     | Privilege to control |
|----------|-------------|-------------------|----------------------|
| Linux    | sysfs/hwmon | sysfs `pwmN`      | root (systemd)       |
| Windows  | LHM         | LHM `SetSoftware` | administrator        |
| macOS    | IOKit/SMC   | SMC `F*Tg`/`F*Md` | root (`sudo`; no LaunchDaemon yet) |

"Not controllable" is a **regular state**, not an error - the UI shows such channels as read-only.

## 2. Libraries & dependencies

### NuGet (App/Core - all platforms)

| Package                                                     | Purpose                                     | License |
|-------------------------------------------------------------|---------------------------------------------|---------|
| Avalonia (Desktop, Themes.Fluent, Fonts.Inter)              | GUI framework - the charts are drawn with it too | MIT |
| CommunityToolkit.Mvvm                                       | controllers (observable properties, commands) | MIT   |
| Microsoft.Extensions.Hosting (+ Systemd, WindowsServices)   | daemon, service lifetime, logging           | MIT     |
| System.Text.Json                                            | persistence (in the framework)              | MIT     |

The daemon's file log is LinFan's own provider on `Microsoft.Extensions.Logging` - no logging library.

### NuGet (hardware Windows)

| Package                 | Purpose                            | License |
|-------------------------|------------------------------------|---------|
| LibreHardwareMonitorLib | Super-I/O/CPU/GPU + kernel driver  | MPL-2.0 |

### Linux - system requirements (not NuGet libs, but kernel/distro)

- A running `hwmon` subsystem + matching kernel modules (see §1.1).
- `lm-sensors` (recommended, for `sensors-detect`/fallback).
- possibly the out-of-tree driver `it87` / `nct6687d` (DKMS).
- systemd (for the privileged service).
- .NET 8 runtime (or a self-contained build → no runtime needed).

### macOS - requirements

- IOKit (system framework), called directly via P/Invoke - no native helper.
- Not there yet: a LaunchDaemon for autostart, code signing and notarization.

## 3. Onboarding / calibration flow

1. Discover the sensors and the PWM channels; channels the backend cannot write show as read-only, with the reason.
2. Per controllable fan, couple its speed sensor: drive this fan, throttle the others, take the tachometer that
   responds. A fan without one is skipped ("no tacho") - a calibration needs a speed sensor of its own.
3. Calibrate it: let it run out at PWM 0, ramp the control value in steps 0→100 %, measure the RPM after a settle
   time → the PWM→RPM samples and the start-up point, stored with the speed sensor they were measured with.
4. Show the result → name the fans, confirm the sensor groups suggested from each sensor's driver, save.
5. Then propose three profiles (silent / balanced / performance) - with a curve per role (CPU, GPU, intake,
   exhaust) where the fans' positions are known, else driven by one primary sensor.

Every step is also available per fan afterwards, under Settings → Fans: calibration, identification
(pulse the fan to see which one it is), and coupling a speed sensor - the last one drives the fan up
while throttling the others and assigns the tachometer that responds.

Afterwards the service keeps checking: a fan whose speed stays clearly off its calibration for a minute, or that was
calibrated with another speed sensor than it uses now, gets a notice to recalibrate - in the overview and at the fan in
the settings. The device behind each fan and sensor is remembered too, so a replaced graphics card, drive or Wi-Fi card
is noticed; a case fan swapped on the same header only shows through the speed check.

![Fan settings: fans in one section per mounting position, each row with live speed, hardware path, an identify button and a position menu; the CPU fan's advanced part is open in three blocks - the power range as one slider with two handles, the calibrated start point marked on it and min/max steppers with their raw PWM; calibration & tach with the coupled speed sensor and the start point, each with a status mark, and the recalibrate and couple buttons; and a test block with a curve/manual switch and the live speed](images/settings.jpg)

> During calibration keep the **safety limit** active (on over-temperature immediately fall back to
> 100 % or hardware auto mode). Fail-safe details: [docs/ARCHITECTURE.md](ARCHITECTURE.md) §7.
