// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Headless;
using LinFan.App;

// Headless-Test-App: lädt dieselbe echte App (App.axaml + FluentTheme) wie zur Laufzeit, aber ohne
// echtes Rendering. So sind die Tests ein ehrlicher Smoke der produktiven XAML/Bindings.
[assembly: AvaloniaTestApplication(typeof(LinFan.App.UiTests.TestAppBuilder))]

namespace LinFan.App.UiTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}
