// SPDX-License-Identifier: GPL-3.0-or-later

using System.Resources;
using Avalonia;

// Neutrale (Haupt-Assembly-)Resourcen sind Englisch: für en-Kulturen wird keine Satellite-Assembly
// gesucht, sondern direkt Strings.resx genutzt. Andere Kulturen fallen letztlich hierauf zurück.
[assembly: NeutralResourcesLanguage("en", UltimateResourceFallbackLocation.MainAssembly)]

namespace LinFan.App;

internal static class Program
{
    // Avalonia erwartet einen STA-Thread; vor BuildAvaloniaApp keine Avalonia-Typen anfassen.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
