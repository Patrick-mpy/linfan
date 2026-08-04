# Changelog

All notable changes to LinFan, summarized per release. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioned by SemVer,
the git tag `vX.Y.Z` is the source of truth.

Open items are tracked as GitHub issues.

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
