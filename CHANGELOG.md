# Changelog

All notable changes to LinFan, summarized per release. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioned by SemVer,
the git tag `vX.Y.Z` is the source of truth.

Open items are tracked as GitHub issues.

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
