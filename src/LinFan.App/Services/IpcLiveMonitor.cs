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
/// liest die GUI keine Hardware mehr selbst - alles kommt über den Daemon (kein Root nötig).
/// </summary>
public sealed class IpcLiveMonitor : ILiveMonitor, ICommandSink, IReconnectable, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Func<IIpcClient> _createClient;
    private readonly TimeSpan _retryDelay;
    private readonly GuiErrorLog _log;

    // Cuts the retry delay short. Released only while _waiting, and drained when the wait ends, so a request that
    // arrives during an attempt cannot leave a wake-up behind that skips the delay after the next failure.
    private readonly SemaphoreSlim _wake = new(0, 1);
    private MonitorSnapshot _latest = MonitorSnapshot.Unavailable(
        Localizer.Instance["MainCtrl.ConnectingToService"], ConnectionProblem.Connecting);
    private IIpcClient? _client;
    private TaskCompletionSource<bool>? _attemptSettled; // a reconnect request waits for the next outcome in _latest
    private bool _waiting; // the loop sits in the retry delay
    private bool _stopped;

    public IpcLiveMonitor() : this(() => new IpcClient(), TimeSpan.FromSeconds(2)) { }

    /// <summary>Test seam: the client per connection attempt, the delay between failed attempts, the error log.</summary>
    internal IpcLiveMonitor(Func<IIpcClient> createClient, TimeSpan retryDelay, GuiErrorLog? log = null)
    {
        _createClient = createClient;
        _retryDelay = retryDelay;
        _log = log ?? GuiErrorLog.Default;
        _ = RunAsync(_cts.Token);
    }

    public MonitorSnapshot Read()
    {
        lock (_lock)
            return _latest;
    }

    public Task<bool> RequestReconnectAsync()
    {
        lock (_lock)
        {
            if (_stopped)
                return Task.FromResult(false); // no attempt follows
            if (_latest.Connected)
                return Task.FromResult(true);
            _attemptSettled ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // During an attempt the request only waits for its outcome; that attempt is the reconnect.
            if (_waiting && _wake.CurrentCount == 0)
                _wake.Release();
            return _attemptSettled.Task;
        }
    }

    /// <summary>
    /// Sendet die bearbeitete Konfiguration an den Daemon (der sie autoritativ übernimmt &amp;
    /// persistiert). Gibt zurück, ob die Übertragung gelang (sonst war der Daemon gerade weg).
    /// </summary>
    public Task<bool> SendConfigAsync(AppConfig config, IReadOnlyDictionary<string, byte> baseMinPwm) =>
        TrySendAsync(new IpcCommand(IpcCommand.SaveConfig, ToIpcConfig(config, baseMinPwm: baseMinPwm)));

    /// <summary>Setzt einen Lüfter manuell auf einen festen PWM-Wert (0-255).</summary>
    public Task SendManualPwmAsync(string fanId, byte pwm) =>
        TrySendAsync(new IpcCommand(IpcCommand.SetManualPwm, Target: fanId, Value: pwm));

    /// <summary>Gibt einen Lüfter zurück an die Kurven-/Auto-Regelung.</summary>
    public Task SendFanAutoAsync(string fanId) =>
        TrySendAsync(new IpcCommand(IpcCommand.SetFanAuto, Target: fanId));

    /// <summary>Startet die Kalibrierung eines Lüfters.</summary>
    public Task SendStartCalibrationAsync(string fanId) =>
        TrySendAsync(new IpcCommand(IpcCommand.StartCalibration, Target: fanId));

    /// <summary>Bricht eine laufende Kalibrierung ab (oder quittiert einen Abschluss-Status).</summary>
    public Task SendCancelCalibrationAsync() =>
        TrySendAsync(new IpcCommand(IpcCommand.CancelCalibration));

    /// <summary>Dreht einen Lüfter kurz auf 100 % (andere gedrosselt), um ihn physisch zu identifizieren.</summary>
    public Task SendIdentifyAsync(string fanId) =>
        TrySendAsync(new IpcCommand(IpcCommand.Identify, Target: fanId));

    /// <summary>Startet die automatische Tacho-Kopplung eines Lüfters (antreiben, reagierenden Drehzahl-Sensor zuordnen).</summary>
    public Task SendStartTachMappingAsync(string fanId) =>
        TrySendAsync(new IpcCommand(IpcCommand.StartTachMapping, Target: fanId));

    /// <summary>Bricht eine laufende automatische Tacho-Kopplung ab (oder quittiert einen Abschluss-Status).</summary>
    public Task SendCancelTachMappingAsync() =>
        TrySendAsync(new IpcCommand(IpcCommand.CancelTachMapping));

    /// <summary>Ordnet einem Lüfter fest einen Drehzahl-Sensor zu (leer/<c>null</c> ⇒ Zuordnung löschen, zurück auf Backend-Heuristik).</summary>
    public Task SendSetFanTachometerAsync(string fanId, string? sensorId) =>
        TrySendAsync(new IpcCommand(IpcCommand.SetFanTachometer, Target: fanId, RpmSource: sensorId));

    /// <summary>Aktiviert ein Profil (Daemon übernimmt dessen Zuordnungen live).</summary>
    public Task SendActiveProfileAsync(string profileId) =>
        TrySendAsync(new IpcCommand(IpcCommand.SetActiveProfile, Target: profileId));

    /// <summary>Schaltet eine Kurve live an/aus (Daemon persistiert; aus ⇒ Lüfter auf Hardware-Auto).</summary>
    public Task SendSetCurveEnabledAsync(string curveId, bool enabled) =>
        TrySendAsync(new IpcCommand(IpcCommand.SetCurveEnabled, Target: curveId, Value: enabled ? 1 : 0));

    /// <summary>
    /// Replaces the daemon config wholesale (import/restore). Unlike <see cref="SendConfigAsync"/> the
    /// daemon-owned fields ride along (<c>withDaemonOwned: true</c>) - calibration and tachometer assignment
    /// - so a backup can actually restore them.
    /// </summary>
    public Task<bool> SendReplaceConfigAsync(AppConfig config) =>
        TrySendAsync(new IpcCommand(IpcCommand.ReplaceConfig, ToIpcConfig(config, withDaemonOwned: true)));

    /// <summary>Setzt die Daemon-Config auf Werkszustand zurück. Liefert, ob die Übertragung gelang.</summary>
    public Task<bool> SendResetConfigAsync() => TrySendAsync(new IpcCommand(IpcCommand.ResetConfig));

    public Task<bool> SendAcceptHardwareAsync(string key, bool resetSensorNames) =>
        TrySendAsync(new IpcCommand(IpcCommand.AcceptHardware, Target: key, Value: resetSensorNames ? 1 : 0));

    public Task<bool> SendForgetHardwareAsync(string key) =>
        TrySendAsync(new IpcCommand(IpcCommand.ForgetHardware, Target: key));

    /// <summary>Sends <paramref name="command"/>; returns whether it went out (no connection, or it dropped: <c>false</c>).</summary>
    private async Task<bool> TrySendAsync(IpcCommand command)
    {
        IIpcClient? client;
        lock (_lock)
            client = _client;
        if (client is null)
            return false;

        try
        {
            await client.SendCommandAsync(command);
            return true;
        }
        catch
        {
            return false; // Verbindung evtl. gerade weg
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        bool unreadable = false; // within a series of unreadable snapshots - logged once, reconnects included
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IIpcClient client = _createClient();
                Exception? failure = null;
                try
                {
                    await client.ConnectAsync(ct);
                    lock (_lock)
                        _client = client;

                    await foreach (IpcSnapshot? snapshot in client.ReadSnapshotsAsync(ct))
                        SetLatest(ConvertOrUnreadable(snapshot, ref unreadable));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    failure = ex; // Verbindung verloren oder Daemon nicht da - unten Wiederverbinden
                }
                finally
                {
                    lock (_lock)
                        _client = null;
                    await client.DisposeAsync();
                }

                // „Zugriff verweigert" (Socket existiert, aber Berechtigung fehlt) getrennt vom generischen
                // „nicht erreichbar" melden - sonst rät der Nutzer bei laufendem Dienst falsch (Linux: linfan-Gruppe).
                SetLatest(IsAccessDenied(failure)
                    ? MonitorSnapshot.Unavailable(Localizer.Instance["IpcLiveMonitor.AccessDenied"], ConnectionProblem.AccessDenied)
                    : MonitorSnapshot.Unavailable(
                        Localizer.Instance.Format("IpcLiveMonitor.Unreachable", string.Join(", ", IpcEndpoint.ClientCandidates())),
                        ConnectionProblem.Unreachable),
                    waiting: true);
                try { await _wake.WaitAsync(_retryDelay, ct); }
                catch (OperationCanceledException) { break; }
                lock (_lock)
                {
                    _waiting = false;
                    _wake.Wait(0); // released as the delay ran out: the attempt starting now serves that request
                }
            }
        }
        finally
        {
            TaskCompletionSource<bool>? settled;
            lock (_lock)
            {
                _stopped = true;
                settled = _attemptSettled;
                _attemptSettled = null;
            }
            settled?.TrySetResult(false); // no attempt follows - nobody may wait for one
        }
    }

    /// <summary>
    /// A snapshot the GUI cannot read is no lost connection: the daemon is there and keeps the fans on its curves. Thrown
    /// into the reconnect path, it would end in "unreachable" forever; instead the connection stays, the GUI reports
    /// <see cref="ConnectionProblem.UnreadableData"/> - without the last values, which are no longer current - and the
    /// next readable snapshot ends it. The first snapshot of a series goes to the GUI log. The service's version rides
    /// along when it sent one: the banner names it. A <c>null</c> snapshot is one the client could not even parse (see
    /// <see cref="IIpcClient.ReadSnapshotsAsync"/>) - no version then.
    /// </summary>
    private MonitorSnapshot ConvertOrUnreadable(IpcSnapshot? snapshot, ref bool unreadable)
    {
        Exception? failure = null;
        if (snapshot is not null)
        {
            try
            {
                MonitorSnapshot read = Convert(snapshot);
                unreadable = false;
                return read;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (!unreadable)
        {
            string problem = snapshot is null
                ? "A snapshot from the service cannot be read (not valid for this GUI, too large, or its configuration never arrived)"
                : "A snapshot from the service cannot be read";
            _log.Error("Snapshot",
                $"{problem} (GUI {AppVersion.InformationalRaw}, service {snapshot?.DaemonVersion ?? "unknown"}) - the "
                + "connection stays, waiting for a readable one", failure);
        }
        unreadable = true;
        MonitorSnapshot unavailable = MonitorSnapshot.Unavailable(
            Localizer.Instance["IpcLiveMonitor.UnreadableData"], ConnectionProblem.UnreadableData);
        return unavailable with
        {
            DaemonVersion = snapshot?.DaemonVersion is { } version ? UntrustedText.Strip(version) : null,
        };
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

    /// <summary>
    /// Publishes an attempt's outcome - a snapshot from the daemon or the failure - and releases a waiting reconnect.
    /// <paramref name="waiting"/> enters the retry delay in the same step, so a request made once the failure shows
    /// always wakes the loop.
    /// </summary>
    private void SetLatest(MonitorSnapshot snapshot, bool waiting = false)
    {
        TaskCompletionSource<bool>? settled;
        lock (_lock)
        {
            _latest = snapshot;
            _waiting = waiting;
            settled = _attemptSettled;
            _attemptSettled = null;
        }
        settled?.TrySetResult(snapshot.Connected);
    }

    /// <summary>IPC snapshot → GUI snapshot. <c>internal</c> so the name resolution can be tested.</summary>
    internal static MonitorSnapshot Convert(IpcSnapshot s)
    {
        var sensors = s.Sensors
            .Select(x => new SensorReading(x.Id, x.Name, ParseKind(x.Kind), x.Unit, x.Value, x.Label))
            .ToList();
        var fans = s.Fans
            .Select(x => new FanReading(
                x.Id, x.Name, x.Rpm, (byte)Math.Clamp(x.Pwm, 0, 255), x.CanControl,
                x.ManualOverride, ParseName<FanControlState>(x.ControlState), x.ControlCurveId,
                x.CanControl ? null : ParseName<FanReadOnlyReason>(x.ReadOnlyReason), x.Tachometer,
                ParseName<CalibrationMismatch>(x.CalibrationMismatch)))
            .ToList();
        CalibrationStatus? cal = s.Calibration is { } c
            ? new CalibrationStatus(c.FanId, c.Phase, c.CurrentPwm, c.CurrentRpm, c.Running, c.Done, c.StartPwm,
                                    c.FailReason, c.OverTempC, c.OverLimitC,
                                    // From the live list, not from the config: the daemon has already resolved
                                    // the display name there (own name, else hardware label). The config
                                    // carries no name for a fan that was never saved - the message would
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
                                   Calibration: cal, Identify: ident, TachMapping: tach, DryRun: s.DryRun,
                                   FailSafe: ParseName<FailSafeState>(s.FailSafe),
                                   ConfigProblem: ParseName<ConfigLoadProblem>(s.ConfigProblem),
                                   ConfigSetAsidePath: s.ConfigSetAsidePath is { } path ? UntrustedText.Strip(path) : null,
                                   DaemonVersion: s.DaemonVersion is { } version ? UntrustedText.Strip(version) : null,
                                   HardwareChanges: ToHardwareChanges(s.HardwareChanges));
    }

    /// <summary>
    /// The daemon's hardware changes; <c>null</c> when there are none. Names and fingerprints are shown, so they lose
    /// unsafe characters; the key and the ids are command targets and lookups and stay as sent. A change without a key
    /// cannot be resolved and is left out; missing lists read as empty.
    /// </summary>
    private static IReadOnlyList<HardwareChangeReading>? ToHardwareChanges(IReadOnlyList<IpcHardwareChange>? changes)
    {
        List<HardwareChangeReading>? read = changes?
            .Where(c => !string.IsNullOrEmpty(c?.Key))
            .Select(c => new HardwareChangeReading(
                c.Key, ParseName<HardwareChangeKind>(c.Kind), Shown(c.OldName), Shown(c.OldFingerprint), Shown(c.NewName),
                Shown(c.NewFingerprint), Ids(c.FanIds), Ids(c.SensorIds)))
            .ToList();
        return read is { Count: > 0 } ? read : null;

        static string? Shown(string? text) => text is null ? null : UntrustedText.Strip(text);
        static IReadOnlyList<string> Ids(IReadOnlyList<string>? ids) => ids?.OfType<string>().ToList() ?? [];
    }

    /// <summary>
    /// A state the daemon sends by member name; <c>null</c> when it is missing (older daemon) or unknown (newer
    /// daemon). Names only, exact case - the daemon writes <c>ToString()</c>, so anything else is not a state.
    /// </summary>
    private static T? ParseName<T>(string? name) where T : struct, Enum =>
        name is not null && Enum.IsDefined(typeof(T), name) ? Enum.Parse<T>(name) : null;

    private static SensorKind ParseKind(string kind) =>
        kind == nameof(SensorKind.FanRpm) ? SensorKind.FanRpm : SensorKind.Temperature;

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
            SensorGroups = c.SensorGroups ?? Array.Empty<string>(), // null: a daemon that predates the field
            CaseType = CurveSourceResolver.ParseEnum(c.CaseType, CaseType.Tower),
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

    // withDaemonOwned: normally false - the daemon owns the calibration and the tachometer assignment, and a
    // merge (SaveConfig) must not overwrite them. A full replace (ReplaceConfig/import) carries them along,
    // otherwise restoring a backup would silently drop what the backup holds.
    // baseMinPwm: a save's base per fan (see ICommandSink.SendConfigAsync); it travels as each fan's BaseMinPwm.
    // internal so a test can cover the mapping - this is the seam where a missing field goes unnoticed.
    internal static IpcConfig ToIpcConfig(
        AppConfig config, bool withDaemonOwned = false, IReadOnlyDictionary<string, byte>? baseMinPwm = null) => new(
            config.Curves.Select(ToIpcCurve).ToList(),
            config.Fans.Select(f => ToIpcFan(f, withDaemonOwned,
                baseMinPwm is not null && baseMinPwm.TryGetValue(f.FanId, out byte b) ? b : null)).ToList(),
            config.Sensors.Select(s => new IpcSensorName(s.SensorId, s.Name, s.Group, s.Hidden)).ToList(),
            config.Profiles.Select(p => new IpcProfile(
                p.Id, p.Name,
                p.Assignments.Select(a => new IpcProfileAssignment(a.FanId, a.CurveId)).ToList(),
                p.Curves.Select(ToIpcCurve).ToList())).ToList(),
            config.ActiveProfileId,
            config.OnboardingCompleted,
            config.SensorGroups,
            config.CaseType.ToString());

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
            // Älterer Daemon ohne das Feld → Core-Default (siehe IpcCurve.SmoothingSeconds).
            SmoothingSeconds = x.SmoothingSeconds ?? CurveConfig.DefaultSmoothingSeconds,
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
            c.Enabled,
            c.SmoothingSeconds);
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
        // The calibration, read-only (the "calibrated" badge after a restart). The daemon owns it: a save never sends it
        // back (ToIpcFan), only a replace does - the measuring sensor and the series ride along so that a backup export
        // and its import keep them. A sample whose PWM is no raw value is dropped, as the daemon drops it: clamped, it
        // would claim a speed at a PWM it was not measured at.
        Calibration = x.Calibration is { } cal
            ? new FanCalibration
            {
                StartPwm = (byte)Math.Clamp(cal.StartPwm, 0, 255),
                MinRpm = cal.MinRpm,
                MaxRpm = cal.MaxRpm,
                Tachometer = string.IsNullOrWhiteSpace(cal.Tachometer) ? null : cal.Tachometer,
                Samples = cal.Samples?.OfType<IpcCalibrationSample>()
                    .Where(s => s.Pwm is >= 0 and <= 255)
                    .Select(s => new CalibrationSample((byte)s.Pwm, s.Rpm))
                    .ToList() ?? [],
            }
            : null,
    };

    private static IpcFanAssignment ToIpcFan(FanConfig f, bool withDaemonOwned, byte? baseMinPwm) => new(
        f.FanId, f.Name, f.MinPwm, f.MaxPwm, f.AssignedCurveId, f.Location.ToString(), f.Hidden,
        withDaemonOwned && f.Calibration is { } cal
            ? new IpcFanCalibration(cal.StartPwm, cal.MinRpm, cal.MaxRpm, cal.Tachometer,
                (cal.Samples ?? []).Select(s => new IpcCalibrationSample(s.Pwm, s.Rpm)).ToList())
            : null,
        withDaemonOwned ? f.RpmSource : null,
        baseMinPwm);

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
