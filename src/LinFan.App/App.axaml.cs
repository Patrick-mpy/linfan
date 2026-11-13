// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.App.Views;

namespace LinFan.App;

public partial class App : Application
{
    private SettingsController? _appearance; // whose theme and accent are on screen (ApplyAppearance)
    private ResourceDictionary? _accentOverride;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Before any window, in every lifetime (the headless UI tests too): windows carry the class of the theme they show.
        WindowThemeClasses.AttachAll();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // MVC: die View bekommt ihren Controller als DataContext.
            var controller = new MainController();
            InstallErrorBarrier(controller);

            // Persistierte Sprache, Theme und Akzent anwenden, bevor das Fenster zum ersten Mal zeichnet, und
            // bei jeder späteren Änderung live nachziehen.
            ApplyCulture(controller.Settings.Language);
            ApplyAppearance(controller.Settings);

            // Must run before the first window: the class handler pulls the native title bar of every
            // window opened afterwards onto the theme variant.
            WindowFrameTheme.AttachAll();
            controller.Settings.PropertyChanged += (_, e) =>
            {
                // The own colour only matters while it is the chosen accent.
                if (e.PropertyName is nameof(SettingsController.Theme) or nameof(SettingsController.Accent)
                    || e.PropertyName == nameof(SettingsController.AccentCustom) && controller.Settings.Accent == AccentChoice.Custom)
                    ApplyAppearance(controller.Settings);
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

    /// <summary>
    /// An exception nobody caught on the UI thread would end the GUI - with the user's unsaved edits; the fans are the
    /// daemon's either way. Instead it is recorded and the user told, since a view may be left half updated. A task
    /// nobody awaited only surfaces when the GC finalizes it, often unrelated to anything on screen and possibly inside
    /// a library (D-Bus, tray): recorded, no notice. What ends the process anyway is recorded in <see cref="Program"/>.
    /// </summary>
    private static void InstallErrorBarrier(MainController controller)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            controller.ReportUnhandled(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            controller.RecordError("Task", "Unobserved task exception", e.Exception);
        };
    }

    /// <summary>
    /// Puts theme and accent colour on screen: the theme variant, the accent tokens after the theme dictionaries, the
    /// accent of Fluent's palettes (<see cref="AccentResources"/>) and, back to the settings, which theme that is - for
    /// "System" the OS decides, also later on.
    /// </summary>
    internal void ApplyAppearance(SettingsController settings)
    {
        if (_appearance is null)
            ActualThemeVariantChanged += OnActualThemeVariantChanged;
        _appearance = settings;

        if (_accentOverride is not null)
            Resources.MergedDictionaries.Remove(_accentOverride);
        _accentOverride = AccentResources.Build(settings.Accent, settings.AccentCustom);
        if (_accentOverride is not null)
            Resources.MergedDictionaries.Add(_accentOverride);

        (Color light, Color dark) = AccentResources.FluentAccents(settings.Theme, settings.Accent, settings.AccentCustom);
        FluentTheme fluent = Styles.OfType<FluentTheme>().Single();
        fluent.Palettes[ThemeVariant.Light].Accent = light;
        fluent.Palettes[ThemeVariant.Dark].Accent = dark;

        RequestedThemeVariant = ThemeVariantMap.ToVariant(settings.Theme);
        settings.ActiveTheme = ThemeVariantMap.Concrete(ActualThemeVariant);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (_appearance is not null)
            _appearance.ActiveTheme = ThemeVariantMap.Concrete(ActualThemeVariant);
    }

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
