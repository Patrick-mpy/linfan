// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc;
using LinFan.Ipc.Messages;

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

    public static async Task<int> RunAsync(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // sauber beenden statt hartem Abbruch → finally läuft → Fail-Safe
            cts.Cancel();
        };

        // IPC-Client-Befehle sprechen nur mit dem Daemon — kein eigenes Hardware-Backend laden.
        switch (command)
        {
            case "monitor-ipc": return await MonitorIpcAsync(cts.Token);
            case "reload": return await ReloadAsync(cts.Token);
            case "help" or "-h" or "--help": return Help();
        }

        ISensorBackend sensors;
        IFanController fans;
        try
        {
            (sensors, fans) = BackendFactory.Create();
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        // Fail-Safe-Restore nur nach Kommandos, die PWM tatsächlich verändern. Ein pauschales
        // RestoreDefaults nach read-only-Kommandos (`list`/`monitor`/`init`) würfe ALLE Lüfter kurz auf
        // Hardware-Auto — neben einem laufenden Daemon riss das dessen Kurvenregelung für einen Moment weg.
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

        Console.WriteLine($"Verbunden mit {client.ConnectedPath} — Strg+C beendet.");
        try
        {
            await foreach (IpcSnapshot snap in client.ReadSnapshotsAsync(ct))
            {
                var sb = new StringBuilder();
                sb.AppendLine($"── {DateTime.Now:HH:mm:ss} · {snap.Status} · max {Fmt(snap.HottestTempC, "°C")}");
                foreach (IpcFan f in snap.Fans)
                {
                    string rpm = f.Rpm is { } r ? Fmt(r, "RPM") : "n/a";
                    sb.AppendLine($"  {f.Name,-22} {rpm,-12} pwm {f.Pwm} ({f.Pwm * 100 / 255}%) · {f.Mode}");
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
        Console.WriteLine("reload gesendet — der Daemon liest die Konfiguration neu ein.");
        return 0;
    }

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
        Console.WriteLine();
        Console.WriteLine($"PWM-/Lüfter-Kanäle ({fanList.Count}):");
        foreach (var f in fanList)
        {
            byte pwm = fans.GetPwm(f.Id);
            string control = f.CanControl ? "steuerbar" : "read-only (Root nötig)";
            Console.WriteLine(
                $"  {f.Name,-24} pwm={pwm,3} ({Percent(pwm),3}%) mode={fans.GetMode(f.Id),-6} {control}  [{f.Id}]");
        }

        if (!Privileges.IsElevated())
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

        Console.WriteLine("Live-Monitor — Strg+C beendet.");
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
            Console.WriteLine($"Warnung: nicht als {Privileges.ElevationTerm} — Schreibzugriff schlägt voraussichtlich fehl.");

        // Fail-Safe vor dem Eingriff: bei Übertemperatur gar nicht erst manuell werden.
        double hottest = SensorAggregator.Hottest(sensors);
        if (!double.IsNaN(hottest) && hottest >= SafetyLimitC)
        {
            Console.Error.WriteLine(
                $"Abbruch: {hottest:0.0} °C ≥ {SafetyLimitC} °C — Fail-Safe, kein manueller Eingriff.");
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
            string rpm = "—";
            if (fan.Tachometer is { } tach)
            {
                // Defensiv wie SensorAggregator.ReadOrNaN: ein werfender Tacho-Kanal (EIO o. Ä.) darf den
                // Watchdog-Loop nicht abreißen — sonst greift der Blind-Hold-Abbruch bei gleichzeitig
                // kaputtem Tacho nie. Der Wert ist reine Anzeige; die Regelung hängt an `t` (bereits defensiv).
                try { rpm = Fmt(sensors.ReadValue(tach), "RPM"); }
                catch { rpm = Fmt(double.NaN, "RPM"); }
            }
            Console.WriteLine($"  pwm={pwm} · {rpm} · max {Fmt(t, "°C")}");

            if (!double.IsNaN(t) && t >= SafetyLimitC)
            {
                Console.Error.WriteLine($"Übertemperatur {t:0.0} °C — Fail-Safe: zurück auf Auto.");
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
                        $"Keine lesbare Temperatur seit {blindHolds} Zyklen — Fail-Safe: zurück auf Auto.");
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 1;
        }
    }

    private static int Init(ISensorBackend sensors, IFanController fans)
    {
        var temps = sensors.DiscoverSensors().Where(s => s.Kind == SensorKind.Temperature).ToList();
        if (temps.Count == 0)
        {
            Console.Error.WriteLine("Keine Temperatursensoren gefunden — Abbruch.");
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

        // Ohne Namen anlegen: FanConfig.Name ist der EIGENE Name des Nutzers, leer heißt „keiner" — dann
        // greift überall die Hardware-Bezeichnung. Sie hier einzutragen fröre sie als eigenen Namen ein.
        var fanConfigs = fans.DiscoverFans()
            .Select(f => new FanConfig { FanId = f.Id.Value, AssignedCurveId = curve.Id })
            .ToList();

        var config = new AppConfig
        {
            Sensors = temps.Select(t => new SensorConfig { SensorId = t.Id.Value, Name = t.Name }).ToList(),
            Fans = fanConfigs,
            Curves = new[] { curve },
        };

        var store = new JsonConfigStore();
        store.Save(config);

        Console.WriteLine($"Start-Konfiguration geschrieben: {store.ConfigPath}");
        Console.WriteLine($"  Kurve 'Balanced' ← Quelle: {source.Name} [{source.Id}]");
        Console.WriteLine($"  {fanConfigs.Count} Lüfter zugeordnet:");
        foreach (var f in fanConfigs)
            Console.WriteLine($"    {f.Name}  [{f.FanId}]");
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
            Console.WriteLine($"Warnung: nicht als {Privileges.ElevationTerm} — Kalibrierung (PWM schreiben) schlägt voraussichtlich fehl.");

        // Config früh laden: liefert die Watchdog-Obergrenze und wird unten für die Ergebnis-Persistenz wiederverwendet.
        var store = new JsonConfigStore();
        AppConfig config = store.Load();

        // Anders als der Daemon hat der CLI-Pfad keinen zweiten Loop-Watchdog — die Obergrenze daher hier
        // defensiv durch denselben Sanitizer klemmen (handeditierte 200 °C / NaN dürfen den Watchdog nicht entschärfen).
        double failSafeTempC = ConfigSanitizer.Sanitize(config, out _).FailSafeTempC;
        var options = new CalibrationOptions { FailSafeTempC = failSafeTempC };
        Console.WriteLine(
            $"Kalibriere {fan.Name} … Rampe in {options.StepSize}-Schritten, " +
            $"{options.SettleTime.TotalSeconds:0}s Settle pro Stufe (Strg+C bricht ab).");

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

        Console.WriteLine($"Ergebnis: Anlauf bei pwm={result.StartPwm}, Drehzahl {result.MinRpm}–{result.MaxRpm} RPM");

        // Anlaufpunkt + Messreihe in die (oben geladene) Konfiguration übernehmen — über denselben
        // Fail-Safe-Pfad wie der Daemon: MinPwm wird nur bei echtem Anlaufpunkt (MinRpm > 0) gesetzt,
        // sonst würde StartPwm==255 („nicht angelaufen") den Lüfter dauerhaft auf Volllast zwingen.
        // Ist der Lüfter noch nicht konfiguriert, vorher anlegen — ohne Namen, wie ConfigMapper.NewFan:
        // leer heißt „kein eigener Name", die Hardware-Bezeichnung gilt.
        AppConfig seeded = config.Fans.Any(f => f.FanId == id.Value)
            ? config
            : config with { Fans = config.Fans.Append(new FanConfig { FanId = id.Value }).ToList() };
        store.Save(ConfigMapper.ApplyCalibration(seeded, id.Value, result));

        Console.WriteLine(result.MinRpm > 0
            ? $"In {store.ConfigPath} gespeichert (MinPwm = {result.StartPwm})."
            : $"In {store.ConfigPath} gespeichert (kein Anlaufpunkt gefunden — MinPwm unverändert).");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine(
            """
            LinFan — Daemon-CLI (Linux hwmon)

            Befehle:
              list                 Sensoren & Lüfter anzeigen (Standard)
              monitor              Live-Anzeige von Temperaturen & Drehzahlen (Strg+C beendet)
              init                 Start-Konfiguration aus der Hardware erzeugen (Kurve + Zuordnung)
              run                  Regel-Loop + IPC-Server im Dauerbetrieb (ohne Root Dry-Run; Strg+C)
              calibrate <fanId>    Lüfter kalibrieren: Anlaufpunkt & Drehzahlbereich (Root)
              set <fanId> <0-255>  Lüfter manuell setzen (Root; mit Temperatur-Watchdog)
              auto <fanId>         Lüfter zurück in den Hardware-Auto-Modus

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

    private static string Runtime() =>
        $"{RuntimeInformation.OSDescription.Trim()} · {(Privileges.IsElevated() ? Privileges.ElevationTerm.ToLowerInvariant() : "user")}";
}
