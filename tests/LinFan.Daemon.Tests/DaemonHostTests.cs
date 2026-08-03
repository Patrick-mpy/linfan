// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Verdrahtungs-Test für den Daemon-Start. Stellt sicher, dass der DI-Container alles auflöst, was
/// <see cref="ControlLoopService"/> per Konstruktor braucht — insbesondere <see cref="IIpcServer"/>.
/// Fehlt diese Bindung, stürzt der privilegierte Daemon beim Start ab (kein Watchdog, keine Regelung,
/// Hardware unbeaufsichtigt). Die Service-Tests umgehen den Container (FakeIpcServer direkt im Ctor)
/// und würden einen solchen Wiring-Bruch nicht bemerken — dieser Test schließt die Lücke.
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
}
