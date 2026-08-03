// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using LinFan.App.Tests;

// Registriert die Headless-Test-Anwendung für [AvaloniaFact]/[AvaloniaTheory]. Bewusst NICHT die echte
// LinFan.App.App: die startet in OnFrameworkInitializationCompleted den MainController samt IPC-Poll-Loop.
// Hier genügt eine minimale App mit FluentTheme, damit Window/Control ein Template und einen Visual-Tree
// bekommen (Grundlage der Attach/Detach-Tests).
[assembly: AvaloniaTestApplication(typeof(HeadlessTestApp))]

namespace LinFan.App.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
