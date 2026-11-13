// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using LinFan.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Verdrahtungs-Test für den Daemon-Start. Stellt sicher, dass der DI-Container alles auflöst, was
/// <see cref="ControlLoopService"/> per Konstruktor braucht - insbesondere <see cref="IIpcServer"/>.
/// Fehlt diese Bindung, stürzt der privilegierte Daemon beim Start ab (kein Watchdog, keine Regelung,
/// Hardware unbeaufsichtigt). Die Service-Tests umgehen den Container (FakeIpcServer direkt im Ctor)
/// und würden einen solchen Wiring-Bruch nicht bemerken - dieser Test schließt die Lücke.
/// </summary>
public class DaemonHostTests
{
    [Fact]
    public async Task ConfigureServices_ControlLoopServiceIsActivatableWithIIpcServer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<ControlLoopService>>(NullLogger<ControlLoopService>.Instance);
        var hw = new FakeHardware();
        DaemonHost.ConfigureServices(services, hw, hw);

        // IpcServer ist nur IAsyncDisposable -> Provider asynchron entsorgen (wie der Generic Host).
        await using ServiceProvider sp = services.BuildServiceProvider();

        // IIpcServer muss aufgelöst werden und dieselbe Instanz wie die konkrete IpcServer sein
        // (der Host disposed genau diese eine Instanz).
        Assert.NotNull(sp.GetRequiredService<IIpcServer>());
        Assert.Same(sp.GetRequiredService<IpcServer>(), sp.GetRequiredService<IIpcServer>());

        // ControlLoopService muss mit allen Ctor-Abhängigkeiten aus dem Container aktivierbar sein.
        using var svc = ActivatorUtilities.CreateInstance<ControlLoopService>(sp);
        Assert.NotNull(svc);
    }

    /// <summary>
    /// The units set WatchdogSec=: if the hosted service did not get the notifier AddSystemd registers, nothing would
    /// report WATCHDOG=1 and systemd would kill the daemon every 30 s. Read from the field, not by starting the service -
    /// that would bind the default socket of the daemon running on this host.
    /// </summary>
    [Fact]
    public async Task ConfigureServices_HandsTheSystemdNotifierToTheControlLoop()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<ControlLoopService>>(NullLogger<ControlLoopService>.Instance);
        var notifier = new NullNotifier();
        services.AddSingleton<ISystemdNotifier>(notifier); // what AddSystemd registers under systemd
        var hw = new FakeHardware();
        DaemonHost.ConfigureServices(services, hw, hw);
        await using ServiceProvider sp = services.BuildServiceProvider();

        ControlLoopService svc = sp.GetServices<IHostedService>().OfType<ControlLoopService>().Single();

        object? injected = typeof(ControlLoopService)
            .GetField("_systemd", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(svc);
        Assert.Same(notifier, injected);
    }

    private sealed class NullNotifier : ISystemdNotifier
    {
        public bool IsEnabled => false;
        public void Notify(ServiceState state) { }
    }
}
