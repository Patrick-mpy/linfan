// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Tests für den fail-safe-kritischen Serialisierungs-Wrapper: die Schreib-Aufrufe müssen weiterhin
/// 1:1 ans Backend delegieren (insbesondere der <c>RestoreDefaults</c>-Pfad), und die Write-Latenz-
/// Messung muss langsame Writes als Warnung melden (Mess-Hilfe für die Async-Queue-Entscheidung,
/// todo.md: <c>SynchronizedFanController</c>).
/// </summary>
public class SynchronizedFanControllerTests
{
    [Fact]
    public void SetPwm_DelegatesToInner()
    {
        var inner = new RecordingController(TimeSpan.Zero);
        var sut = new SynchronizedFanController(inner, new CapturingLogger());

        sut.SetPwm(new FanId("fan0"), 128);

        Assert.Equal(("fan0", (byte)128), Assert.Single(inner.Writes));
    }

    [Fact]
    public void RestoreDefaults_DelegatesToInner()
    {
        var inner = new RecordingController(TimeSpan.Zero);
        var sut = new SynchronizedFanController(inner, new CapturingLogger());

        sut.RestoreDefaults();

        Assert.Equal(1, inner.RestoreCount);
    }

    [Fact]
    public async Task RestoreDefaults_DoesNotDeadlock_WhenAnotherWriteIsWedged()
    {
        // Ein hängender Write (z. B. aus dem Kalibrier-/Identify-Thread) hält das Gate. Der Fail-Safe-
        // RestoreDefaults aus dem Loop-Thread darf darauf NICHT unbegrenzt blockieren - sonst ist der
        // einzige Rückfall-Mechanismus selbst deadlockbar. Er wartet begrenzt und schreibt am Gate vorbei.
        var inner = new WedgingController();
        var log = new CapturingLogger();
        var sut = new SynchronizedFanController(inner, log);

        var wedged = new Thread(() => sut.SetPwm(new FanId("fan0"), 128)) { IsBackground = true };
        wedged.Start();
        Assert.True(inner.Entered.Wait(TimeSpan.FromSeconds(5)),
            "Der blockierende Write hat das Gate nicht rechtzeitig betreten.");

        // Muss zurückkehren, obwohl der andere Thread das Gate weiter hält (sonst hinge der Test hier).
        var restore = Task.Run(sut.RestoreDefaults);
        try
        {
            await restore.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Assert.Fail("RestoreDefaults blockierte hinter dem hängenden Write (Deadlock).");
        }

        Assert.Equal(1, inner.RestoreCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("am Gate vorbei"));

        inner.Release.Set(); // den hängenden Write auflösen, damit der Hintergrund-Thread sauber endet
        wedged.Join(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FastWrite_DoesNotWarn()
    {
        var inner = new RecordingController(TimeSpan.Zero);
        var log = new CapturingLogger();
        var sut = new SynchronizedFanController(inner, log);

        sut.SetPwm(new FanId("fan0"), 50);

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void SlowWrite_LogsWarningAndNewMax()
    {
        // 60 ms ≥ Schwelle (50 ms) und garantiert > Start-Maximum (0) - beide Pfade feuern deterministisch.
        var inner = new RecordingController(TimeSpan.FromMilliseconds(60));
        var log = new CapturingLogger();
        var sut = new SynchronizedFanController(inner, log);

        sut.SetPwm(new FanId("fan0"), 200);

        Assert.Equal(("fan0", (byte)200), Assert.Single(inner.Writes));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Langsamer Hardware-Write"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Max-Hardware-Write-Latenz"));
    }

    [Fact]
    public void BlockManualWrites_RejectsManualWrites_ButStillRestoresTheSafeState()
    {
        var inner = new RecordingController(TimeSpan.Zero);
        var sut = new SynchronizedFanController(inner, new CapturingLogger());

        sut.BlockManualWrites();

        Assert.Throws<ManualWritesBlockedException>(() => sut.SetPwm(new FanId("fan0"), 0));
        Assert.Throws<ManualWritesBlockedException>(() => sut.SetMode(new FanId("fan0"), FanMode.Manual));
        Assert.Empty(inner.Writes);
        Assert.Empty(inner.Modes);

        sut.SetMode(new FanId("fan0"), FanMode.Auto);
        sut.RestoreDefaults();
        Assert.Equal(FanMode.Auto, Assert.Single(inner.Modes));
        Assert.Equal(1, inner.RestoreCount);
    }

    [Fact]
    public void Dispose_BlocksManualWrites()
    {
        var inner = new RecordingController(TimeSpan.Zero);
        var sut = new SynchronizedFanController(inner, new CapturingLogger());

        sut.Dispose();

        Assert.Throws<ManualWritesBlockedException>(() => sut.SetPwm(new FanId("fan0"), 0));
        Assert.Empty(inner.Writes);
    }

    /// <summary>
    /// A write wedged in the driver holds the gate. Every other read and write gives up after a bounded wait instead of
    /// stalling its caller - the control loop among them, and with it the over-temperature watchdog. Discovery and
    /// CanControl need no gate at all, so a stall cannot make a fan look uncontrollable.
    /// </summary>
    [Fact]
    public void ReadsAndWrites_GiveUp_WhileTheGateIsWedged()
    {
        var inner = new WedgingController();
        var sut = new SynchronizedFanController(inner, new CapturingLogger(), gateTimeout: TimeSpan.FromMilliseconds(50));
        var fan = new FanId("fan0");
        var wedged = new Thread(() => sut.SetPwm(fan, 128)) { IsBackground = true };
        wedged.Start();
        Assert.True(inner.Entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Throws<TimeoutException>(() => sut.GetPwm(fan));
            Assert.Throws<TimeoutException>(() => sut.GetMode(fan));
            Assert.Throws<TimeoutException>(() => sut.SetPwm(fan, 10));
            Assert.Throws<TimeoutException>(() => sut.SetMode(fan, FanMode.Auto));
            Assert.True(sut.CanControl(fan));
            Assert.Empty(sut.DiscoverFans());
        }
        finally
        {
            inner.Release.Set();
            wedged.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>The fail-safe restores every tick while it holds: a channel that will not go back is logged once.</summary>
    [Fact]
    public void RestoreProblems_AreLoggedOncePerChange()
    {
        var inner = new ReportingController();
        var log = new CapturingLogger();
        var sut = new SynchronizedFanController(inner, log);

        inner.Problems = ["thinkpad/pwm1: Hardware-Auto nicht setzbar (EIO) - stattdessen Volllast."];
        for (int i = 0; i < 3; i++)
            sut.RestoreDefaults();
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("thinkpad/pwm1"));

        inner.Problems = [];
        sut.RestoreDefaults();
        sut.RestoreDefaults();
        Assert.Single(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("wieder auf Hardware-Auto"));

        inner.Problems = ["thinkpad/pwm1: weder Hardware-Auto (EIO) noch Volllast (EIO) setzbar."];
        sut.RestoreDefaults();
        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Error && e.Message.Contains("thinkpad/pwm1")));
    }

    private sealed class ReportingController : IFanController, IRestoreDiagnostics
    {
        public IReadOnlyList<string> Problems { get; set; } = [];
        public IReadOnlyList<string> LastRestoreProblems { get; private set; } = [];

        public void RestoreDefaults() => LastRestoreProblems = Problems;
        public void SetPwm(FanId id, byte value) { }
        public void SetMode(FanId id, FanMode mode) { }
        public IReadOnlyList<FanDescriptor> DiscoverFans() => Array.Empty<FanDescriptor>();
        public bool CanControl(FanId id) => true;
        public FanMode GetMode(FanId id) => FanMode.Auto;
        public byte GetPwm(FanId id) => 0;
        public void Dispose() { }
    }

    /// <summary>Minimaler <see cref="IFanController"/>: protokolliert Writes und verzögert optional (langsames Backend).</summary>
    private sealed class RecordingController : IFanController
    {
        private readonly TimeSpan _delay;
        public RecordingController(TimeSpan delay) => _delay = delay;

        public List<(string Fan, byte Pwm)> Writes { get; } = new();
        public int RestoreCount { get; private set; }

        public void SetPwm(FanId id, byte value)
        {
            Thread.Sleep(_delay);
            Writes.Add((id.Value, value));
        }

        public void RestoreDefaults()
        {
            Thread.Sleep(_delay);
            RestoreCount++;
        }

        public List<FanMode> Modes { get; } = new();

        public void SetMode(FanId id, FanMode mode)
        {
            Thread.Sleep(_delay);
            Modes.Add(mode);
        }

        public IReadOnlyList<FanDescriptor> DiscoverFans() => Array.Empty<FanDescriptor>();
        public bool CanControl(FanId id) => true;
        public FanMode GetMode(FanId id) => FanMode.Manual;
        public byte GetPwm(FanId id) => 0;
        public void Dispose() { }
    }

    /// <summary>
    /// <see cref="IFanController"/>, dessen <c>SetPwm</c> unbegrenzt blockiert (simuliert einen im EC/Treiber
    /// festhängenden sysfs-Write, der das Gate hält). <c>Entered</c> signalisiert, dass der Write das Gate
    /// betreten hat; <c>Release</c> löst ihn wieder auf (Test-Aufräumen).
    /// </summary>
    private sealed class WedgingController : IFanController
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        private int _restoreCount;
        public int RestoreCount => Volatile.Read(ref _restoreCount);

        public void SetPwm(FanId id, byte value)
        {
            Entered.Set();
            Release.Wait();
        }

        public void RestoreDefaults() => Interlocked.Increment(ref _restoreCount);
        public void SetMode(FanId id, FanMode mode) { }
        public IReadOnlyList<FanDescriptor> DiscoverFans() => Array.Empty<FanDescriptor>();
        public bool CanControl(FanId id) => true;
        public FanMode GetMode(FanId id) => FanMode.Manual;
        public byte GetPwm(FanId id) => 0;
        public void Dispose() { }
    }

    /// <summary>Sammelt Log-Einträge (Level + gerenderte Nachricht) für Assertions.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
