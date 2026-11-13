# LinFan - Architecture (detail)

Complements [README.md](../README.md) with the binding architecture details.
Pattern: **MVC** with process separation (privileged daemon ⟷ user GUI).
The **IPC boundary equals the Controller↔Model boundary**.

---

**MVC** with process separation: the **model** (domain + hardware access) lives authoritatively in the
**privileged daemon**, **view + controller** run in the unprivileged user process, and the **IPC boundary
is the Controller↔Model boundary**.

```
        GUI process (user, no root)                   Daemon process (privileged)
   ┌───────────────────────────────────────┐      ┌──────────────────────────────────┐
   │   VIEW                CONTROLLER       │ IPC  │   MODEL (authoritative)          │
   │   Avalonia .axaml ◄─► presentation     │◄────►│   domain + services + hardware   │
   │   (DataContext)       logic / commands │      │   curve engine, calibration,     │
   │                                        │      │   fail-safe watchdog             │
   └───────────────────────────────────────┘      └──────────────────────────────────┘
```

## 1. Layers & responsibilities

### Model - `LinFan.Core` + `LinFan.Hardware.*`

Platform-neutral domain in **`LinFan.Core`**:

- **Models/** - POCOs without behavior/dependencies: `Sensor`, `Fan`, `Curve`, `Profile`, `Config`.
- **Services/** - the actual logic:
  - `CurveEngine` - computes temperature → PWM (interpolation, hysteresis, clamping).
  - `CalibrationService` - the onboarding flow (ramp PWM, measure RPM, start-up point).
  - `ConfigStore` - JSON persistence (load/save, schema versioning).
  - `ControlLoop` - poll cycle: read sensors → apply curves → set PWM.
- **Abstractions/** - `ISensorBackend` (read), `IFanController` (control), `IConfigStore`.

**`LinFan.Hardware.{Linux,Windows,Mac}`** implement these abstractions per platform (see §6).

Rules:
- Knows **nothing** about Avalonia, IPC, or the GUI.
- No `#if <OS>` switches - platform differences live in `Hardware.*`.
- Lives **authoritatively in the daemon process**.

### View - `LinFan.App/Views`

- Avalonia `.axaml` + **minimal** code-behind (only pure UI concerns like focus, animations).
- Only presentation, layout, bindings. **No** domain/hardware logic.
- The `DataContext` is the corresponding controller.

### Controller - `LinFan.App/Controllers`

- Presentation logic: takes view commands, calls the model via the **IPC client**, holds the bound
  state (live RPM, temperatures, status, calibration progress).
- Mechanics via CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), conceptually an
  **MVC controller**. Naming: `XyzController`.
- Marshals incoming IPC updates onto the UI thread (`Dispatcher.UIThread`).
- Contains **no** hardware logic - delegates everything to the daemon.

---

## 2. Process separation & privileges

| Aspect        | GUI process (`LinFan.App`)     | Daemon process (`LinFan.Daemon`)         |
|---------------|--------------------------------|------------------------------------------|
| Rights        | user, **no** root rights       | **privileged** (root/admin/SYSTEM)       |
| Contains      | View + Controller + IPC client | Model (Core + Hardware) + IPC server     |
| Hardware      | **never** directly             | exclusively here                         |
| Start         | manually by the user           | systemd / Windows Service / LaunchDaemon |
| Crash outcome | daemon falls back to fail-safe | watchdog keeps safe fan settings         |

Rationale: writing PWM needs elevated rights everywhere. Instead of running the whole GUI as root, the
slim daemon encapsulates the privileged part; the large UI codebase stays unprivileged.

---

## 3. Data flow (one user action)

Example "user assigns a curve to a fan":

```
View (edit in the curve editor, then "Apply")
  → CurveEditorController.BuildConfig()
    → IpcClient sends saveConfig { config }                 ⟦process boundary⟧
      → Daemon: merges it into its own config (daemon-owned fields stay; a fan's minPwm only
        changes if the GUI changed it against the value the daemon reported - a calibration that
        landed meanwhile is kept), persists via ConfigStore, takes the change over at the next tick
        → from then on: CurveEngine drives the fan
      ← Daemon pushes a snapshot (state + config) every tick
    ← Controller receives the snapshot, Dispatcher.UIThread → sets ObservableProperty
  ← View updates itself automatically via binding
```

Key point: the view **never** calls the model directly; the controller **never** calls hardware
directly. Each stage only talks to its neighbor.

---

## 4. IPC contract - `LinFan.Ipc`

- Shared **DTOs/commands**, referenced by both app **and** daemon (single source of truth for the
  contract).
- **Transport:** a local Unix domain socket (Linux/macOS) or a named pipe (Windows). Serialization is
  NDJSON (line-delimited JSON), kept behind an interface (`IIpcClient` / `IIpcServer`) so it stays
  swappable.
- **Example commands** (`IpcCommand`): `saveConfig`, `setManualPwm`, `startCalibration`, `identify`,
  `setActiveProfile`, `acceptHardware`, `forgetHardware`, `reload`; the daemon pushes one snapshot (state +
  current config + its own version, so the GUI can name a service that runs another build) per poll tick. The config travels only when it
  changed: each snapshot numbers it (`ConfigRevision`), and a client that asked for it on connect
  (`configOnChange`, sent by `IpcClient`) gets it only with a revision it has not had - `IpcClient` puts the last one
  back in, so its readers always see a config. A snapshot it cannot read - not valid for its contract, over its line
  bound, or of a revision whose config it never received - comes out as `null`, never skipped: the GUI then shows the
  data as unreadable instead of the last snapshot as current. A client that does not ask (an older GUI) gets it every
  tick.
- On a Unix socket the server never takes over an endpoint another daemon holds (`IpcEndpointInUseException`, see §7;
  the Windows pipe instead retries until a squatted name is free), and it
  logs every connection with its peer (Linux: uid/gid/pid) and every config change, device action and the first manual
  PWM command with the connection that sent it - the members of the group all have the same authority.
- Only serializable DTOs cross the boundary - **never** pass Core service objects through.

---

## 5. Threading

- **Daemon:** one poll loop (~1 s, configurable) reads sensors, applies curves, writes PWM, checks the
  watchdog. Hardware calls run here, never blocking in a UI context.
- **GUI:** everything on the UI thread; incoming IPC updates are marshaled via
  `Dispatcher.UIThread.Post(...)`.
- No blocking hardware/IPC call on the UI thread.

---

## 6. Hardware backend contract

```csharp
public interface ISensorBackend : IDisposable
{
    IReadOnlyList<SensorDescriptor> DiscoverSensors(); // find sensors (temperatures, tachometers)
    double ReadValue(SensorId id);                     // RPM / °C
}

public interface IFanController : IDisposable
{
    IReadOnlyList<FanDescriptor> DiscoverFans();       // find PWM channels
    bool    CanControl(FanId id);                      // controllable? (otherwise read-only)
    FanMode GetMode(FanId id);
    void    SetMode(FanId id, FanMode mode);           // Manual / Auto
    byte    GetPwm(FanId id);
    void    SetPwm(FanId id, byte value);              // 0..255
    void    RestoreDefaults();                         // fail-safe / on shutdown
}
```

- **Linux:** reads/writes `sysfs` (`/sys/class/hwmon/...`); `pwmN_enable=1` for manual, `=2/5` for
  hardware auto. `thinkpad_acpi` loaded without `fan_control=1` refuses every write, root's included: its channel
  is read-only, with a startup warning.
- **Windows:** `LibreHardwareMonitorLib` (`Control.SetSoftware(percent)`).
- **macOS:** IOKit/SMC (`F*Tg`/`F*Md`); controllable as root (Intel and Apple Silicon alike) where the channel has
  target and mode keys - a channel without them is read-only (`NoControlInterface`), root or not.
- "Not controllable" is a **regular state**, not an error - the UI shows such channels as read-only, with the
  reason the backend gives in `FanDescriptor.ReadOnlyReason` (`null` = controllable or unknown).
- `Dispose` restores every channel like `RestoreDefaults()`, but only once the instance has written one
  (`SetPwm`, `SetMode` to manual): a read-only process - the CLI's `list` as root - leaves a running daemon's fans
  alone. After `Dispose` a manual write throws `ObjectDisposedException`; callers serialize writes and `Dispose`.
- A manual write that fails part-way (manual mode set, value refused) hands the channel back like `RestoreDefaults()`
  before it throws - otherwise it would stay manual on its old value with nothing to undo it.
- `GetMode` is for display only. A mode that cannot be read right now throws `IOException` (the snapshot shows
  `Unknown`, the CLI `?`) - never a guessed `Auto`; a channel without a control mode reports `Auto`.
- Each descriptor names the device behind it (`Device`: a name and a fingerprint): on Linux the chip name and, through
  the hwmon `device` link, the PCI ids (`pci:vendor:device:subsystem-vendor:subsystem-device`, for a class device such
  as `nvme0` or a Wi-Fi `phy0` those of its parent) or an NVMe model (`nvme:<model>`); on Windows LHM's hardware name;
  on macOS `SMC`. It is read once at construction, best-effort: whatever cannot be read leaves the fingerprint `null`,
  and reading it never throws and never changes ids, names or `CanControl` - the same scan serves the CLI `restore`
  behind `ExecStopPost=`. It names the model, not the unit, and never carries a serial number.

---

## 7. Fail-safe (daemon)

- The **watchdog** checks temperature limits and the connection to the GUI every tick.
- Trigger → **safe state**: fans to 100 % or `RestoreDefaults()` (hardware auto mode).
- Triggers are: over-temperature, no readable temperature for a few ticks ("blind" - also when a sensor that last
  read near the limit turns unreadable, until it reads again), an internal daemon error, optionally GUI disconnect.
- A clean shutdown calls `RestoreDefaults()` → hardware auto restored. SIGTERM, SIGINT, SIGQUIT and SIGHUP all stop the
  daemon that way, also one started by hand or detached (the host's systemd lifetime alone would handle only SIGTERM). On
  Windows closing the console window (CTRL_CLOSE, reported as SIGHUP) ends the process the moment the handler returns, at the
  latest after 5 s, so the handlers of a hand-started `run` and of the CLI wait up to 4 s for the shutdown - restore and
  marker removal included - to finish (`ConsoleCloseHold`); the restore itself never runs on the handler thread. Under systemd the unit's `ExecStopPost=`
  runs the CLI `restore` after every stop, a crash or kill included; the daemon reports `WATCHDOG=1` after every
  tick whose watchdog ran, so `WatchdogSec=` restarts a daemon whose loop hangs.
- A daemon whose Unix socket another daemon holds touches no fan either, not even the start restore, and exits with
  code 3: the fans are the other's. It claims the endpoint before anything else for that reason - an exclusive lock
  next to the socket (Linux), then a probe that must receive a snapshot. If the other daemon holds the endpoint but
  sends nothing (stopped, stuck), the fans go to hardware auto first. A root daemon always uses the system socket
  (`XDG_RUNTIME_DIR` does not count for root), so two root daemons cannot miss each other. The units skip their
  `ExecStopPost=` restore and the restart for exit code 3 and keep `/run/linfan` on stop. Restores of a daemon wait until
  the endpoint is settled as its own. On Windows the pipe's first instance is taken in `Listen`; if the name is taken, it is retried for 3 s (a restart can overlap the old process's shutdown), then a probe client (cancellable with the host's token) checks the pipe's owner (SYSTEM, administrators or this account) and waits for a snapshot - the same outcome as on a Unix socket. A holder with another owner is a squatter, not a daemon: it does not suppress the start restore, and the accept loop retries until the name is free.
- The CLI's `set`, `calibrate` and `init` refuse (exit 3) while a daemon holds any endpoint a client would try - serving
  or stuck, on Windows whoever owns the pipe - before any backend exists. Next to it `set`/`calibrate` would fight its
  loop, and both restore every fan when they end; `init` would replace the config the daemon owns. `auto` and `restore`
  stay: they only move fans toward the safe state (next to a daemon only until its next write).
- A **dry run** (no root) touches no fan at all: no restore, no calibration, identification or coupling - such an
  instance must not act on the fans of a live daemon. The GUI does not offer those (nor a manual fan test) in a dry
  run and names the reason, as it does while the daemon is offline or busy with another device action - and a
  calibration of a fan with no speed sensor to measure with (`IpcFan.Tachometer` empty or not detected), which the
  daemon refuses as well; with a daemon that reports no version (before 0.5) that is not known, and it stays offered.
- Each snapshot reports what the last tick really did: the fail-safe state as the watchdog saw it
  (`FailSafeState`: none / over-temperature / blind / error - the tick itself failed) and, per fan, who controlled it (`FanControlState`: curve,
  holding its last value because no curve source is readable, manual, calibration / identify / coupling and the
  fans those throttle, hardware auto, fail-safe, failed write, not controllable) and which speed sensor its RPM
  comes from (`IpcFan.Tachometer`). The GUI shows these as they are instead of inferring them from the config.
- It also says why a read-only fan is read-only (`FanReadOnlyReason`: no write access / driver lock / no control
  interface), and - until the next saved change or a reload - why the daemon runs without the configuration it found
  at start (`ConfigLoadProblem`: not loaded, the next change overwrites it / set aside, with the new path). An
  unusable file is moved to `config.json.invalid…` and the daemon starts fresh, fans on hardware auto.
- Two reports never change what is written. **Hardware changes** (`IpcSnapshot.HardwareChanges`): a device whose
  fingerprint differs from the one its entries recorded (replaced - in practice a graphics card, an NVMe drive, a Wi-Fi
  card), and entries whose hardware is not detected (missing; never reported in a dry run, which may see less). A
  replaced fan keeps running on its old calibration, start point and curve until the user decides: `acceptHardware`
  records the new device (optionally resetting the sensors' own names), `forgetHardware` drops the entries and hands a
  fan that is still present to hardware auto - refused while a calibration, identification or coupling runs on one of
  them. **Calibration plausibility** (`IpcFan.CalibrationMismatch`): a fan on its curve or a manual value whose speed
  at a steady PWM stays off what its calibration predicts for a minute - it stands although it should turn (also at
  its start point), runs slower or faster; and, reported at once whatever drives the fan, a calibration measured
  with another speed sensor than the fan uses now. Only this check can notice a case fan swapped on the same header; a fingerprint cannot. Both run in their own
  guard outside the tick and read nothing the snapshot has not read already: an error there costs the report, never
  the tick.

---

## 8. Dependency rules (forbidden)

- ❌ View → Model **directly** (always via the controller).
- ❌ `LinFan.Core` → Avalonia / IPC / a concrete hardware implementation.
- ❌ `LinFan.App` → `LinFan.Hardware.*` **directly** (only the daemon loads backends).
- ❌ `#if <OS>` in `Core` or `App` - platform **hardware** access belongs exclusively in `Hardware.*`.
  Pure platform *presentation or infrastructure* details with no domain meaning (window decoration, config
  paths, the IPC transport, the single-instance endpoint) may live in `App`/`Core` when they are selected by a **runtime** check
  (`OperatingSystem.IsWindows()` …), so the same binary stays correct everywhere. Moving them into
  `Hardware.*` would be worse: it would force an `App` → `Hardware.*` reference, which is forbidden above.
- ❌ Hardware/domain logic in view code-behind.
- ❌ Blocking hardware/IPC calls on the UI thread.

---

## 9. Project structure

```
linfan/
├─ src/
│  ├─ LinFan.Core/              # MODEL: domain, services, abstractions
│  │  ├─ Models/                #   Sensor, Fan, Curve, Profile, Config (POCOs)
│  │  ├─ Services/              #   CalibrationService, CurveEngine, ConfigStore, ControlLoop
│  │  └─ Abstractions/          #   ISensorBackend, IFanController, IConfigStore
│  ├─ LinFan.Hardware.Linux/    # MODEL/infra: sysfs/hwmon backend
│  ├─ LinFan.Hardware.Windows/  # MODEL/infra: LibreHardwareMonitorLib backend
│  ├─ LinFan.Hardware.Mac/      # MODEL/infra: IOKit/SMC backend
│  ├─ LinFan.Daemon/            # host: BackgroundService + IPC server (privileged)
│  ├─ LinFan.Ipc/               # IPC contracts (DTOs/commands), shared by app & daemon
│  └─ LinFan.App/               # VIEW + CONTROLLER (Avalonia GUI)
│     ├─ Views/                 #   VIEW: .axaml + minimal code-behind
│     ├─ Controllers/           #   CONTROLLER: presentation logic, DataContext
│     ├─ Controls/              #   reusable controls (e.g. the curve editor)
│     └─ Services/              #   IPC client to the daemon (ILiveMonitor) + app services
└─ tests/                       # Core, Hardware, Ipc, Daemon, App + a hardware conformance suite
```

---

## 10. Data model (sketch)

```
Sensor      { Id, Name(custom), Group(name, case-insensitive), Source(hwmon path|SMC key|chip+idx),
              Type(temp|fan), Unit, currentValue, device(daemon-owned) }
Fan         { Id, Name(custom), PwmSource, RpmSource(own coupling → wins over the backend's tach pairing),
              minPwm, maxPwm(≥ calibrated start point; without one 0 = no cap),
              calibration(start point, RPM range, samples PWM→RPM, tachometer), assignedCurveId, location,
              device(daemon-owned) }
Curve       { Id, Name, sourceSensorId, points[(temp,percent)],
              hysteresis, smoothingSeconds, minClamp, maxClamp, interpolation(linear|spline) }
Profile     { Id, Name, fan→curve assignments }   // optional, multiple setups
Config      { schemaVersion, sensors[], sensorGroups[], fans[], caseType(tower|server|open|laptop), curves[], profiles[],
              pollIntervalMs, failSafeTemp }
```

One speed sensor, one fan (`FanTachometers`): a sensor a fan is coupled to (`RpmSource`) is no other fan's pairing - that
fan then has none, and calibration refuses it rather than measure another fan. The sanitizer keeps a sensor coupled to
several fans with the first; a measured coupling takes the sensor over, one assigned by hand is refused. Calibration
measures only with a detected sensor, after letting the fan run out at PWM 0.

`Name(custom)` is the user's own name; empty means the hardware label applies (the snapshot carries it next to the
resolved name, `IpcSensor.Label`). The daemon clears a name that is just the current label (`OwnNames`) - saved as a
name, a label stays when the hardware behind the id changes. The GUI saves a fan without an own name as "Fan N".
A save keeps the entries of sensors and fans the GUI does not send (not detected right now); apart from an import or a
reset, only `forgetHardware` removes them.

`device` records the hardware an entry was set up with; it never crosses IPC. The daemon fills it in for a detected
entry without one (the first start after an update, a new entry) and when a calibration that found a start point
measured the fan. An entry whose recorded fingerprint differs from the current one stays as it is - that is the
replaced device the snapshot reports. A calibration keeps its samples and the speed sensor it measured with
(`tachometer`); both travel with an export, and the plausibility check compares against them.

A fan a profile does not list has no curve in that profile - the editor shows it so and writes the entry down with the
next save; entries for fans not detected right now stay as they are.

`caseType` decides which case positions a fan can have (a server: front, rear and its fan wall; a laptop: every edge but
the front, and of the components only CPU and GPU; an open bench: none) and whether the airflow analysis draws a
pressure balance (not on an open bench or a laptop) - display and analysis only, the control loop never reads it
or a fan's `location`. A fan at a position its case type lacks keeps its role curve and stays out of the balance.

`sensorGroups[]` orders the sensor groups (display only) and may list groups no sensor is in. A group's identity is
its name - the one `Sensor.Group` carries, compared case-insensitively - so a group that sensors use but the list
lacks still exists.

Storage location (one path, OS-conforming via `SpecialFolder.ApplicationData`):
`Linux/macOS ~/.config/linfan/`, `Windows %AppData%\LinFan\` (machine-wide `%ProgramData%\linfan\` for
the service). Deliberately **no** macOS-specific path (`~/Library/Application Support`) - macOS is
deferred for now to avoid maintaining a second path branch.
