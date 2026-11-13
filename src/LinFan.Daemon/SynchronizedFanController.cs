// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinFan.Daemon;

/// <summary>
/// Thread-sicherer Wrapper um einen <see cref="IFanController"/>: serialisiert alle Hardware-Zugriffe
/// über ein gemeinsames Gate. So kommen sich Regel-Loop und Kalibrierung (verschiedene Threads) beim
/// Schreiben nicht in die Quere - insbesondere darf ein Watchdog-<c>RestoreDefaults</c> nicht mit
/// einem gleichzeitigen Rampen-Write auf demselben Kanal verschränken.
/// <para>
/// <b>Fail-Safe-Ausnahme:</b> <see cref="RestoreDefaults"/> wartet nur <see cref="FailSafeGateTimeout"/>
/// lang aufs Gate und schreibt danach <i>am Gate vorbei</i>. Grund: die Backend-Writes dahinter sind
/// synchron und ohne Timeout - hängt ein Write (EC-/Treiber-Wedge), hielte er das Gate sonst für immer,
/// und der <b>einzige</b> Rückfall-Mechanismus wäre selbst deadlockbar (ein Übertemp-<c>RestoreDefaults</c>
/// aus dem Loop-Thread blockiert auf dem Gate, das ein Kalibrier-/Identify-Write hält).
/// </para>
/// <para>
/// Der gate-freie Restore zielt konkret auf den <b>Linux-sysfs-Fall</b>: dort nutzt jeder Write einen
/// eigenen File-Deskriptor und der Kernel serialisiert <c>write()</c> pro Attribut - ein Bypass kann also
/// nur die Reihenfolge betreffen, nichts korrumpieren. Das Windows-LHM-Backend ist nicht dokumentiert
/// thread-sicher; dort ist der Bypass praktisch nie aktiv (LHM-Writes ~1 ms erreichen die
/// <see cref="FailSafeGateTimeout"/> nie - nur ein echter EC-/sysfs-Wedge tut das).
/// </para>
/// <para>
/// Every other read and write waits at most <see cref="DefaultGateTimeout"/> and then throws a
/// <see cref="TimeoutException"/>, so a wedged write cannot stall the control loop (and its watchdog) either.
/// <see cref="DiscoverFans"/> and <see cref="CanControl"/> take no gate: they are fixed for the backend's lifetime.
/// </para>
/// <para>
/// Misst zusätzlich die reine Backend-Dauer jedes Schreib-Aufrufs (<c>SetMode</c>/<c>SetPwm</c>/
/// <c>RestoreDefaults</c> = LHM <c>Control.SetSoftware</c> bzw. der sysfs-Write). Hintergrund: das
/// grobe Gate ist nur tragbar, solange Writes schnell sind - ein langsames Backend würde unter Last
/// ein Watchdog-<c>RestoreDefaults</c> hinter sich stauen.
/// Die Messung soll die Zahl für diese Entscheidung liefern.
/// </para>
/// </summary>
internal sealed class SynchronizedFanController : IFanController, IManualWriteLatch
{
    /// <summary>
    /// Schwelle, ab der ein einzelner Hardware-Write als fail-safe-relevant gilt: er hält das Gate so
    /// lange, dass ein gleichzeitiges Watchdog-<c>RestoreDefaults</c> dahinter warten müsste.
    /// </summary>
    private const double SlowWriteThresholdMs = 50.0;

    /// <summary>
    /// Maximale Wartezeit, die ein Fail-Safe-<see cref="RestoreDefaults"/> aufs Gate verwendet, bevor es
    /// am Gate vorbei schreibt. Großzügig gegenüber einem gesunden Write (Messungen: sub-ms sysfs, ~1 ms
    /// LHM), aber weit unter dem systemd-<c>TimeoutStopSec</c> und jeder relevanten Übertemp-Frist - ein
    /// echter Wedge fällt so nach Bruchteilen einer Sekunde in den gate-freien Restore-Pfad.
    /// </summary>
    private static readonly TimeSpan FailSafeGateTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long any other read or write waits for the gate before it fails with a <see cref="TimeoutException"/>.
    /// Far above a healthy hold (see <see cref="SlowWriteThresholdMs"/>): only a write wedged in the driver or EC
    /// reaches it. Without the bound such a wedge in a coordinator's write would also stall the control loop - and
    /// with it the over-temperature watchdog - at its next fan read or write.
    /// </summary>
    private static readonly TimeSpan DefaultGateTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IFanController _inner;
    private readonly ILogger _log;
    private readonly object _gate = new();

    /// <summary>
    /// Schützt <see cref="_maxWriteMs"/> und <see cref="_reportedRestoreProblems"/> - Writes und Restores laufen aus
    /// Regel-Loop und Kalibrierung nebenläufig.
    /// </summary>
    private readonly object _statGate = new();
    private double _maxWriteMs;

    // Checked under the gate, so a write that is still waiting for the gate when shutdown begins cannot
    // slip in after the final RestoreDefaults.
    private volatile bool _manualWritesBlocked;

    private readonly TimeSpan _gateTimeout;

    // Set by a gate wait that timed out, cleared by the next one that gets in: meanwhile reads and writes do not wait
    // at all, so a wedged write costs the control loop one timeout instead of one per call.
    private volatile bool _gateStalled;

    // The restore problems last logged, so a restore repeated every tick (over-temperature) logs a problem once.
    private string _reportedRestoreProblems = "";

    public SynchronizedFanController(IFanController inner, ILogger? log = null, TimeSpan? gateTimeout = null)
    {
        _inner = inner;
        _log = log ?? NullLogger.Instance;
        _gateTimeout = gateTimeout ?? DefaultGateTimeout;
    }

    public void BlockManualWrites() => _manualWritesBlocked = true;

    /// <summary>
    /// Whether a write is holding the gate past <see cref="DefaultGateTimeout"/> - the control loop's writes fail then,
    /// so its curves cannot raise a fan.
    /// </summary>
    public bool GateStalled => _gateStalled;

    // Discovery and CanControl are fixed for the backend's lifetime (IFanController contract) and every backend
    // answers them from data it built at start, so they need no gate - and a wedged write cannot make a fan look
    // uncontrollable to the loop.
    public IReadOnlyList<FanDescriptor> DiscoverFans() => _inner.DiscoverFans();

    public bool CanControl(FanId id) => _inner.CanControl(id);

    public FanMode GetMode(FanId id)
    {
        EnterGate("GetMode", id.Value);
        try { return _inner.GetMode(id); }
        finally { Monitor.Exit(_gate); }
    }

    public void SetMode(FanId id, FanMode mode) =>
        TimedWrite("SetMode", id.Value, () => _inner.SetMode(id, mode), drivesManual: mode != FanMode.Auto);

    public byte GetPwm(FanId id)
    {
        EnterGate("GetPwm", id.Value);
        try { return _inner.GetPwm(id); }
        finally { Monitor.Exit(_gate); }
    }

    public void SetPwm(FanId id, byte value) =>
        TimedWrite("SetPwm", id.Value, () => _inner.SetPwm(id, value), drivesManual: true);

    /// <summary>
    /// Fail-Safe-Restore auf Hardware-Auto. Wartet nur <see cref="FailSafeGateTimeout"/> aufs Gate und
    /// schreibt sonst am Gate vorbei - der sichere Zustand darf nie hinter einem hängenden Write
    /// blockieren (siehe Klassen-Doku). Der Restore selbst wirft laut Vertrag nicht.
    /// </summary>
    public void RestoreDefaults()
    {
        bool taken = false;
        TimeSpan elapsed;
        try
        {
            Monitor.TryEnter(_gate, FailSafeGateTimeout, ref taken);
            if (taken)
                _gateStalled = false;

            long start = Stopwatch.GetTimestamp();
            _inner.RestoreDefaults();
            elapsed = Stopwatch.GetElapsedTime(start);
        }
        finally
        {
            if (taken)
                Monitor.Exit(_gate);
        }

        // Only now, after the write: a logger that blocks must not hold up the safe state.
        try
        {
            if (!taken)
                _log.LogError(
                    "Fail-Safe RestoreDefaults: Gate nach {Ms:F0} ms nicht frei (hängender Hardware-Write?) - "
                    + "am Gate vorbei geschrieben, um den sicheren Zustand nicht zu blockieren.",
                    FailSafeGateTimeout.TotalMilliseconds);
            ReportRestoreProblems();
        }
        catch
        {
            // best-effort, like Record: the restore has happened, the contract says it does not throw
        }
        Record("RestoreDefaults", "alle", elapsed);
    }

    /// <summary>
    /// Logs the channels the backend could not put on hardware auto - once per change, since the fail-safe restores
    /// every tick while it holds.
    /// </summary>
    private void ReportRestoreProblems()
    {
        if (_inner is not IRestoreDiagnostics diagnostics)
            return;
        IReadOnlyList<string> problems = diagnostics.LastRestoreProblems;
        string key = string.Join("\n", problems);
        lock (_statGate)
        {
            if (key == _reportedRestoreProblems)
                return;
            _reportedRestoreProblems = key;
        }
        if (problems.Count == 0)
            _log.LogInformation("RestoreDefaults: alle Lüfter-Kanäle wieder auf Hardware-Auto.");
        foreach (string problem in problems)
            _log.LogError("RestoreDefaults: {Problem}", problem);
    }

    public void Dispose()
    {
        BlockManualWrites(); // the inner Dispose restores (if the daemon wrote); nothing may override that afterwards
        lock (_gate) _inner.Dispose();
    }

    /// <summary>
    /// Führt einen Schreib-Aufruf unter dem Gate aus und misst die reine Backend-Dauer. Der Stopwatch
    /// läuft NUR um den inneren Aufruf; das Loggen passiert AUSSERHALB des Locks - die Messung darf die
    /// Lock-Hold-Zeit (genau die hier untersuchte Größe) nicht selbst verlängern.
    /// </summary>
    private void TimedWrite(string op, string fan, Action write, bool drivesManual)
    {
        TimeSpan elapsed;
        EnterGate(op, fan);
        try
        {
            if (drivesManual && _manualWritesBlocked)
                throw new ManualWritesBlockedException(
                    $"{op} {fan} abgelehnt: der Daemon fährt herunter, nur der sichere Zustand wird noch geschrieben.");

            long start = Stopwatch.GetTimestamp();
            write();
            elapsed = Stopwatch.GetElapsedTime(start);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
        Record(op, fan, elapsed);
    }

    private void EnterGate(string op, string fan)
    {
        bool taken = false;
        Monitor.TryEnter(_gate, _gateStalled ? TimeSpan.Zero : _gateTimeout, ref taken);
        if (!taken)
        {
            _gateStalled = true;
            throw new TimeoutException(
                $"{op} {fan}: Hardware-Gate nicht frei (hängender Write?).");
        }
        _gateStalled = false;
    }

    private void Record(string op, string fan, TimeSpan elapsed)
    {
        // Reine Diagnose: ein scheiterndes Logging darf einen Steuer-/Fail-Safe-Write nie reißen - der
        // RestoreDefaults-Vertrag garantiert „wirft nicht", und der HW-Write ist hier ohnehin schon erfolgt.
        try
        {
            double ms = elapsed.TotalMilliseconds;
            _log.LogDebug("HW-Write {Op} {Fan}: {Ms:F1} ms", op, fan, ms);

            if (ms >= SlowWriteThresholdMs)
                _log.LogWarning(
                    "Langsamer Hardware-Write {Op} {Fan}: {Ms:F1} ms (≥ {Threshold} ms) - staut unter Last "
                    + "das Gate und kann ein Watchdog-RestoreDefaults verzögern.",
                    op, fan, ms, SlowWriteThresholdMs);

            bool newMax;
            lock (_statGate)
            {
                newMax = ms > _maxWriteMs;
                if (newMax)
                    _maxWriteMs = ms;
            }
            if (newMax)
                _log.LogInformation("Neue Max-Hardware-Write-Latenz {Op} {Fan}: {Ms:F1} ms.", op, fan, ms);
        }
        catch
        {
            // best-effort: Mess-/Logging-Fehler werden verschluckt, der Steuerpfad läuft unbeeinträchtigt weiter.
        }
    }
}
