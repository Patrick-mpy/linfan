# Changelog

All notable changes to LinFan, summarized per release. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioned by SemVer,
the git tag `vX.Y.Z` is the source of truth.

Open items are tracked as GitHub issues.

## [0.3.1] - 2026-08-13

One window again — no matter how often you start LinFan.

### Fixed

- **LinFan opens only once.** Every launch used to start a GUI of its own: several windows on the same
  daemon, each with its own tray icon, each saving settings and window geometry independently. A further
  launch — desktop icon, start menu, autostart — now hands over to the instance that is already running
  and brings its window to the front, out of the tray as well, instead of adding another one.
- **Repository only:** line endings are now fixed to LF on every platform. A Windows checkout used to
  hand out CRLF while the project's own formatter writes LF, so running it left Git reporting hundreds
  of changed files that did not differ by a single byte. Nothing about the application changes.

## [0.3.0] - 2026-08-12

LinFan gets its own look — and the setup assistant no longer loses fans along the way.

### Added

- **LinFan logo and app icons**: the wordmark in the GUI header and on the assistant's welcome page,
  plus a proper icon for the window and tray, for the executable in Explorer and the Start menu on
  Windows, and for the application menu and dock on Linux (it used to borrow a generic system icon).
  The installers and the `.deb` ship it.
- **Windows: the native title bar follows the app theme** — no more light system bar above a dark window.

### Fixed

- **The setup assistant no longer loses the fan after a skipped one.** A coupling request that arrived
  right after the previous run was silently discarded, so the GUI waited out its timeout and wrote the
  fan off — skipping both its speed-sensor coupling and its calibration. Requests now wait out the cool
  down instead of vanishing.
- **Inert fans are no longer reported as having no tachometer.** The reference measurement waits longer
  for the fan to coast down — coasting down takes far longer than spinning up, and a large CPU cooler
  was still near full speed when measured. The log now also records what was measured, so a fan that
  genuinely has no tachometer can be told apart from one that was measured too early.
- **The speed-sensor coupling refuses to start when it is already hot.** It parks every fan near zero
  for the measurement, so it now requires headroom below the fail-safe limit rather than starting just
  under it.
- **No more raw hardware paths as fan names.** Entries created by a calibration or a coupling kept the
  device path (`/lpc/nct6797d/0/control/1`) as their display name. They now show the hardware label
  until you pick a name of your own, and the calibration message names the fan instead of its id.
- **Restoring a backup no longer drops the speed-sensor assignments.** The GUI never put them on the
  wire, so a restore reset every fan to "no sensor" even though the backup file held them — and every
  coupling you had run was lost with it. The calibration was already carried; both travel together now.
- **The English interface was partly German**: the theme picker ("Hell"/"Dunkel") and the point and
  sensor counts in the curve editor and dashboard now follow the display language and switch with it.
- **Setup assistant, calibration step**: the content scrolls, so the progress card no longer pushes over
  the buttons while a fan is being coupled or calibrated.

### Changed

- The device lists' filter reads **"Hide here too"** instead of "Hide hidden" — it hides entries you
  already marked hidden from *this* list as well.

## [0.2.0] - 2026-08-04

Curve and UI refinements — step interpolation, visibly smoother splines, toast notifications,
and a sharper airflow analysis.

### Added

- **Step interpolation** for fan curves: the points act as thresholds — the level holds until the
  next point is reached (classic BIOS-style control, avoids constant small adjustments).

### Removed

- **Free-form fan groups**: fans now group solely by their installed position (the manual group
  field was a redundant override). Existing fan group names in the config are ignored on load and
  dropped on the next save. Sensor groups are unaffected.

### Changed

- **Spline interpolation now visibly smooths**: the engine no longer clamps the spline to the
  straight connection between points, which had made it indistinguishable from linear on typical
  fan curves. Monotonicity and no-overshoot guarantees remain.
- **Airflow analysis**: hidden fans and sensors are excluded entirely (no suggestion, no pressure
  weight, no curve source). CPU/GPU curves now average *all* sensors of their component (matched
  by name or group) instead of tracking a single one — more representative on AMD CPUs whose
  Tctl/Tdie read far above the SoC sensor.
- **Default curves reach full speed earlier**: suggested and onboarding curves hit 100 % at
  82–86 °C, comfortably below the fail-safe threshold (90 °C), so fans ramp up smoothly before the
  watchdog would take over.
- Curve editor: the mix, interpolation, and hysteresis inputs are right-aligned.
- **Notifications are toasts now**: the header banners (unsaved changes, daemon unreachable,
  calibration progress, update available) and the inline status lines (save result, backup/import)
  moved into a toast overlay at the top right — showing one no longer shifts the page. Every toast
  has a close button; success messages fade out on their own, errors stay until dismissed.

### Fixed

- A latent spline bug that let non-monotonic curves dip below the lowest drawn point (stale
  tangent values in the Fritsch–Carlson limiter).
- Group suggestions: clicking a suggestion in the sensor group field now applies that suggestion
  instead of creating a new group from the typed prefix.
- Group names that differ only in casing ("cpu" vs. "CPU") now land in one block on the dashboard
  and in the curve editor.
- Inputs release keyboard focus when clicking elsewhere in the window; pending edits in the group
  field are committed by the click-away.
- The "Ungrouped" fallback label on the dashboard and in the curve editor is localized now (it was
  hardcoded German).

## [0.1.0] - 2026-08-03

First public release — fan control for **Linux** (primary focus) and **Windows**,
**macOS** best-effort. License: GPL-3.0-or-later.

### Added

- **Daemon + GUI, cleanly separated**: a privileged daemon (systemd / Windows service) drives
  the fans along temperature curves (interpolation, hysteresis, clamping); the Avalonia GUI runs
  without root and talks to it over local IPC (Unix socket / named pipe).
- **Fail-safe built in**: on over-temperature, errors, or shutdown a watchdog ramps the fans to
  full speed or hands them back to the hardware's automatic control.
- **Onboarding with calibration**: per fan, the PWM→RPM curve and the spin-up point are measured;
  an automatic tachometer coupling empirically pairs each fan with its RPM sensor.
- **GUI**: live dashboard (temperatures, fan speeds), curve editor with profiles, case view with
  fan positions, tray mode, configuration backup/restore, interface in English and German.
- **Hardware backends**: Linux `sysfs`/hwmon, Windows LibreHardwareMonitor, macOS IOKit/SMC
  (validated on Apple Silicon; reading without root, control as root — service integration to
  follow).
- **Update notice**: the GUI reports newer GitHub releases (notice only, no auto-download, can be
  disabled).
- **Packages**: Linux tarball plus `.deb`/`.run` installers, Windows ZIP and a one-click
  installer (`.exe`).
