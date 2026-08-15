// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.App.Views;

namespace LinFan.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // MVC: die View bekommt ihren Controller als DataContext.
            var controller = new MainController();

            // Persistierte Sprache und Theme anwenden, bevor das Fenster zum ersten Mal zeichnet, und
            // bei jeder späteren Änderung im Header live nachziehen.
            ApplyCulture(controller.Settings.Language);
            ApplyTheme(controller.Settings.Theme);

            // Must run before the first window: the class handler pulls the native title bar of every
            // window opened afterwards onto the theme variant.
            WindowFrameTheme.AttachAll();
            controller.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsController.Theme))
                    ApplyTheme(controller.Settings.Theme);
                else if (e.PropertyName == nameof(SettingsController.Language))
                    ApplyCulture(controller.Settings.Language);
            };

            var window = new MainWindow { DataContext = controller };
            desktop.MainWindow = window;
            TrySetupTray(window);

            // --minimized (Login-Autostart): mit Tray das Fenster direkt nach dem ersten Öffnen verstecken -
            // das Lifetime ruft Show() nach dieser Methode selbst auf, ein früheres Hide() würde überschrieben.
            // Ohne Tray-Backend wäre ein verstecktes Fenster unerreichbar → dann nur minimiert starten.
            if (desktop.Args?.Contains("--minimized") == true)
            {
                if (window.TrayAvailable)
                {
                    EventHandler? hideOnce = null;
                    hideOnce = (_, _) =>
                    {
                        window.Opened -= hideOnce; // nur der Start - spätere Show() (Tray-Klick) nicht wieder verstecken
                        window.Hide();
                    };
                    window.Opened += hideOnce;
                }
                else
                {
                    window.WindowState = WindowState.Minimized;
                }
            }

            // Further launches (desktop icon, start menu, autostart) do not start a second GUI - they
            // reach this instance instead, which shows itself, including back out of the tray.
            Program.Instance?.ListenForActivation(() => Dispatcher.UIThread.Post(() => ShowWindow(window)));

            // Einmaliger, additiver Update-Check (GitHub-Release) - nach dem Fenster-Setup, best-effort/still.
            controller.BeginUpdateCheck();

            // Beenden auf Wunsch des Systems (Abmelden/Herunterfahren, oder ein Setup, das die App über den
            // Restart Manager schließt): erst das Fenster wirklich schließen lassen - ins Tray zu minimieren
            // hieße hier, die Anfrage abzulehnen -, dann den Poll-Loop sauber stoppen.
            desktop.ShutdownRequested += (_, _) =>
            {
                window.PrepareForShutdown();
                controller.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(ThemeChoice choice) =>
        RequestedThemeVariant = ThemeVariantMap.ToVariant(choice);

    private static void ApplyCulture(LanguageChoice choice) =>
        Localizer.Instance.SetLanguage(choice);

    /// <summary>
    /// Erstellt das Tray-Icon programmatisch - bewusst nicht in App.axaml, sonst würde die Headless-Test-App
    /// es mitladen. Auf Desktops ohne Tray-Backend schlägt das Erzeugen fehl; dann läuft die App ohne Tray
    /// weiter und „In den Tray minimieren" greift nicht (das Fenster schließt normal).
    /// </summary>
    private void TrySetupTray(MainWindow window)
    {
        try
        {
            var show = new NativeMenuItem(Localizer.Instance["Tray.ShowWindow"]);
            show.Click += (_, _) => ShowWindow(window);
            var quit = new NativeMenuItem(Localizer.Instance["Tray.Quit"]);
            quit.Click += (_, _) => window.RequestQuit();

            // Das Tray-Menü wird nur einmal erstellt; seine Beschriftungen bei Sprachwechsel live nachziehen.
            Localizer.Instance.PropertyChanged += (_, _) =>
            {
                show.Header = Localizer.Instance["Tray.ShowWindow"];
                quit.Header = Localizer.Instance["Tray.Quit"];
            };

            var menu = new NativeMenu();
            menu.Items.Add(show);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            var tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LinFan.App/Assets/icon.png"))),
                ToolTipText = "LinFan",
                Menu = menu,
            };
            tray.Clicked += (_, _) => ShowWindow(window);

            TrayIcon.SetIcons(this, new TrayIcons { tray });
            window.TrayAvailable = true;
        }
        catch
        {
            // Kein Tray-Backend (z. B. fehlender StatusNotifier) → ohne Tray weiterlaufen.
        }
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
