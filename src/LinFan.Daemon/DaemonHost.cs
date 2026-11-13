// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using LinFan.Core.Abstractions;
using LinFan.Core.Services;
using LinFan.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace LinFan.Daemon;

/// <summary>
/// Generic-Host für den Dauerbetrieb (<c>run</c>): registriert Backend, ConfigStore und den
/// <see cref="ControlLoopService"/>. Vorstufe zum späteren systemd-Dienst mit IPC-Server (Teil 3).
/// </summary>
internal static class DaemonHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Set on every way out, after the host and its services are disposed: a closing console window holds its signal
        // handler until then (see ConsoleCloseHold). Never disposed - the handler may still be waiting on it.
        var finished = new ManualResetEventSlim();
        try
        {
            return await RunCoreAsync(args, finished);
        }
        finally
        {
            finished.Set();
        }
    }

    private static async Task<int> RunCoreAsync(string[] args, ManualResetEventSlim finished)
    {
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
        catch (Exception ex)
        {
            // A backend that cannot start (driver or hardware library failing) ends with an exit code like a failed
            // start below, not with an unhandled exception and its core dump. No fan was written yet.
            Console.Error.WriteLine($"LinFan: Hardware-Backend nicht startbar: {ex.Message}");
            return 1;
        }

        var builder = Host.CreateApplicationBuilder(args);

        // Diagnose-Datei-Log neben der Konfiguration (v. a. für Windows, wo der Dienst nur ins Event-Log
        // schreibt). Zusätzlich zu den Default-Providern (Console/EventLog); über LINFAN_LOG abschaltbar.
        if (FileLoggerProvider.ResolveLogPath() is { } logPath)
            builder.Logging.AddProvider(new FileLoggerProvider(logPath));

        // The console logger queues records for a writer thread; once the queue is full it would block the caller -
        // the control loop logs every tick, and a stalled stdout (journald backed up) must not stall the watchdog.
        builder.Services.Configure<ConsoleLoggerOptions>(o => o.QueueFullMode = ConsoleLoggerQueueFullMode.DropWrite);

        // Als Dienst integrieren: Windows-Service- bzw. systemd-Lebenszyklus. Beide sind No-op, wenn der
        // Prozess nicht unter dem jeweiligen Dienstmanager läuft (z. B. interaktiv per `run`) - also unbedingt
        // aufrufbar, der Konsolenbetrieb bleibt unverändert.
        // Same name as in packaging/windows/Install-LinFan.ps1; the GUI's offline banner tells users to run
        // `Get-Service LinFan` (ConnectionController.DiagnosticCommandFor) - rename all three together.
        builder.Services.AddWindowsService(options => options.ServiceName = "LinFan");
        builder.Services.AddSystemd();

        // Below systemd's TimeoutStopSec=20 (the default 30 s is not): the host must finish stopping - and
        // with it the final fail-safe RestoreDefaults - before systemd SIGKILLs the daemon.
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));

        ConfigureServices(builder.Services, sensors, fans);

        using IHost host = builder.Build();
        // The host's own lifetimes miss signals that end a daemon run by hand: neither stops on SIGHUP (closed terminal,
        // dropped SSH session), and the systemd one - chosen whenever the parent process is systemd, also for a
        // `setsid`-started daemon - only on SIGTERM. Without a stop there is no restore: the fans would stay on their
        // last curve value with no watchdog. The unit sends none of these, so its stop stays SIGTERM.
        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            lifetime.StopApplication();
            // A closed console window (SIGHUP on Windows) ends the process as soon as this returns - before StopAsync has
            // handed the fans back. Wait for the shutdown instead (the service has no console, so it never gets here).
            ConsoleCloseHold.Hold(context, finished);
        }
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        using var sigquit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Stop);
        using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop);
        // The first record of every start, so a log that ends in a failed start still names the build.
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LinFan.Startup")
            .LogInformation("LinFan {Version}", BuildVersion.Current);
        LogStartupDiagnostics(host, sensors);
        // Asked here, not from the exception: whether the host rethrows a failed start or ends normally depends on how
        // it runs ExecuteAsync.
        ControlLoopService loop = host.Services.GetServices<IHostedService>().OfType<ControlLoopService>().Single();
        int exitCode = 0;
        try
        {
            await host.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopped (SIGTERM) while still starting
        }
        catch (Exception ex)
        {
            // A start that fails (another daemon holds the endpoint, an unusable socket path) has been logged by the
            // loop and the host. End with an exit code: an unhandled exception would abort with a core dump.
            Console.Error.WriteLine($"LinFan: {ex.Message}");
            exitCode = 1;
        }
        return loop.AnotherDaemonHoldsTheEndpoint ? AnotherDaemonExitCode : exitCode;
    }

    /// <summary>
    /// Exit code of a daemon that ended because another one holds the IPC endpoint. The systemd units name it in
    /// <c>RestartPreventExitStatus=</c> and skip their <c>ExecStopPost=</c> restore for it: the fans are the other's.
    /// </summary>
    internal const int AnotherDaemonExitCode = 3;

    /// <summary>
    /// Meldet eine etwaige Backend-Start-Diagnose (z. B. „nur GPU-Sensoren" → Treiber-Konflikt) ins Log -
    /// über <see cref="AddWindowsService"/> landet das im Windows-Event-Log, sonst auf journald/stderr.
    /// Opt-in per Pattern-Match, damit keine Plattform-Logik in den neutralen Daemon leckt.
    /// </summary>
    private static void LogStartupDiagnostics(IHost host, ISensorBackend sensors)
    {
        if (sensors is not IBackendDiagnostics diagnostics)
            return;

        var loggers = host.Services.GetRequiredService<ILoggerFactory>();
        if (diagnostics.StartupWarning is { } warning)
            loggers.CreateLogger("LinFan.Startup").LogWarning("{Warning}", warning);

        // Subscribed before RunAsync, so a condition found by the start-restore is logged too.
        ILogger runtime = loggers.CreateLogger("LinFan.Backend");
        diagnostics.RuntimeWarning += message => runtime.LogWarning("{Warning}", message);
    }

    /// <summary>
    /// Registriert Backend, ConfigStore, IPC-Server und den <see cref="ControlLoopService"/>.
    /// Eigene Methode, damit ein Test die DI-Verdrahtung prüfen kann (insbesondere, dass
    /// <see cref="IIpcServer"/> auflösbar ist) - ohne den Host tatsächlich zu starten.
    /// </summary>
    internal static void ConfigureServices(IServiceCollection services, ISensorBackend sensors, IFanController fans)
    {
        services.AddSingleton(sensors);                       // Backend (Lesen)

        // Backend (Steuern) thread-sicher kapseln: Regel-Loop und Kalibrierung laufen auf verschiedenen
        // Threads. Über die DI-Factory, damit der Wrapper einen ILogger für die Write-Latenz-Messung bekommt
        // (fehlt die Bindung - z. B. im Verdrahtungs-Test - fällt er auf NullLogger zurück).
        services.AddSingleton<IFanController>(sp =>
            new SynchronizedFanController(fans, sp.GetService<ILogger<SynchronizedFanController>>()));
        services.AddSingleton<IConfigStore>(new JsonConfigStore());
        // Logger durchreichen: der privilegierte IPC-Server braucht eine Audit-/Fehler-Spur (DoS-/
        // Fehlkommando-Diagnose). Ohne Bindung (Verdrahtungs-Test) fällt er auf NullLogger zurück.
        services.AddSingleton(sp => new IpcServer(log: sp.GetService<ILogger<IpcServer>>())); // Unix-Socket-Server (vom Host disposed)
        services.AddSingleton<IIpcServer>(sp => sp.GetRequiredService<IpcServer>()); // dieselbe Instanz hinter dem Interface
        services.AddHostedService<ControlLoopService>();
    }
}
