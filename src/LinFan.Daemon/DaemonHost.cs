// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Services;
using LinFan.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinFan.Daemon;

/// <summary>
/// Generic-Host für den Dauerbetrieb (<c>run</c>): registriert Backend, ConfigStore und den
/// <see cref="ControlLoopService"/>. Vorstufe zum späteren systemd-Dienst mit IPC-Server (Teil 3).
/// </summary>
internal static class DaemonHost
{
    public static async Task<int> RunAsync(string[] args)
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

        var builder = Host.CreateApplicationBuilder(args);

        // Diagnose-Datei-Log neben der Konfiguration (v. a. für Windows, wo der Dienst nur ins Event-Log
        // schreibt). Zusätzlich zu den Default-Providern (Console/EventLog); über LINFAN_LOG abschaltbar.
        if (FileLoggerProvider.ResolveLogPath() is { } logPath)
            builder.Logging.AddProvider(new FileLoggerProvider(logPath));

        // Als Dienst integrieren: Windows-Service- bzw. systemd-Lebenszyklus. Beide sind No-op, wenn der
        // Prozess nicht unter dem jeweiligen Dienstmanager läuft (z. B. interaktiv per `run`) — also unbedingt
        // aufrufbar, der Konsolenbetrieb bleibt unverändert.
        builder.Services.AddWindowsService(options => options.ServiceName = "LinFan");
        builder.Services.AddSystemd();

        ConfigureServices(builder.Services, sensors, fans);

        using IHost host = builder.Build();
        LogStartupDiagnostics(host, sensors);
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Meldet eine etwaige Backend-Start-Diagnose (z. B. „nur GPU-Sensoren" → Treiber-Konflikt) ins Log —
    /// über <see cref="AddWindowsService"/> landet das im Windows-Event-Log, sonst auf journald/stderr.
    /// Opt-in per Pattern-Match, damit keine Plattform-Logik in den neutralen Daemon leckt.
    /// </summary>
    private static void LogStartupDiagnostics(IHost host, ISensorBackend sensors)
    {
        if (sensors is IBackendDiagnostics { StartupWarning: { } warning })
            host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("LinFan.Startup")
                .LogWarning("{Warning}", warning);
    }

    /// <summary>
    /// Registriert Backend, ConfigStore, IPC-Server und den <see cref="ControlLoopService"/>.
    /// Eigene Methode, damit ein Test die DI-Verdrahtung prüfen kann (insbesondere, dass
    /// <see cref="IIpcServer"/> auflösbar ist) — ohne den Host tatsächlich zu starten.
    /// </summary>
    internal static void ConfigureServices(IServiceCollection services, ISensorBackend sensors, IFanController fans)
    {
        services.AddSingleton(sensors);                       // Backend (Lesen)

        // Backend (Steuern) thread-sicher kapseln: Regel-Loop und Kalibrierung laufen auf verschiedenen
        // Threads. Über die DI-Factory, damit der Wrapper einen ILogger für die Write-Latenz-Messung bekommt
        // (fehlt die Bindung — z. B. im Verdrahtungs-Test — fällt er auf NullLogger zurück).
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
