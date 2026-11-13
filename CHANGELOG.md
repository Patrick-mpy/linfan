# Changelog

All notable changes to LinFan, summarized per release. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioned by SemVer,
the git tag `vX.Y.Z` is the source of truth.

Open items are tracked as GitHub issues.

## [0.5.0] - 2026-11-13

A new overview that shows what needs attention, an app that notices replaced hardware - and a security review of
everything between the app, the service and the fans.

### Added

- **A new overview.** Four tiles - status, hottest sensor, fan average, running profile - and a status that lists
  what needs attention, each notice with the action that resolves it. Below follow one row per control loop (its
  input with a history, a mini curve, the fans it drives, an on/off switch), the temperatures per sensor group and a
  fan table whose "Controlled by" shows what the service really did. Without a connection the overview keeps the
  last state, dimmed, and locks every control.
- **Replaced hardware is noticed.** The service remembers the device behind each fan and sensor - a graphics card,
  drive or Wi-Fi card by its PCI ids or model, on Windows by its hardware name. When another one turns up under the
  same entries, a dialog offers to keep the settings, recalibrate or set it up as new; until then its fans run as
  before. Entries of hardware that is gone can be removed there too.
- **Fans are checked against their calibration.** A fan that stays clearly slower or faster than calibrated for a
  minute, or stands although it should turn, gets a notice to recalibrate - on the overview and at the fan in the
  settings, next to its recalibrate button. It is the only way a case fan swapped on the same header shows up. Backups
  keep the calibration's measurements.
- **Two more themes and an accent colour of your own.** Terminal (green on black in the system's terminal font) and
  High contrast (all text at 7:1, framed controls, a strong focus ring); the accent comes from six presets or a hex
  value and is adjusted per theme to stay readable. Temperature colours follow the theme.
- **Undo and redo for every edit** (Ctrl+Z, Ctrl+Y). Live actions - switching the profile, a curve's on/off switch,
  a calibrated start point - stay out of it.
- **Case types: tower, server / rack, laptop and open bench**, each with its own diagram and positions; laptop and
  open bench have no pressure balance. Intake and exhaust may share a side of the case.
- **Sensor groups in Settings → Sensors**: rename, reorder, merge and delete them, or group the sensors
  automatically by driver; several sensors move at once or by drag and drop.
- **Curve templates and a better chart.** The fans' power limits and the hysteresis band are shaded; a double-click
  adds a point, Delete removes it, the arrow keys move it.
- **Banners that say what is wrong**: the service is unreachable (with the command to check it), access is denied
  (group membership), the service lacks the rights to write fan speeds, or it sends data this app cannot read.
- **Reasons instead of guesses.** The app and `LinFan.Daemon list` name why a fan is read-only - missing rights, a
  driver lock (a ThinkPad without `fan_control=1`, with the fix) or no control interface - and the overview says
  when the service could not use the saved configuration, and where it moved an unusable one.
- **An error log for the app.** An unexpected error no longer closes it; it goes to `gui.log`, and a notice shows
  where.
- **The version under the settings menu**, with a GitHub link and a note when the service runs another version.
- **Fans can be named in the setup assistant** - unnamed ones are "Fan 1", "Fan 2" … everywhere - and a sensor gets
  its hardware name back with one click.

### Changed

- **A reworked layout.** A header with the profile button and a gear for the settings, unsaved changes in a floating
  bar (undo, redo, discard, apply, Ctrl+S), profiles and curves in one tree, fans sorted by mounting position, and
  the airflow page built around a diagram of the case.
- **Profiles switch from the header, unsaved changes or not** - "Apply & switch", or "Discard & switch", which Ctrl+Z
  takes back.
- **A fan's settings in three blocks.** Min and max share one slider that stops at the calibrated start point,
  calibration and speed sensor show their state before their actions, and the manual test shows the live speed.
- **The airflow proposal goes into a profile you pick**, or a copy of it, and lists only what would change.
- **One speed sensor, one fan - kept by the service too.** A fan whose sensor another fan is coupled to shows no
  speed and cannot be calibrated until it has its own, instead of measuring the other fan.
- **The service sends its configuration only when it changed**, not with every update.

### Fixed

- **Closing the console window hands the fans back (Windows).** Closing the window of a hand-started daemon or of
  `set`/`calibrate` with the X ended the process before the fans were returned: a fan stayed on its manual value, and
  the next run counted as untrusted until a restart. The process now waits for the hand-back first.
- **Calibration waits for a fan to stop.** A fan still running down read as turning at 0 % and got a start point of
  0 %, where its curve could hold it standing still. A fan whose start point shows 0 % is worth calibrating again.
- **Fans end up where they belong.** After identify, coupling or a failed calibration they return to their curve
  instead of staying on hardware control. A fan let go by hand, by a curve without a readable sensor or by a write
  that failed part-way goes back to hardware control instead of keeping its last speed - possibly 0; channels without
  an automatic mode go to full speed.
- **A fan's maximum can no longer be set below its start point**, where every curve value would have stopped it.
- **Manual control starts at the fan's current power** instead of dropping to 0 % first, and its switch follows when
  the service lets go.
- **A speed the hardware refuses is no longer shown as applied**, and a control mode that cannot be read shows as
  unknown instead of automatic.
- **Profiles and edits behave.** Applying and switching in quick succession no longer cancel each other out, the app
  never claims a profile the service did not switch to, opening a profile shows no phantom changes, the airflow
  proposal no longer mixes up profiles, and a calibrated start point is no longer lost to a save made at the same
  moment.
- **Sensor names follow the hardware.** Hardware labels were saved as own names, so a replaced graphics card kept the
  old model's name; "Use the hardware name" repairs such a name.
- **The CPU curve no longer reads too low.** Cooler sensors in the CPU group (a Ryzen SoC, the mainboard's "CPUTIN")
  and Intel's "Distance to TjMax" no longer pull the CPU temperature down.
- **Linux: a curve stays with its fan when the kernel renumbers identical sensor chips.**
- **The command line leaves a running daemon alone.** `set`, `calibrate` and `init` refuse while one runs (exit code
  3), and `list` or `monitor` as root no longer hand its fans back on exit. `set`, `calibrate` and a daemon started by
  hand return the fans when their terminal closes.
- **Importing a backup asks first**, and reports success only once the service took it.
- **Actions the service would refuse are not offered.** Calibrate, identify, couple and the manual test are locked,
  with the reason, in a dry run, while offline or during another device action - and calibrate also for a fan without
  a speed sensor to measure with, until one is coupled.
- **Smaller fixes:** readable checks and switches in the dark theme, readable tooltips on accent and red buttons, theme
  tiles aligned left with as many per row as fit, the curve editor follows a language switch, no `systemctl` hint on
  macOS, clearer service log messages.

### Security

- **The over-temperature protection cannot be stalled.** A client that stops reading, a configuration that keeps
  failing to apply or a hot sensor that stops reporting no longer keep it from acting, and a failing control step
  hands the fans back to the hardware.
- **Input from the app connection is bounded and checked** - sizes, ranges, ids, control characters - so it can no
  longer slow the control loop, forge log entries or leave a configuration that keeps the daemon from starting. An
  unusable configuration file is set aside as `config.json.invalid…` and the fans stay on hardware control; a reset
  keeps the over-temperature limit.
- **Other local accounts can no longer block fan and temperature access** with file locks.
- **Stopping always hands the fans back.** The restore runs right at the stop request and, on Linux, at every start;
  systemd restarts a hung daemon and runs `restore` after every stop, a crash included.
- **Only one daemon drives the fans.** A second one finds the running daemon, exits with code 3 and touches nothing -
  on Windows too.
- **The systemd service runs in a sandbox** (exposure 9.4 → 3.0): no network, no home folders, only the hwmon files
  and its own paths. The framework-dependent unit therefore needs the .NET runtime outside `/home` and `/root`.
- **Least privilege and an audit trail.** A daemon without root touches no fan, the app connection takes at most 16
  clients, configuration and log are owner-only on Linux, and the log names the account and process behind every
  change.
- **App hardening.** Messages from the service are capped at 16 MiB, the update notice only opens LinFan's release
  page, and the single-window check no longer falls back to the shared temporary folder.
- **Windows: no slow fans after a crash.** After a crashed LinFan process, or next to one still writing, fans without
  a curve run at full speed until the next reboot instead of at the speed it left behind.
- **Windows: locked-down service access.** Without the "LinFan Users" group nobody but administrators gets in, the app
  only talks to a service run by the system or the administrators, and no other account can reach your LinFan window.
- **Windows: locked-down data folder and install source.** Installing or upgrading makes `%ProgramData%\linfan`
  read-only for users and removes what LinFan did not write there; the install script only installs the build that
  came with it.

## [0.4.0] - 2026-08-15

The fans stop chasing every temperature spike - and a profile can be built before it takes over.

### Added

- **Curve smoothing.** Each curve averages its temperature over a short window - 3 seconds by default,
  adjustable per curve, `0` switches it off - before it is applied. Some processors, AMD's in particular,
  report brief and very steep spikes the moment load arrives, and the fans would surge and drop back
  within seconds; hysteresis could not absorb that, because it filters how *far* the temperature moves,
  not how *briefly*. Sustained load still arrives in full, only a little later, and the over-temperature
  protection keeps reacting to the raw reading, immediately.
- **Settings → Airflow shows what is already in effect.** The section used to open blank, so a machine
  that had been tuned looked exactly like one that never had been. It now lists the fans following an
  airflow role curve - each with its mounting position and curve - above the pressure balance of their
  positions, for the profile you have open.

### Changed

- **A profile can be prepared before it takes over the fans.** Picking a profile used to hand the fans to
  it on the spot, so there was no way to build another setup without the machine following along. The
  side menu now lists profiles the way it lists curves: selecting one opens it, a green dot marks the one
  actually regulating, and only its "Active" switch hands the fans over - once pending changes are
  applied, since the service would otherwise take over its own saved copy rather than what is on screen.
  The overview therefore always shows what the service is really running.
- **Profiles and unsaved changes moved into the window header.** Both tabs lost the bar above their
  content and gained its height: curves and profiles are managed in the curves tab's side menu, each list
  with a plus in its heading, while the running profile and the buttons to apply or discard pending
  changes sit in the header, in reach from every tab. A blocked "Active" switch now names its reason
  instead of sitting greyed out without a word.
- **The interface tells things apart by more than text.** The three tabs and the curve editor's sensor and
  fan lists carry symbols; message cards show a severity icon beside a wider stripe and switch both when
  their severity changes; a screen's confirming action is filled in the accent colour and destructive ones
  in red, so colour keeps meaning something; and an active curve's temperature is coloured on the same
  scale as the readings below it - amber from 60 °C, red from 80 °C.
- **Sections slide open instead of jumping.** Active curves, temperatures, fans, a curve's data points and
  a fan's advanced settings expand and close in a short animation, the header arrow folding over in the
  same motion - and no longer clipped by the header row on the way.

### Fixed

- **Dialogs get the dark title bar too.** A confirmation or setup window kept the light system title bar
  for as long as it was open, while the main window had the right one.
- **Installing over a running LinFan works on Windows.** The install and uninstall scripts closed the
  service but not the app, so an upgrade stopped with "the process cannot access the file
  Avalonia.Base.dll" and an uninstall silently left files behind. Both now close a running app from the
  install directory first; one started from a source tree is left alone. The one-click installer was
  never affected, and the Linux uninstall script gained the same step.

## [0.3.1] - 2026-08-13

One window again - no matter how often you start LinFan.

### Fixed

- **LinFan opens only once.** Every launch used to start a GUI of its own: several windows on the same
  daemon, each with its own tray icon, each saving settings and window geometry independently. A further
  launch - desktop icon, start menu, autostart - now hands over to the instance that is already running
  and brings its window to the front, out of the tray as well, instead of adding another one.
- **Repository only:** line endings are now fixed to LF on every platform. A Windows checkout used to
  hand out CRLF while the project's own formatter writes LF, so running it left Git reporting hundreds
  of changed files that did not differ by a single byte. Nothing about the application changes.

## [0.3.0] - 2026-08-12

LinFan gets its own look - and the setup assistant no longer loses fans along the way.

### Added

- **LinFan logo and app icons**: the wordmark in the GUI header and on the assistant's welcome page,
  plus a proper icon for the window and tray, for the executable in Explorer and the Start menu on
  Windows, and for the application menu and dock on Linux (it used to borrow a generic system icon).
  The installers and the `.deb` ship it.
- **Windows: the native title bar follows the app theme** - no more light system bar above a dark window.

### Fixed

- **The setup assistant no longer loses the fan after a skipped one.** A coupling request that arrived
  right after the previous run was silently discarded, so the GUI waited out its timeout and wrote the
  fan off - skipping both its speed-sensor coupling and its calibration. Requests now wait out the cool
  down instead of vanishing.
- **Inert fans are no longer reported as having no tachometer.** The reference measurement waits longer
  for the fan to coast down - coasting down takes far longer than spinning up, and a large CPU cooler
  was still near full speed when measured. The log now also records what was measured, so a fan that
  genuinely has no tachometer can be told apart from one that was measured too early.
- **The speed-sensor coupling refuses to start when it is already hot.** It parks every fan near zero
  for the measurement, so it now requires headroom below the fail-safe limit rather than starting just
  under it.
- **No more raw hardware paths as fan names.** Entries created by a calibration or a coupling kept the
  device path (`/lpc/nct6797d/0/control/1`) as their display name. They now show the hardware label
  until you pick a name of your own, and the calibration message names the fan instead of its id.
- **Restoring a backup no longer drops the speed-sensor assignments.** The GUI never put them on the
  wire, so a restore reset every fan to "no sensor" even though the backup file held them - and every
  coupling you had run was lost with it. The calibration was already carried; both travel together now.
- **The English interface was partly German**: the theme picker ("Hell"/"Dunkel") and the point and
  sensor counts in the curve editor and dashboard now follow the display language and switch with it.
- **Setup assistant, calibration step**: the content scrolls, so the progress card no longer pushes over
  the buttons while a fan is being coupled or calibrated.

### Changed

- The device lists' filter reads **"Hide here too"** instead of "Hide hidden" - it hides entries you
  already marked hidden from *this* list as well.

## [0.2.0] - 2026-08-04

Curve and UI refinements - step interpolation, visibly smoother splines, toast notifications,
and a sharper airflow analysis.

### Added

- **Step interpolation** for fan curves: the points act as thresholds - the level holds until the
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
  by name or group) instead of tracking a single one - more representative on AMD CPUs whose
  Tctl/Tdie read far above the SoC sensor.
- **Default curves reach full speed earlier**: suggested and onboarding curves hit 100 % at
  82-86 °C, comfortably below the fail-safe threshold (90 °C), so fans ramp up smoothly before the
  watchdog would take over.
- Curve editor: the mix, interpolation, and hysteresis inputs are right-aligned.
- **Notifications are toasts now**: the header banners (unsaved changes, daemon unreachable,
  calibration progress, update available) and the inline status lines (save result, backup/import)
  moved into a toast overlay at the top right - showing one no longer shifts the page. Every toast
  has a close button; success messages fade out on their own, errors stay until dismissed.

### Fixed

- A latent spline bug that let non-monotonic curves dip below the lowest drawn point (stale
  tangent values in the Fritsch-Carlson limiter).
- Group suggestions: clicking a suggestion in the sensor group field now applies that suggestion
  instead of creating a new group from the typed prefix.
- Group names that differ only in casing ("cpu" vs. "CPU") now land in one block on the dashboard
  and in the curve editor.
- Inputs release keyboard focus when clicking elsewhere in the window; pending edits in the group
  field are committed by the click-away.
- The "Ungrouped" fallback label on the dashboard and in the curve editor is localized now (it was
  hardcoded German).

## [0.1.0] - 2026-08-03

First public release - fan control for **Linux** (primary focus) and **Windows**,
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
  (validated on Apple Silicon; reading without root, control as root - service integration to
  follow).
- **Update notice**: the GUI reports newer GitHub releases (notice only, no auto-download, can be
  disabled).
- **Packages**: Linux tarball plus `.deb`/`.run` installers, Windows ZIP and a one-click
  installer (`.exe`).
