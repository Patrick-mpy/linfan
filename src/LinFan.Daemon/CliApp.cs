// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc;
using LinFan.Ipc.Messages;
using LinFan.Ipc.Transport;

namespace LinFan.Daemon;

/// <summary>
/// Phase-0-Kommandozeile. Beweist den Hardware-Kern auf Linux: Sensoren auflisten, Drehzahlen
/// live anzeigen, einen Lüfter manuell setzen (mit Temperatur-Watchdog) und auf Auto zurückstellen.
/// Vollwertiger Hintergrunddienst + IPC folgen in Phase 1.
/// </summary>
internal static class CliApp
{
    private const double SafetyLimitC = 90.0; // Fail-Safe-Obergrenze für manuelle Eingriffe

    /// <summary>
    /// So viele aufeinanderfolgende Zyklen ohne lesbare Temperatur beenden das manuelle Halten sicher
    /// (Fail-Safe): einen festen PWM ohne aktiven Watchdog zu halten ist unzulässig. Analog
    /// <c>ControlLoop.MaxBlindTicks</c> / <c>IdentifyCoordinator.MaxBlindGuards</c>.
    /// </summary>
    private const int MaxBlindHolds = 3;

    public static Task<int> RunAsync(string[] args) =>
        RunAsync(args, BackendFactory.Create, IpcEndpoint.ClientCandidates());

    /// <param name="args">The command line.</param>
    /// <param name="createBackends">The hardware backends - created only once the command may run (a test seam).</param>
    /// <param name="daemonEndpoints">Where a running daemon could be (see <see cref="RefusedNextToDaemon"/>).</param>
    internal static async Task<int> RunAsync(
        string[] args, Func<(ISensorBackend Sensors, IFanController Fans)> createBackends,
        IReadOnlyList<string> daemonEndpoints)
    {
        // Set on every way out, after the restore: a closing console window holds its signal handler until then (see
        // ConsoleCloseHold). Never disposed - the handler may still be waiting on it.
        var finished = new ManualResetEventSlim();
        try
        {
            return await RunCoreAsync(args, createBackends, daemonEndpoints, finished);
        }
        finally
        {
            finished.Set();
        }
    }

    private static async Task<int> RunCoreAsync(
        string[] args, Func<(ISensorBackend Sensors, IFanController Fans)> createBackends,
        IReadOnlyList<string> daemonEndpoints, ManualResetEventSlim finished)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // sauber beenden statt hartem Abbruch → finally läuft → Fail-Safe
            cts.Cancel();
        };
        // Ctrl+C is not the only way out: `kill` (SIGTERM), a closed terminal or dropped SSH session (SIGHUP) and Ctrl+\
        // (SIGQUIT) would end the process without the finally below - leaving a fan that `set`/`calibrate` drove on its
        // manual value. Every command watches the token or ends on its own.
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            cts.Cancel();
            // A closed console window (SIGHUP on Windows) ends the process as soon as this returns: wait for the restore.
            ConsoleCloseHold.Hold(context, finished);
        }
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop);
        using var sigquit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Stop);

        // IPC-Client-Befehle sprechen nur mit dem Daemon - kein eigenes Hardware-Backend laden.
        switch (command)
        {
            case "monitor-ipc": return await MonitorIpcAsync(cts.Token);
            case "reload": return await ReloadAsync(cts.Token);
            case "help" or "-h" or "--help": return Help();
        }

        // Before any backend exists: a refused command must not restore the fans of the daemon it stepped back for.
        if (RefusedNextToDaemon(command, daemonEndpoints))
            return 3;
        if (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Abgebrochen - nichts geschrieben."); // Ctrl+C while the probe waited
            return 1;
        }

        ISensorBackend sensors;
        IFanController fans;
        try
        {
            (sensors, fans) = createBackends();
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            // As in DaemonHost: an exit code, not an unhandled exception and its core dump - `restore` runs as the units'
            // ExecStopPost and must end cleanly when the backend cannot start either.
            Console.Error.WriteLine($"Hardware-Backend nicht startbar: {ex.Message}");
            return 1;
        }

        // E.g. another LinFan process of this boot wrote the fans (Windows): explains why they end on 100 %.
        if (sensors is IBackendDiagnostics diagnostics)
            diagnostics.RuntimeWarning += message => Console.Error.WriteLine($"Warnung: {message}");

        // Fail-Safe-Restore nur nach Kommandos, die PWM tatsächlich verändern. Ein pauschales
        // RestoreDefaults nach read-only-Kommandos (`list`/`monitor`/`init`) würfe ALLE Lüfter kurz auf
        // Hardware-Auto - neben einem laufenden Daemon riss das dessen Kurvenregelung für einen Moment weg.
        // The backend's Dispose below keeps to that too: it restores only if this process wrote a fan.
        bool touchesPwm = CommandTouchesPwm(command);
        try
        {
            return command switch
            {
                "list" => List(sensors, fans),
                "monitor" => await MonitorAsync(sensors, cts.Token),
                "init" => Init(sensors, fans),
                "calibrate" => await CalibrateAsync(fans, sensors, args, cts.Token),
                "set" => await SetAsync(fans, sensors, args, cts.Token),
                "auto" => Auto(fans, args),
                "restore" => Restore(fans),
                _ => Unknown(command),
            };
        }
        finally
        {
            if (touchesPwm)
                fans.RestoreDefaults();      // Fail-Safe: nach `set`/`calibrate` sicher auf Auto zurück
            (sensors as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Verändert das Kommando PWM (und braucht daher den Fail-Safe-Restore beim Verlassen)? Nur <c>set</c>
    /// (manueller PWM) und <c>calibrate</c> (Rampe) schreiben; <c>list</c>/<c>monitor</c>/<c>init</c> sind
    /// read-only und <c>auto</c> setzt genau einen Lüfter explizit auf Auto (kein Rückstellen aller nötig).
    /// </summary>
    internal static bool CommandTouchesPwm(string command) => command is "set" or "calibrate";

    /// <summary>
    /// Refuses <c>set</c>, <c>calibrate</c> and <c>init</c> while a daemon holds one of <paramref name="endpoints"/> -
    /// every endpoint a client would try, not only the one this process would bind: a <c>LINFAN_SOCKET</c> in the CLI's
    /// environment (<c>sudo -E</c>, <c>su</c> without <c>-</c>) or a CLI without root that may write the fans anyway must
    /// not miss the service. Next to the daemon <c>set</c>/<c>calibrate</c> would fight its loop for the fan, and both
    /// put every fan on hardware auto when they end. <c>init</c> would replace the config the daemon owns - on Windows
    /// the service's own file, whose curves are gone at its next start, unless its next save overwrites
    /// <c>init</c>'s first. <c>auto</c> and <c>restore</c> stay: they only move fans toward the safe state, and
    /// <c>restore</c> is the emergency brake.
    /// </summary>
    internal static bool RefusedNextToDaemon(string command, IEnumerable<string> endpoints)
    {
        const string stopTheService = "Erst den Dienst stoppen (Linux: systemctl stop linfan-daemon, Windows: Stop-Service LinFan)";
        string? why = command switch
        {
            "set" or "calibrate" => "würde gegen seine Regelung arbeiten und am Ende alle Lüfter auf Hardware-Auto stellen. "
                + stopTheService + " oder für einen Test den Manuell-Modus der GUI nutzen. 'auto' und 'restore' laufen "
                + "weiter, wirken neben dem Daemon aber nur bis zu seinem nächsten Write.",
            "init" => "schreibt eine neue Konfiguration - solange der Daemon läuft, ist er ihr alleiniger Schreiber. "
                + "Kurven und Zuordnungen in der GUI einrichten (Einstellungen › „Auf Werkszustand zurücksetzen“ startet "
                + "die Einrichtung neu). " + stopTheService + ".",
            _ => null,
        };
        if (why is null || endpoints.FirstOrDefault(IpcTransportFactory.IsEndpointHeld) is not { } held)
            return false;

        Console.Error.WriteLine($"Abbruch: an {held} läuft bereits ein LinFan-Daemon - '{command}' {why}");
        return true;
    }

    private static async Task<int> MonitorIpcAsync(CancellationToken ct)
    {
        await using var client = new IpcClient();
        try
        {
            await client.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Kein Daemon erreichbar ({string.Join(", ", IpcEndpoint.ClientCandidates())}): {ex.Message}");
            Console.Error.WriteLine("Zuerst in einem anderen Terminal 'run' starten (für Steuerung als Root).");
            return 2;
        }

        Console.WriteLine($"Verbunden mit {client.ConnectedPath} - Strg+C beendet.");
        try
        {
            await foreach (IpcSnapshot? snap in client.ReadSnapshotsAsync(ct))
            {
                if (snap is null)
                {
                    Console.WriteLine($"── {DateTime.Now:HH:mm:ss} · Snapshot nicht lesbar (andere Version des Dienstes?)");
                    continue;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"── {DateTime.Now:HH:mm:ss} · {snap.Status} · max {Fmt(snap.HottestTempC, "°C")}");
                foreach (IpcFan f in snap.Fans)
                {
                    string rpm = f.Rpm is { } r ? Fmt(r, "RPM") : "n/a";
                    // Names are peer-authored (any client may rename a fan): never let them steer this terminal.
                    sb.AppendLine($"  {UntrustedText.Escape(f.Name),-22} {rpm,-12} pwm {f.Pwm} ({f.Pwm * 100 / 255}%) · "
                                  + UntrustedText.Escape(f.Mode));
                }
                Console.Write(sb.ToString());
                Console.Out.Flush(); // sofort sichtbar, auch wenn per Signal beendet
            }
        }
        catch (OperationCanceledException)
        {
            // Strg+C
        }
        return 0;
    }

    private static async Task<int> ReloadAsync(CancellationToken ct)
    {
        await using var client = new IpcClient();
        try
        {
            await client.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Kein Daemon erreichbar ({string.Join(", ", IpcEndpoint.ClientCandidates())}): {ex.Message}");
            return 2;
        }

        await client.SendCommandAsync(new IpcCommand(IpcCommand.Reload), ct);
        Console.WriteLine("reload gesendet - der Daemon liest die Konfiguration neu ein.");
        return 0;
    }

    /// <summary>
    /// A channel's control column in <c>list</c>: the reason the backend gives for a read-only one, since root only helps
    /// a channel that lacks write access - not one without a control interface (an SMC channel without target and mode
    /// keys, for instance) or a driver lock.
    /// </summary>
    internal static string ControlText(FanDescriptor fan, bool elevated) => fan.CanControl ? "steuerbar" : fan.ReadOnlyReason switch
    {
        FanReadOnlyReason.NoWriteAccess when !elevated => $"read-only ({Privileges.ElevationTerm} nötig)",
        FanReadOnlyReason.NoWriteAccess => "read-only (kein Schreibzugriff)",
        FanReadOnlyReason.DriverLocked => "read-only (Treiber-Sperre)",
        FanReadOnlyReason.NoControlInterface => "read-only (keine Steuerung)",
        _ => "read-only",
    };

    private static int List(ISensorBackend sensors, IFanController fans)
    {
        Console.WriteLine($"LinFan Phase-0-Spike · {Runtime()}");
        Console.WriteLine();

        var all = sensors.DiscoverSensors();
        var temps = all.Where(s => s.Kind == SensorKind.Temperature).ToList();
        var rpms = all.Where(s => s.Kind == SensorKind.FanRpm).ToList();

        Console.WriteLine($"Temperatursensoren ({temps.Count}):");
        foreach (var s in temps)
            Console.WriteLine($"  {s.Name,-24} {Fmt(sensors.ReadValue(s.Id), "°C"),-12} [{s.Id}]");

        Console.WriteLine();
        Console.WriteLine($"Drehzahl-Sensoren ({rpms.Count}):");
        foreach (var s in rpms)
            Console.WriteLine($"  {s.Name,-24} {Fmt(sensors.ReadValue(s.Id), "RPM"),-12} [{s.Id}]");

        var fanList = fans.DiscoverFans();
        bool elevated = Privileges.IsElevated();
        Console.WriteLine();
        Console.WriteLine($"PWM-/Lüfter-Kanäle ({fanList.Count}):");
        foreach (var f in fanList)
        {
            byte pwm = fans.GetPwm(f.Id);
            Console.WriteLine(
                $"  {f.Name,-24} pwm={pwm,3} ({Percent(pwm),3}%) mode={ModeText(fans, f.Id),-6} {ControlText(f, elevated)}  [{f.Id}]");
        }

        if (!elevated)
        {
            Console.WriteLine();
            Console.WriteLine("Hinweis: Ohne Root sind nur Lesezugriffe möglich. Für 'set' mit sudo starten.");
        }

        if (sensors is IBackendDiagnostics { StartupWarning: { } warning })
        {
            Console.WriteLine();
            Console.WriteLine($"Warnung: {warning}");
        }
        return 0;
    }

    private static async Task<int> MonitorAsync(ISensorBackend sensors, CancellationToken ct)
    {
        var all = sensors.DiscoverSensors();
        var temps = all.Where(s => s.Kind == SensorKind.Temperature).ToList();
        var rpms = all.Where(s => s.Kind == SensorKind.FanRpm).ToList();

        Console.WriteLine("Live-Monitor - Strg+C beendet.");
        while (!ct.IsCancellationRequested)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"── {DateTime.Now:HH:mm:ss} ──────────────");
            foreach (var s in temps)
                sb.AppendLine($"  {s.Name,-24} {Fmt(sensors.ReadValue(s.Id), "°C")}");
            foreach (var s in rpms)
                sb.AppendLine($"  {s.Name,-24} {Fmt(sensors.ReadValue(s.Id), "RPM")}");
            Console.Write(sb.ToString());

            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    internal static async Task<int> SetAsync(
        IFanController fans, ISensorBackend sensors, string[] args, CancellationToken ct,
        Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;

        if (args.Length < 3 || !byte.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte pwm))
        {
            Console.Error.WriteLine("Aufruf: set <fanId> <0-255>");
            return 1;
        }

        var id = new FanId(args[1]);
        var fan = fans.DiscoverFans().FirstOrDefault(f => f.Id == id);
        if (fan is null)
        {
            Console.Error.WriteLine($"Unbekannter Lüfter: {id}. 'list' zeigt gültige IDs.");
            return 1;
        }

        if (!Privileges.IsElevated())
            Console.WriteLine($"Warnung: nicht als {Privileges.ElevationTerm} - Schreibzugriff schlägt voraussichtlich fehl.");

        // Fail-Safe vor dem Eingriff: bei Übertemperatur gar nicht erst manuell werden.
        double hottest = SensorAggregator.Hottest(sensors);
        if (!double.IsNaN(hottest) && hottest >= SafetyLimitC)
        {
            Console.Error.WriteLine(
                $"Abbruch: {hottest:0.0} °C ≥ {SafetyLimitC} °C - Fail-Safe, kein manueller Eingriff.");
            return 3;
        }

        try
        {
            fans.SetMode(id, FanMode.Manual);
            fans.SetPwm(id, pwm);
            Console.WriteLine($"{fan.Name}: pwm={pwm} ({Percent(pwm)}%) gesetzt (Manual).");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            Console.Error.WriteLine($"Schreibfehler: {ex.Message}");
            Console.Error.WriteLine(
                "ThinkPad: thinkpad_acpi mit 'fan_control=1' laden. Allgemein: als Root ausführen.");
            return 3;
        }

        // Wert halten, dabei Live-Drehzahl anzeigen und die Temperatur überwachen (Watchdog), bis Strg+C.
        Console.WriteLine("Halte Wert, überwache Drehzahl & Temperatur … (Strg+C beendet und stellt Auto wieder her)");
        int blindHolds = 0;
        while (!ct.IsCancellationRequested)
        {
            double t = SensorAggregator.Hottest(sensors);
            string rpm = "-";
            if (fan.Tachometer is { } tach)
            {
                // Defensiv wie SensorAggregator.ReadOrNaN: ein werfender Tacho-Kanal (EIO o. Ä.) darf den
                // Watchdog-Loop nicht abreißen - sonst greift der Blind-Hold-Abbruch bei gleichzeitig
                // kaputtem Tacho nie. Der Wert ist reine Anzeige; die Regelung hängt an `t` (bereits defensiv).
                try { rpm = Fmt(sensors.ReadValue(tach), "RPM"); }
                catch { rpm = Fmt(double.NaN, "RPM"); }
            }
            Console.WriteLine($"  pwm={pwm} · {rpm} · max {Fmt(t, "°C")}");

            if (!double.IsNaN(t) && t >= SafetyLimitC)
            {
                Console.Error.WriteLine($"Übertemperatur {t:0.0} °C - Fail-Safe: zurück auf Auto.");
                fans.RestoreDefaults();
                return 3;
            }

            // „Temperatur unbekannt" ist nicht „alles in Ordnung": nach einigen blinden Zyklen sicher auf
            // Auto, statt den festen PWM ohne Watchdog zu halten (analog ControlLoop/IdentifyCoordinator).
            if (double.IsNaN(t))
            {
                if (++blindHolds >= MaxBlindHolds)
                {
                    Console.Error.WriteLine(
                        $"Keine lesbare Temperatur seit {blindHolds} Zyklen - Fail-Safe: zurück auf Auto.");
                    fans.RestoreDefaults();
                    return 3;
                }
            }
            else
            {
                blindHolds = 0;
            }

            try { await delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    private static int Auto(IFanController fans, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Aufruf: auto <fanId>");
            return 1;
        }

        var id = new FanId(args[1]);
        try
        {
            fans.SetMode(id, FanMode.Auto);
            Console.WriteLine($"{id}: Hardware-Auto-Modus gesetzt.");
            return 0;
        }
        catch (KeyNotFoundException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            // `auto` is what one reaches for after a crashed `set`: the fan may still sit at a low manual value. If it
            // cannot be put on auto, every channel goes to its safe state (full speed where auto fails), as `restore` does.
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            if (!fans.DiscoverFans().Any(f => f.CanControl))
                return 1; // nothing this process could write (no root)
            fans.RestoreDefaults();
            foreach (string problem in (fans as IRestoreDiagnostics)?.LastRestoreProblems ?? [])
                Console.Error.WriteLine(problem);
            Console.Error.WriteLine("Stattdessen alle Lüfter-Kanäle in den sicheren Zustand gesetzt (wie 'restore').");
            return 1;
        }
    }

    /// <summary>
    /// Puts every fan on hardware auto (full speed where a channel has none). The systemd unit runs it as
    /// <c>ExecStopPost=</c>: it also reaches the fans after a SIGKILL, an OOM kill or a watchdog kill, when the daemon's
    /// own restore never ran. Fails (exit 1) if a channel could not be put in its safe state.
    /// </summary>
    private static int Restore(IFanController fans)
    {
        // Without the rights every channel reads as not controllable and the restore would skip them all - that must
        // not pass for success.
        if (!Privileges.IsElevated())
        {
            Console.Error.WriteLine($"restore braucht {Privileges.ElevationTerm}-Rechte - nichts zurückgesetzt.");
            return 1;
        }
        // Also without a controllable channel: the restore still reports a read-only fan stuck on a manual level.
        int controllable = fans.DiscoverFans().Count(f => f.CanControl);
        fans.RestoreDefaults();
        IReadOnlyList<string> problems = (fans as IRestoreDiagnostics)?.LastRestoreProblems ?? [];
        foreach (string problem in problems)
            Console.Error.WriteLine(problem);
        if (problems.Count > 0)
            return 1;
        Console.WriteLine(controllable == 0
            ? "Keine steuerbaren Lüfter-Kanäle - nichts zurückzusetzen."
            : $"{controllable} Lüfter-Kanal/Kanäle auf Hardware-Auto zurückgesetzt.");
        return 0;
    }

    private static int Init(ISensorBackend sensors, IFanController fans)
    {
        var temps = sensors.DiscoverSensors().Where(s => s.Kind == SensorKind.Temperature).ToList();
        if (temps.Count == 0)
        {
            Console.Error.WriteLine("Keine Temperatursensoren gefunden - Abbruch.");
            return 1;
        }

        SensorDescriptor source = PickCpuTempSensor(sensors, temps);
        var curve = new CurveConfig
        {
            Id = "balanced",
            Name = "Balanced",
            SourceSensorIds = new[] { source.Id.Value },
            HysteresisC = 2.0,
            Points = new[]
            {
                new CurvePoint(30, 20),
                new CurvePoint(45, 30),
                new CurvePoint(60, 55),
                new CurvePoint(75, 80),
                new CurvePoint(85, 100),
            },
        };

        // No names, for fans and sensors alike: Name is the user's own, empty means none and the hardware label applies.
        // Writing the label in would freeze it as an own name - it would stay when the hardware behind the id changes.
        var discovered = fans.DiscoverFans();
        var fanConfigs = discovered
            .Select(f => new FanConfig { FanId = f.Id.Value, AssignedCurveId = curve.Id })
            .ToList();

        var config = new AppConfig
        {
            Sensors = temps.Select(t => new SensorConfig { SensorId = t.Id.Value }).ToList(),
            Fans = fanConfigs,
            Curves = new[] { curve },
        };

        var store = new JsonConfigStore();
        store.Save(config);

        Console.WriteLine($"Start-Konfiguration geschrieben: {store.ConfigPath}");
        Console.WriteLine($"  Kurve 'Balanced' ← Quelle: {source.Name} [{source.Id}]");
        Console.WriteLine($"  {fanConfigs.Count} Lüfter zugeordnet:");
        foreach (FanDescriptor f in discovered)
            Console.WriteLine($"    {f.Name}  [{f.Id.Value}]");
        Console.WriteLine();
        Console.WriteLine("Weiter mit:  dotnet run --project src/LinFan.Daemon -- run");
        return 0;
    }

    private static SensorDescriptor PickCpuTempSensor(ISensorBackend sensors, IReadOnlyList<SensorDescriptor> temps)
    {
        string[] preferred = { "tctl", "cpu", "package", "k10temp", "coretemp" };
        var readable = temps.Where(t => !double.IsNaN(sensors.ReadValue(t.Id))).ToList();
        var pool = readable.Count > 0 ? readable : temps;

        foreach (string key in preferred)
        {
            var hit = pool.FirstOrDefault(t => t.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return hit;
        }
        return pool[0];
    }

    private static async Task<int> CalibrateAsync(
        IFanController fans, ISensorBackend sensors, string[] args, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Aufruf: calibrate <fanId>   (IDs siehe 'list')");
            return 1;
        }

        var id = new FanId(args[1]);
        var fan = fans.DiscoverFans().FirstOrDefault(f => f.Id == id);
        if (fan is null)
        {
            Console.Error.WriteLine($"Unbekannter Lüfter: {id}. 'list' zeigt gültige IDs.");
            return 1;
        }

        if (!Privileges.IsElevated())
            Console.WriteLine($"Warnung: nicht als {Privileges.ElevationTerm} - Kalibrierung (PWM schreiben) schlägt voraussichtlich fehl.");

        // Config früh laden: liefert die Watchdog-Obergrenze und wird unten für die Ergebnis-Persistenz wiederverwendet.
        var store = new JsonConfigStore();
        AppConfig config = store.Load();

        // Anders als der Daemon hat der CLI-Pfad keinen zweiten Loop-Watchdog - die Obergrenze daher hier
        // defensiv durch denselben Sanitizer klemmen (handeditierte 200 °C / NaN dürfen den Watchdog nicht entschärfen).
        AppConfig sanitized = ConfigSanitizer.Sanitize(config, out _);
        // The couplings too: the same speed sensor the daemon would measure with (one sensor, one fan).
        var options = new CalibrationOptions { FailSafeTempC = sanitized.FailSafeTempC, Couplings = sanitized.Fans };
        Console.WriteLine(
            $"Kalibriere {fan.Name} … erst Auslauf bei pwm=0 (bis {options.RunOutTime.TotalSeconds:0}s), dann Rampe in " +
            $"{options.StepSize}-Schritten, {options.SettleTime.TotalSeconds:0}s Settle pro Stufe (Strg+C bricht ab).");

        FanCalibration result;
        try
        {
            result = await new CalibrationService(sensors, fans).CalibrateAsync(id, options, ct);
        }
        catch (OverTemperatureException ex)
        {
            Console.Error.WriteLine($"Abgebrochen: {ex.Message}");
            return 3;
        }
        catch (Exception ex) when (ex is NotSupportedException or FanNotControllableException
            or NoTachometerException or UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"Kalibrierung fehlgeschlagen: {ex.Message}");
            Console.Error.WriteLine("ThinkPad: thinkpad_acpi mit 'fan_control=1' laden; generell als Root ausführen.");
            return 3;
        }

        Console.WriteLine($"Ergebnis: Anlauf bei pwm={result.StartPwm}, Drehzahl {result.MinRpm}-{result.MaxRpm} RPM");

        // Anlaufpunkt + Messreihe in die (oben geladene) Konfiguration übernehmen - über denselben
        // Fail-Safe-Pfad wie der Daemon: MinPwm wird nur bei echtem Anlaufpunkt (MinRpm > 0) gesetzt,
        // sonst würde StartPwm==255 („nicht angelaufen") den Lüfter dauerhaft auf Volllast zwingen.
        // Ist der Lüfter noch nicht konfiguriert, vorher anlegen - ohne Namen, wie ConfigMapper.NewFan:
        // leer heißt „kein eigener Name", die Hardware-Bezeichnung gilt.
        AppConfig seeded = config.Fans.Any(f => f.FanId == id.Value)
            ? config
            : config with { Fans = config.Fans.Append(new FanConfig { FanId = id.Value }).ToList() };
        store.Save(ConfigMapper.ApplyCalibration(seeded, id.Value, result, fan.Device));

        Console.WriteLine(result.MinRpm > 0
            ? $"In {store.ConfigPath} gespeichert (MinPwm = {result.StartPwm})."
            : $"In {store.ConfigPath} gespeichert (kein Anlaufpunkt gefunden - MinPwm unverändert).");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine(
            """
            LinFan - Daemon-CLI (Linux hwmon)

            Befehle:
              list                 Sensoren & Lüfter anzeigen (Standard)
              monitor              Live-Anzeige von Temperaturen & Drehzahlen (Strg+C beendet)
              init                 Start-Konfiguration aus der Hardware erzeugen (Kurve + Zuordnung; nicht neben 'run')
              run                  Regel-Loop + IPC-Server im Dauerbetrieb (ohne Root Dry-Run; Strg+C)
              calibrate <fanId>    Lüfter kalibrieren: Anlaufpunkt & Drehzahlbereich (Root; nicht neben 'run')
              set <fanId> <0-255>  Lüfter manuell setzen (Root, mit Temperatur-Watchdog; nicht neben 'run')
              auto <fanId>         Lüfter zurück in den Hardware-Auto-Modus
              restore              alle Lüfter zurück in den Hardware-Auto-Modus (Root)

            IPC-Client (spricht mit einem laufenden 'run'):
              monitor-ipc          Live-Snapshots vom Daemon anzeigen (Strg+C beendet)
              reload               Daemon die Konfiguration neu einlesen lassen
              help                 diese Hilfe

            Gültige IDs stehen in der Spalte [..] der 'list'-Ausgabe.
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unbekannter Befehl: {command}. 'help' zeigt die Befehle.");
        return 1;
    }

    // --- Helfer ---------------------------------------------------------------

    private static string Fmt(double v, string unit) =>
        double.IsNaN(v) ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{v:0.0} {unit}");

    private static int Percent(byte pwm) => pwm * 100 / 255;

    /// <summary>The fan's mode, or <c>?</c> when the backend cannot read it right now.</summary>
    private static string ModeText(IFanController fans, FanId id)
    {
        try
        {
            return fans.GetMode(id).ToString();
        }
        catch (IOException)
        {
            return "?";
        }
    }

    private static string Runtime() =>
        $"{RuntimeInformation.OSDescription.Trim()} · {(Privileges.IsElevated() ? Privileges.ElevationTerm.ToLowerInvariant() : "user")}";
}
