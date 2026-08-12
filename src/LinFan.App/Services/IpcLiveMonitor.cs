// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc;
using LinFan.Ipc.Messages;

namespace LinFan.App.Services;

/// <summary>
/// <see cref="ILiveMonitor"/> auf Basis des IPC-Clients: verbindet sich (mit Wiederverbinden) zum
/// Daemon-Socket, cached den zuletzt empfangenen Snapshot und kann <c>reload</c> senden. Damit
/// liest die GUI keine Hardware mehr selbst — alles kommt über den Daemon (kein Root nötig).
/// </summary>
public sealed class IpcLiveMonitor : ILiveMonitor, ICommandSink, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private MonitorSnapshot _latest = MonitorSnapshot.Unavailable(Localizer.Instance["MainCtrl.ConnectingToService"]);
    private IIpcClient? _client;

    public IpcLiveMonitor() => _ = RunAsync(_cts.Token);

    public MonitorSnapshot Read()
    {
        lock (_lock)
            return _latest;
    }

    /// <summary>
    /// Sendet die bearbeitete Konfiguration an den Daemon (der sie autoritativ übernimmt &amp;
    /// persistiert). Gibt zurück, ob die Übertragung gelang (sonst war der Daemon gerade weg).
    /// </summary>
    public async Task<bool> SendConfigAsync(AppConfig config)
    {
        IIpcClient? client;
        lock (_lock)
            client = _client;
        if (client is null)
            return false;

        try
        {
            await client.SendCommandAsync(new IpcCommand(IpcCommand.SaveConfig, ToIpcConfig(config)));
            return true;
        }
        catch
        {
            return false; // Verbindung evtl. gerade weg
        }
    }

    /// <summary>Setzt einen Lüfter manuell auf einen festen PWM-Wert (0–255).</summary>
    public Task SendManualPwmAsync(string fanId, byte pwm) =>
        SendAsync(new IpcCommand(IpcCommand.SetManualPwm, Target: fanId, Value: pwm));

    /// <summary>Gibt einen Lüfter zurück an die Kurven-/Auto-Regelung.</summary>
    public Task SendFanAutoAsync(string fanId) =>
        SendAsync(new IpcCommand(IpcCommand.SetFanAuto, Target: fanId));

    /// <summary>Startet die Kalibrierung eines Lüfters.</summary>
    public Task SendStartCalibrationAsync(string fanId) =>
        SendAsync(new IpcCommand(IpcCommand.StartCalibration, Target: fanId));

    /// <summary>Bricht eine laufende Kalibrierung ab (oder quittiert einen Abschluss-Status).</summary>
    public Task SendCancelCalibrationAsync() =>
        SendAsync(new IpcCommand(IpcCommand.CancelCalibration));

    /// <summary>Dreht einen Lüfter kurz auf 100 % (andere gedrosselt), um ihn physisch zu identifizieren.</summary>
    public Task SendIdentifyAsync(string fanId) =>
        SendAsync(new IpcCommand(IpcCommand.Identify, Target: fanId));

    /// <summary>Startet die automatische Tacho-Kopplung eines Lüfters (antreiben, reagierenden Drehzahl-Sensor zuordnen).</summary>
    public Task SendStartTachMappingAsync(string fanId) =>
        SendAsync(new IpcCommand(IpcCommand.StartTachMapping, Target: fanId));

    /// <summary>Bricht eine laufende automatische Tacho-Kopplung ab (oder quittiert einen Abschluss-Status).</summary>
    public Task SendCancelTachMappingAsync() =>
        SendAsync(new IpcCommand(IpcCommand.CancelTachMapping));

    /// <summary>Ordnet einem Lüfter fest einen Drehzahl-Sensor zu (leer/<c>null</c> ⇒ Zuordnung löschen, zurück auf Backend-Heuristik).</summary>
    public Task SendSetFanTachometerAsync(string fanId, string? sensorId) =>
        SendAsync(new IpcCommand(IpcCommand.SetFanTachometer, Target: fanId, RpmSource: sensorId));

    /// <summary>Aktiviert ein Profil (Daemon übernimmt dessen Zuordnungen live).</summary>
    public Task SendActiveProfileAsync(string profileId) =>
        SendAsync(new IpcCommand(IpcCommand.SetActiveProfile, Target: profileId));

    /// <summary>Schaltet eine Kurve live an/aus (Daemon persistiert; aus ⇒ Lüfter auf Hardware-Auto).</summary>
    public Task SendSetCurveEnabledAsync(string curveId, bool enabled) =>
        SendAsync(new IpcCommand(IpcCommand.SetCurveEnabled, Target: curveId, Value: enabled ? 1 : 0));

    /// <summary>
    /// Replaces the daemon config wholesale (import/restore). Unlike <see cref="SendConfigAsync"/> the
    /// daemon-owned fields ride along (<c>withDaemonOwned: true</c>) — calibration and tachometer assignment
    /// — so a backup can actually restore them.
    /// </summary>
    public async Task<bool> SendReplaceConfigAsync(AppConfig config)
    {
        IIpcClient? client;
        lock (_lock)
            client = _client;
        if (client is null)
            return false;

        try
        {
            await client.SendCommandAsync(new IpcCommand(IpcCommand.ReplaceConfig, ToIpcConfig(config, withDaemonOwned: true)));
            return true;
        }
        catch
        {
            return false; // Verbindung evtl. gerade weg
        }
    }

    /// <summary>Setzt die Daemon-Config auf Werkszustand zurück. Liefert, ob die Übertragung gelang.</summary>
    public async Task<bool> SendResetConfigAsync()
    {
        IIpcClient? client;
        lock (_lock)
            client = _client;
        if (client is null)
            return false;

        try
        {
            await client.SendCommandAsync(new IpcCommand(IpcCommand.ResetConfig));
            return true;
        }
        catch
        {
            return false; // Verbindung evtl. gerade weg
        }
    }

    private async Task SendAsync(IpcCommand command)
    {
        IIpcClient? client;
        lock (_lock)
            client = _client;
        if (client is null)
            return;
        try { await client.SendCommandAsync(command); }
        catch { /* Verbindung evtl. gerade weg */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = new IpcClient();
            Exception? failure = null;
            try
            {
                await client.ConnectAsync(ct);
                lock (_lock)
                    _client = client;

                await foreach (IpcSnapshot snapshot in client.ReadSnapshotsAsync(ct))
                    SetLatest(Convert(snapshot));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failure = ex; // Verbindung verloren oder Daemon nicht da — unten Wiederverbinden
            }
            finally
            {
                lock (_lock)
                    _client = null;
                await client.DisposeAsync();
            }

            // „Zugriff verweigert" (Socket existiert, aber Berechtigung fehlt) getrennt vom generischen
            // „nicht erreichbar" melden — sonst rät der Nutzer bei laufendem Dienst falsch (Linux: linfan-Gruppe).
            SetLatest(MonitorSnapshot.Unavailable(IsAccessDenied(failure)
                ? Localizer.Instance["IpcLiveMonitor.AccessDenied"]
                : Localizer.Instance.Format("IpcLiveMonitor.Unreachable", string.Join(", ", IpcEndpoint.ClientCandidates()))));
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// War der Verbindungsfehler ein Berechtigungsproblem (Socket da, aber kein Zugriff)? Auf Linux
    /// <see cref="SocketError.AccessDenied"/> (EACCES, fehlende <c>linfan</c>-Gruppe), plattformübergreifend
    /// auch <see cref="UnauthorizedAccessException"/> (z. B. Named-Pipe-ACL auf Windows). Die Inner-Kette wird
    /// mitgeprüft, falls der Transport die Ausnahme verpackt.
    /// </summary>
    private static bool IsAccessDenied(Exception? ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AccessDenied } or UnauthorizedAccessException)
                return true;
        }
        return false;
    }

    private void SetLatest(MonitorSnapshot snapshot)
    {
        lock (_lock)
            _latest = snapshot;
    }

    /// <summary>IPC snapshot → GUI snapshot. <c>internal</c> so the name resolution can be tested.</summary>
    internal static MonitorSnapshot Convert(IpcSnapshot s)
    {
        var sensors = s.Sensors
            .Select(x => new SensorReading(x.Id, x.Name, ParseKind(x.Kind), x.Unit, x.Value))
            .ToList();
        var fans = s.Fans
            .Select(x => new FanReading(
                x.Id, x.Name, x.Rpm, (byte)Math.Clamp(x.Pwm, 0, 255), ParseMode(x.Mode), x.CanControl,
                x.ManualOverride))
            .ToList();
        CalibrationStatus? cal = s.Calibration is { } c
            ? new CalibrationStatus(c.FanId, c.Phase, c.CurrentPwm, c.CurrentRpm, c.Running, c.Done, c.StartPwm,
                                    c.FailReason, c.OverTempC, c.OverLimitC,
                                    // From the live list, not from the config: the daemon has already resolved
                                    // the display name there (own name, else hardware label). The config
                                    // carries no name for a fan that was never saved — the message would
                                    // otherwise fall back to the raw hardware id.
                                    FanName: s.Fans.FirstOrDefault(f => f.Id == c.FanId)?.Name)
            : null;
        IdentifyStatus? ident = s.Identify is { } id
            ? new IdentifyStatus(id.FanId, id.Running, id.FailReason, id.OverTempC, id.OverLimitC)
            : null;
        TachMappingStatus? tach = s.TachMapping is { } tm
            ? new TachMappingStatus(tm.FanId, tm.Phase, tm.Running, tm.MatchedTachId, tm.RiseRpm, tm.FailReason,
                                    tm.OverTempC, tm.OverLimitC)
            : null;
        return new MonitorSnapshot(IpcStatusText.Status(s.Status), sensors, fans, ToAppConfig(s.Config), Connected: true,
                                   Calibration: cal, Identify: ident, TachMapping: tach);
    }

    private static SensorKind ParseKind(string kind) =>
        kind == nameof(SensorKind.FanRpm) ? SensorKind.FanRpm : SensorKind.Temperature;

    private static FanMode ParseMode(string mode) =>
        mode == nameof(FanMode.Manual) ? FanMode.Manual : FanMode.Auto;

    // --- Config-Mapping IPC ⇄ Core-Models (nur Datenformen, keine Core-Services) ----------------

    private static AppConfig ToAppConfig(IpcConfig? c)
    {
        if (c is null)
            return AppConfig.Empty;

        return new AppConfig
        {
            Curves = c.Curves.Select(ToCoreCurve).ToList(),
            Fans = c.Fans.Select(ToCoreFan).ToList(),
            Sensors = c.Sensors.Select(s => new SensorConfig
            {
                SensorId = s.Id,
                Name = s.Name,
                Group = string.IsNullOrWhiteSpace(s.Group) ? null : s.Group,
                Hidden = s.Hidden,
            }).ToList(),
            Profiles = (c.Profiles ?? Array.Empty<IpcProfile>()).Select(p => new Profile
            {
                Id = p.Id,
                Name = p.Name,
                // Defensiv: ein älterer Daemon kennt das Curves-Feld noch nicht (null).
                Curves = (p.Curves ?? Array.Empty<IpcCurve>()).Select(ToCoreCurve).ToList(),
                Assignments = (p.Assignments ?? Array.Empty<IpcProfileAssignment>())
                    .Select(a => new ProfileAssignment(a.FanId, a.CurveId)).ToList(),
            }).ToList(),
            ActiveProfileId = c.ActiveProfileId,
            OnboardingCompleted = c.OnboardingCompleted,
        };
    }

    // withDaemonOwned: normally false — the daemon owns the calibration and the tachometer assignment, and a
    // merge (SaveConfig) must not overwrite them. A full replace (ReplaceConfig/import) carries them along,
    // otherwise restoring a backup would silently drop what the backup holds.
    // internal so a test can cover the mapping — this is the seam where a missing field goes unnoticed.
    internal static IpcConfig ToIpcConfig(AppConfig config, bool withDaemonOwned = false) => new(
        config.Curves.Select(ToIpcCurve).ToList(),
        config.Fans.Select(f => ToIpcFan(f, withDaemonOwned)).ToList(),
        config.Sensors.Select(s => new IpcSensorName(s.SensorId, s.Name, s.Group, s.Hidden)).ToList(),
        config.Profiles.Select(p => new IpcProfile(
            p.Id, p.Name,
            p.Assignments.Select(a => new IpcProfileAssignment(a.FanId, a.CurveId)).ToList(),
            p.Curves.Select(ToIpcCurve).ToList())).ToList(),
        config.ActiveProfileId,
        config.OnboardingCompleted);

    private static CurveConfig ToCoreCurve(IpcCurve x)
    {
        // Schema-2-Quelle bevorzugen; sonst (älterer Daemon) aus dem alten Einzelfeld migrieren.
        IReadOnlyList<string> sources = CurveSourceResolver.ResolveSources(x.SourceSensorId, x.SourceSensorIds);

        return new CurveConfig
        {
            Id = x.Id,
            Name = x.Name,
            Enabled = x.Enabled,
            SourceSensorIds = sources,
            Aggregation = CurveSourceResolver.ParseAggregation(x.Aggregation),
            HysteresisC = x.HysteresisC,
            InterpolationMode = CurveSourceResolver.ParseEnum(x.InterpolationMode, InterpolationMode.Linear),
            Points = x.Points.Select(p => new CurvePoint(p.TemperatureC, p.Percent)).ToList(),
        };
    }

    private static IpcCurve ToIpcCurve(CurveConfig c)
    {
        IReadOnlyList<string> sources = CurveSourceResolver.ResolveSources(c.SourceSensorId, c.SourceSensorIds);

        return new IpcCurve(
            c.Id, c.Name,
            sources.Count > 0 ? sources[0] : "", // alte Quelle spiegeln (Abwärtskompat.)
            c.HysteresisC,
            c.Points.Select(p => new IpcCurvePoint(p.TemperatureC, p.Percent)).ToList(),
            sources,
            c.Aggregation.ToString(),
            c.InterpolationMode.ToString(),
            c.Enabled);
    }

    private static FanConfig ToCoreFan(IpcFanAssignment x) => new()
    {
        FanId = x.FanId,
        Name = x.Name,
        MinPwm = (byte)Math.Clamp(x.MinPwm, 0, 255),
        MaxPwm = (byte)Math.Clamp(x.MaxPwm, 0, 255),
        AssignedCurveId = x.AssignedCurveId,
        Location = Enum.TryParse(x.Location, out FanLocation loc) ? loc : FanLocation.Unspecified,
        Hidden = x.Hidden,
        // Zugeordneter Drehzahl-Sensor read-only übernehmen (fürs Dropdown „aktuelle Zuordnung"). Wie die
        // Kalibrierung daemon-verwaltet: die GUI schickt das Feld nie über SaveConfig zurück (ToIpcFan lässt es weg),
        // Änderungen laufen über das eigene SetFanTachometer-Command.
        RpmSource = string.IsNullOrWhiteSpace(x.RpmSource) ? null : x.RpmSource,
        // Kalibrier-Ergebnis read-only übernehmen (für das „bereits kalibriert"-Badge nach Neustart). Die rohe
        // Messreihe bleibt im Daemon; die GUI schickt das Feld nie zurück (ToIpcFan), der Daemon ist Eigentümer.
        Calibration = x.Calibration is { } cal
            ? new FanCalibration { StartPwm = (byte)Math.Clamp(cal.StartPwm, 0, 255), MinRpm = cal.MinRpm, MaxRpm = cal.MaxRpm }
            : null,
    };

    private static IpcFanAssignment ToIpcFan(FanConfig f, bool withDaemonOwned = false) => new(
        f.FanId, f.Name, f.MinPwm, f.MaxPwm, f.AssignedCurveId, f.Location.ToString(), f.Hidden,
        withDaemonOwned && f.Calibration is { } cal
            ? new IpcFanCalibration(cal.StartPwm, cal.MinRpm, cal.MaxRpm)
            : null,
        withDaemonOwned ? f.RpmSource : null);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        IIpcClient? client;
        lock (_lock)
            client = _client;
        if (client is not null)
            await client.DisposeAsync();
        _cts.Dispose();
    }
}
