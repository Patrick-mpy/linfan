// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;
using LinFan.App.Services;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert die Einzelinstanz-Logik: der erste Start bekommt den Endpunkt, jeder weitere wird abgewiesen
/// und meldet stattdessen die laufende Instanz — und ein nach einem Absturz liegengebliebener Socket darf
/// den Start nicht blockieren. Läuft gegen einen eigenen Test-Endpunkt (Named Pipe bzw. Socket-Datei),
/// nie gegen den echten Benutzer-Endpunkt.
/// </summary>
public sealed class SingleInstanceGuardTests
{
    private static string TempEndpoint() => OperatingSystem.IsWindows()
        ? $"linfan-guardtest-{Guid.NewGuid():N}"
        : Path.Combine(Path.GetTempPath(), $"linfan-guardtest-{Guid.NewGuid():N}.sock");

    [Fact]
    public void First_start_owns_the_endpoint()
    {
        string endpoint = TempEndpoint();

        using SingleInstanceGuard? guard = SingleInstanceGuard.AcquireOrActivate(endpoint);

        Assert.NotNull(guard);
    }

    [Fact]
    public async Task Second_start_is_refused_and_activates_the_first()
    {
        string endpoint = TempEndpoint();
        using SingleInstanceGuard? first = SingleInstanceGuard.AcquireOrActivate(endpoint);
        Assert.NotNull(first);

        var activated = new TaskCompletionSource();
        first!.ListenForActivation(() => activated.TrySetResult());

        SingleInstanceGuard? second = SingleInstanceGuard.AcquireOrActivate(endpoint);

        Assert.Null(second); // der zweite Prozess beendet sich, statt eine zweite GUI zu bauen
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Every_further_start_activates_again()
    {
        string endpoint = TempEndpoint();
        using SingleInstanceGuard? first = SingleInstanceGuard.AcquireOrActivate(endpoint);
        Assert.NotNull(first);

        var activations = new SemaphoreSlim(0);
        first!.ListenForActivation(() => activations.Release());

        // Der Endpunkt muss nach der ersten Aktivierung wieder annehmen — sonst öffnet der dritte Start
        // doch ein zweites Fenster (auf Windows hält die Pipe genau eine Instanz und muss getrennt werden).
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(SingleInstanceGuard.AcquireOrActivate(endpoint));
            Assert.True(await activations.WaitAsync(TimeSpan.FromSeconds(10)), $"Aktivierung {i + 1} kam nicht an.");
        }
    }

    [Fact]
    public void Endpoint_is_released_on_dispose()
    {
        string endpoint = TempEndpoint();
        SingleInstanceGuard.AcquireOrActivate(endpoint)!.Dispose();

        using SingleInstanceGuard? again = SingleInstanceGuard.AcquireOrActivate(endpoint);

        Assert.NotNull(again);
    }

    // Der Unix-Zweig wird direkt gerufen, damit er auf jeder Plattform läuft (AF_UNIX gibt es unter
    // Windows seit Windows 10) — dasselbe Muster wie NamedPipeTransportTests, das die Windows-Pipe unter
    // Linux prüft. Sonst liefe der Zweig der Vorrang-Plattform nur in der CI.

    private static string TempSocketPath() =>
        Path.Combine(Path.GetTempPath(), $"linfan-guardtest-{Guid.NewGuid():N}.sock");

    [Fact]
    public async Task Unix_second_start_is_refused_and_activates_the_first()
    {
        string endpoint = TempSocketPath();
        using SingleInstanceGuard? first = SingleInstanceGuard.AcquireUnix(endpoint);
        Assert.NotNull(first);

        var activated = new TaskCompletionSource();
        first!.ListenForActivation(() => activated.TrySetResult());

        Assert.Null(SingleInstanceGuard.AcquireUnix(endpoint));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Stale_socket_file_does_not_block_the_start()
    {
        // Abgestürzte Instanz nachstellen: die Socket-Datei liegt da, aber niemand nimmt Verbindungen an.
        // Dafür wird der Socket gebunden und NICHT geschlossen — Dispose entfernt die Datei sofort wieder
        // (nur ein echter Absturz lässt sie liegen). Ohne Listen wird ein connect() abgewiesen: genau die
        // Signatur, an der der Guard die Leiche von einer laufenden Instanz unterscheidet.
        string endpoint = TempSocketPath();
        var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        dead.Bind(new UnixDomainSocketEndPoint(endpoint));

        using SingleInstanceGuard? guard = SingleInstanceGuard.AcquireUnix(endpoint);

        Assert.NotNull(guard); // die Datei wurde ersetzt, nicht als laufende Instanz missdeutet
        dead.Dispose();
    }

    // Die Unix-Ableitung wird über den Seam auf jeder Plattform geprüft: unter Windows läuft der Zweig
    // sonst nie, und genau dort schlug er zu (CI-Container ohne HOME) — siehe UnixSocketPath.

    [Fact]
    public void Unix_endpoint_prefers_the_runtime_dir()
    {
        string path = SingleInstanceGuard.UnixSocketPath("/run/user/1000", "/home/u/.config", uid: 1000);

        Assert.Equal(Path.Combine("/run/user/1000", "linfan-gui.sock"), path);
    }

    [Fact]
    public void Unix_endpoint_falls_back_to_the_config_dir()
    {
        string path = SingleInstanceGuard.UnixSocketPath(runtimeDir: null, "/home/u/.config", uid: 1000);

        Assert.Equal(Path.Combine("/home/u/.config", "linfan", "gui.sock"), path);
    }

    [Fact]
    public void Unix_endpoint_stays_absolute_without_a_home_directory()
    {
        // Ohne HOME liefert GetFolderPath einen leeren Pfad; ein relativer Endpunkt hinge am
        // Arbeitsverzeichnis und jeder Startort bekäme seine eigene „Einzel"-Instanz.
        string path = SingleInstanceGuard.UnixSocketPath(runtimeDir: null, appData: "", uid: 0);

        Assert.True(Path.IsPathRooted(path), $"Endpunkt ist relativ: {path}");
        Assert.Contains("linfan-gui-0", path); // uid im Namen: das Temp-Verzeichnis ist geteilt
    }

    [Fact]
    public void Default_endpoint_is_stable_within_a_user_session()
    {
        string endpoint = SingleInstanceGuard.DefaultEndpoint();

        Assert.False(string.IsNullOrWhiteSpace(endpoint));
        Assert.Equal(endpoint, SingleInstanceGuard.DefaultEndpoint()); // beide Prozesse müssen dasselbe ableiten
        if (!OperatingSystem.IsWindows())
        {
            // Absolut — sonst hinge der Endpunkt am Arbeitsverzeichnis und jeder Startort bekäme seine
            // eigene „Einzel"-Instanz. Ohne Home und ohne XDG_RUNTIME_DIR (CI-Container, Dienstkonten)
            // war genau das der Fall, weil GetFolderPath dann einen leeren Pfad liefert.
            Assert.True(Path.IsPathRooted(endpoint), $"Endpunkt ist relativ: {endpoint}");
        }
    }
}
