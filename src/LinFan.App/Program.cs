// SPDX-License-Identifier: GPL-3.0-or-later

using System.Resources;
using Avalonia;
using LinFan.App.Services;

// Neutrale (Haupt-Assembly-)Resourcen sind Englisch: für en-Kulturen wird keine Satellite-Assembly
// gesucht, sondern direkt Strings.resx genutzt. Andere Kulturen fallen letztlich hierauf zurück.
[assembly: NeutralResourcesLanguage("en", UltimateResourceFallbackLocation.MainAssembly)]

namespace LinFan.App;

internal static class Program
{
    /// <summary>
    /// The activation endpoint this process owns, handed to <see cref="App"/> — the lifetime lives here
    /// because the guard has to be settled before Avalonia exists. Null in the headless test apps, which
    /// construct <see cref="App"/> without going through <see cref="Main"/>.
    /// </summary>
    internal static SingleInstanceGuard? Instance { get; private set; }

    // Avalonia erwartet einen STA-Thread; vor BuildAvaloniaApp keine Avalonia-Typen anfassen.
    [STAThread]
    public static void Main(string[] args)
    {
        // Single instance: a further launch only wakes the running GUI and exits. Decided before any
        // Avalonia type is touched, so a second start never flashes a window or a tray icon.
        using SingleInstanceGuard? guard =
            SingleInstanceGuard.AcquireOrActivate(SingleInstanceGuard.DefaultEndpoint());
        if (guard is null)
            return;

        Instance = guard;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
