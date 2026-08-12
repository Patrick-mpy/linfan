# LinFan — Architecture (detail)

Complements [README.md](../README.md) with the binding architecture details.
Pattern: **MVC** with process separation (privileged daemon ⟷ user GUI).
The **IPC boundary equals the Controller↔Model boundary**.

---

## 1. Layers & responsibilities

### Model — `LinFan.Core` + `LinFan.Hardware.*`

Platform-neutral domain in **`LinFan.Core`**:

- **Models/** — POCOs without behavior/dependencies: `Sensor`, `Fan`, `Curve`, `Profile`, `Config`.
- **Services/** — the actual logic:
  - `CurveEngine` — computes temperature → PWM (interpolation, hysteresis, clamping).
  - `CalibrationService` — the onboarding flow (ramp PWM, measure RPM, start-up point).
  - `ConfigStore` — JSON persistence (load/save, schema versioning).
  - `ControlLoop` — poll cycle: read sensors → apply curves → set PWM.
- **Abstractions/** — `ISensorBackend` (read), `IFanController` (control), `IConfigStore`.

**`LinFan.Hardware.{Linux,Windows,Mac}`** implement these abstractions per platform (see §6).

Rules:
- Knows **nothing** about Avalonia, IPC, or the GUI.
- No `#if <OS>` switches — platform differences live in `Hardware.*`.
- Lives **authoritatively in the daemon process**.

### View — `LinFan.App/Views`

- Avalonia `.axaml` + **minimal** code-behind (only pure UI concerns like focus, animations).
- Only presentation, layout, bindings. **No** domain/hardware logic.
- The `DataContext` is the corresponding controller.

### Controller — `LinFan.App/Controllers`

- Presentation logic: takes view commands, calls the model via the **IPC client**, holds the bound
  state (live RPM, temperatures, status, calibration progress).
- Mechanics via CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), conceptually an
  **MVC controller**. Naming: `XyzController`.
- Marshals incoming IPC updates onto the UI thread (`Dispatcher.UIThread`).
- Contains **no** hardware logic — delegates everything to the daemon.

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

> **Prototype exception:** in phase 0/1 the controller may call the model in-process (app with elevated
> rights) to skip IPC at first. Still introduce the IPC client's interface from the start, so the switch
> stays local later.

---

## 3. Data flow (one user action)

Example "user assigns a curve to a fan":

```
View (Click)
  → Controller.AssignCurveCommand(fanId, curveId)
    → IpcClient.Send(AssignCurve { fanId, curveId })       ⟦process boundary⟧
      → Daemon: ControlLoop/Model takes the assignment, persists via ConfigStore
        → from the next poll tick: CurveEngine drives the fan
      ← Daemon pushes SensorUpdate/StateUpdate (stream)
    ← Controller receives update, Dispatcher.UIThread → sets ObservableProperty
  ← View updates itself automatically via binding
```

Key point: the view **never** calls the model directly; the controller **never** calls hardware
directly. Each stage only talks to its neighbor.

---

## 4. IPC contract — `LinFan.Ipc`

- Shared **DTOs/commands**, referenced by both app **and** daemon (single source of truth for the
  contract).
- **Transport:** a local Unix domain socket (Linux/macOS) or a named pipe (Windows). Serialization is
  NDJSON (line-delimited JSON), kept behind an interface (`IIpcClient` / `IIpcServer`) so it stays
  swappable.
- **Example commands:** `ListDevices`, `StartCalibration`, `AssignCurve`, `SetManualPwm`, `SaveConfig`,
  `Subscribe(stream SensorUpdate)`.
- Only serializable DTOs cross the boundary — **never** pass Core service objects through.

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
public interface ISensorBackend
{
    IReadOnlyList<SensorDescriptor> Discover();      // find sensors/fans
    double ReadValue(SensorId id);                   // RPM / °C
}

public interface IFanController
{
    bool   CanControl(FanId id);                     // controllable? (otherwise read-only)
    void   SetMode(FanId id, FanMode mode);          // Manual / Auto
    void   SetPwm(FanId id, byte value);             // 0..255
    void   RestoreDefaults();                         // fail-safe / on shutdown
}
```

- **Linux:** reads/writes `sysfs` (`/sys/class/hwmon/...`); `pwmN_enable=1` for manual, `=2/5` for
  hardware auto.
- **Windows:** `LibreHardwareMonitorLib` (`Control.SetSoftware(percent)`).
- **macOS:** IOKit/SMC (`F*Tg`/`F*Md`); `CanControl` often `false` on Apple Silicon.
- "Not controllable" is a **regular state**, not an error — the UI shows such channels as read-only.

---

## 7. Fail-safe (daemon)

- The **watchdog** checks temperature limits and the connection to the GUI every tick.
- Trigger → **safe state**: fans to 100 % or `RestoreDefaults()` (hardware auto mode).
- Triggers are: over-temperature, an internal daemon error, optionally GUI disconnect.
- A clean shutdown calls `RestoreDefaults()` → hardware auto restored.

---

## 8. Dependency rules (forbidden)

- ❌ View → Model **directly** (always via the controller).
- ❌ `LinFan.Core` → Avalonia / IPC / a concrete hardware implementation.
- ❌ `LinFan.App` → `LinFan.Hardware.*` **directly** (only the daemon loads backends).
- ❌ `#if <OS>` in `Core` or `App` — platform **hardware** access belongs exclusively in `Hardware.*`.
  Pure platform *presentation or infrastructure* details with no domain meaning (window decoration, config
  paths, the IPC transport) may live in `App`/`Core` when they are selected by a **runtime** check
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
│     └─ Ipc/                   #   IPC client to the daemon
└─ tests/                       # Core, Hardware, Ipc, Daemon, App + a hardware conformance suite
```

---

## 10. Data model (sketch)

```
Sensor      { Id, Name(custom), Source(hwmon path|SMC key|chip+idx),
              Type(temp|fan), Unit, currentValue }
Fan         { Id, Name(custom), PwmSource, RpmSource(override → wins over backend tach heuristic),
              minPwm, maxPwm, calibration(PWM→RPM), assignedCurveId }
Curve       { Id, Name, sourceSensorId, points[(temp,percent)],
              hysteresis, minClamp, maxClamp, interpolation(linear|spline) }
Profile     { Id, Name, fan→curve assignments }   // optional, multiple setups
Config      { schemaVersion, sensors[], fans[], curves[], profiles[],
              pollIntervalMs, failSafeTemp }
```

Storage location (one path, OS-conforming via `SpecialFolder.ApplicationData`):
`Linux/macOS ~/.config/linfan/`, `Windows %AppData%\LinFan\` (machine-wide `%ProgramData%\linfan\` for
the service). Deliberately **no** macOS-specific path (`~/Library/Application Support`) — macOS is
deferred for now to avoid maintaining a second path branch.
