// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Views;

/// <summary>
/// Hauptfenster. Code-Behind bleibt minimal (nur reine UI-Belange) — Logik liegt im Controller.
/// Zuständig hier: Onboarding-Dialog (folgt <see cref="MainController.Onboarding"/>), die
/// Unsaved-Nachfrage beim Schließen und das Merken der Fenster-Geometrie (<see cref="UiSettingsStore"/>).
/// </summary>
public partial class MainWindow : Window
{
    private readonly UiSettingsStore _settingsStore = new();
    private OnboardingWindow? _onboardingWindow;
    private bool _allowClose;
    private bool _quitRequested;

    /// <summary>Setzt die App, sobald ein Tray-Icon erfolgreich erstellt wurde. Ohne Tray wird nie ins Tray minimiert.</summary>
    internal bool TrayAvailable { get; set; }

    // Zuletzt bekannte Normal-Geometrie (weder maximiert noch minimiert). Die persistieren wir, damit ein
    // maximiert geschlossenes Fenster beim Entmaximieren eine sinnvolle Größe/Position zurückbekommt
    // (Maximiert/Minimiert liefern sonst eine unbrauchbare Geometrie).
    private double? _normalWidth;
    private double? _normalHeight;
    private PixelPoint? _normalPosition;

    public MainWindow()
    {
        InitializeComponent();
        RestoreGeometry(_settingsStore.Load());

        // Normal-Geometrie live mitführen, solange das Fenster im Normal-Zustand ist. Genau EINMAL für die
        // Fenster-Lebensdauer abonnieren (nicht in OnOpened): OnOpened feuert nach jedem Tray-Restore
        // (Show nach Hide) erneut, ein Abo dort summierte sich pro Restore zu Handler-Leaks. Beide Handler
        // sind ohne Screens nutzbar (anders als die Off-Screen-Zentrierung in OnOpened).
        PositionChanged += (_, _) => CaptureNormalGeometry();
        SizeChanged += (_, _) => CaptureNormalGeometry();

        // Tunnel so targets that mark the press handled (e.g. CurveChart's drag capture) cannot bypass
        // the click-away focus release below.
        AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Click-away focus release: Avalonia only moves keyboard focus when a press lands on a focusable
    /// control, so a click into empty space used to leave the focus (accent border, pending LostFocus
    /// commits) on the last input forever. If the press target has no focusable ancestor, the window
    /// takes focus itself — the previous control gets its LostFocus. Popup contents (dropdowns, flyouts,
    /// context menus) live in their own visual roots and never route through this window handler.
    /// </summary>
    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        for (Visual? v = e.Source as Visual; v is not null && !ReferenceEquals(v, this); v = v.GetVisualParent())
        {
            if (v is IInputElement { Focusable: true, IsEffectivelyEnabled: true })
                return; // the press moves focus by itself (TextBox, Button, ListBoxItem, ...)
        }
        if (FocusManager?.GetFocusedElement() is { } focused && !ReferenceEquals(focused, this))
            Focus();
    }

    /// <summary>Wendet die gespeicherte Geometrie an: Größe/Maximiert sofort, Position erst nach dem Anzeigen validiert.</summary>
    private void RestoreGeometry(UiSettings s)
    {
        // Normal-Geometrie vorbelegen, damit sie ein maximiert geschlossenes Fenster über Sessions überlebt.
        _normalWidth = s.Width;
        _normalHeight = s.Height;

        if (s.Width is > 0)
            Width = s.Width.Value;
        if (s.Height is > 0)
            Height = s.Height.Value;

        if (s.X is { } x && s.Y is { } y)
        {
            _normalPosition = new PixelPoint(x, y);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = _normalPosition.Value;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen; // erster Start: mittig
        }

        if (s.Maximized)
            WindowState = WindowState.Maximized;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Position erst jetzt prüfen — die Screens stehen erst nach dem Anzeigen bereit. Liegt das Fenster
        // off-screen (gespeicherter Monitor weg / Auflösung geändert), auf den Hauptbildschirm zentrieren.
        if (_normalPosition is { } pos && Screens is { } screens)
        {
            List<PixelRect> bounds = screens.All.Select(sc => sc.Bounds).ToList();
            var rect = new PixelRect(pos.X, pos.Y, (int)Width, (int)Height);
            if (bounds.Count > 0 && !WindowPlacement.IsOnAnyScreen(rect, bounds))
                CenterOnPrimary();
        }
    }

    private void CaptureNormalGeometry()
    {
        if (WindowState != WindowState.Normal)
            return;
        _normalWidth = Width;
        _normalHeight = Height;
        _normalPosition = Position;
    }

    private void CenterOnPrimary()
    {
        PixelRect area = Screens.Primary?.WorkingArea
                         ?? (Screens.All.Count > 0 ? Screens.All[0].Bounds : default);
        if (area.Width <= 0 || area.Height <= 0)
            return;
        int x = area.X + Math.Max(0, (area.Width - (int)Width) / 2);
        int y = area.Y + Math.Max(0, (area.Height - (int)Height) / 2);
        _normalPosition = new PixelPoint(x, y);
        Position = _normalPosition.Value;
    }

    /// <summary>Speichert die zuletzt bekannte Normal-Geometrie plus den Maximiert-Zustand (best-effort).</summary>
    private void PersistGeometry()
    {
        bool maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
            CaptureNormalGeometry();

        // Load-modify-write: erhält die separat gespeicherten UI-Prefs (Theme, Tray) derselben Datei.
        _settingsStore.Save(_settingsStore.Load() with
        {
            Width = _normalWidth,
            Height = _normalHeight,
            X = _normalPosition?.X,
            Y = _normalPosition?.Y,
            Maximized = maximized,
        });
    }

    /// <summary>Vom Tray-„Beenden" aufgerufen: echtes Schließen erzwingen (umgeht das Minimieren ins Tray).</summary>
    public void RequestQuit()
    {
        _quitRequested = true;
        Close();
    }

    private bool MinimizeToTrayEnabled =>
        DataContext is MainController controller && controller.Settings.MinimizeToTray;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // „In den Tray minimieren": das Schließen abfangen und das Fenster nur verstecken — außer es wurde
        // ein echtes Beenden angefordert (Tray) oder es läuft bereits ein bestätigtes Schließen (_allowClose).
        // Nur mit tatsächlich vorhandenem Tray, sonst würde das Fenster unerreichbar. Die ungespeicherten
        // Änderungen bleiben im Speicher; die Geometrie wird erst beim echten Beenden persistiert.
        if (!_quitRequested && !_allowClose && TrayAvailable && MinimizeToTrayEnabled)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Beim Schließen mit ungespeicherten Editor-Änderungen einmal nachfragen (Speichern/Verwerfen/Abbrechen).
        if (_allowClose || DataContext is not MainController controller || !controller.Editor.HasUnsavedChanges)
        {
            PersistGeometry(); // nur auf den Pfaden, die wirklich schließen
            return;
        }

        e.Cancel = true; // Schließen aufhalten, bis der Nutzer entschieden hat
        try
        {
            UnsavedChoice choice = await new UnsavedChangesDialog().ShowDialog<UnsavedChoice>(this);
            if (choice == UnsavedChoice.Cancel)
                return; // Fenster offen lassen — Geometrie NICHT speichern
            if (choice == UnsavedChoice.Save)
                await controller.Editor.SaveCommand.ExecuteAsync(null);
        }
        catch
        {
            // Defensive: ein unerwarteter Dialog-/Save-Fehler darf diesen async-void-Handler nicht
            // in einen unbehandelten Crash kippen — dann lieber schließen (Save geht nur über IPC,
            // kein Hardware-Zustand bleibt hängen).
        }

        _allowClose = true;
        Close(); // re-entrant: läuft erneut durch OnClosing → früher Pfad persistiert die Geometrie
    }

    // Bestätigung destruktiver Aktionen über einen modalen Dialog (statt der alten Aufklapp-Flyouts).
    // „Soll gefragt werden?" ist reine UI; die eigentliche Aktion bleibt ein Command auf dem Controller.

    private async void OnRevertChanges(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainController controller)
            return;
        // Revert stellt die Baseline wieder her → kein Auto-Save (wäre ein No-op gegen den gespeicherten Stand).
        await ConfirmThen(
            Localizer.Instance["Dialog.RevertTitle"],
            Localizer.Instance["Dialog.RevertMessage"],
            Localizer.Instance["MainWindow.Revert"],
            () => { controller.Editor.RevertCommand.Execute(null); return Task.CompletedTask; });
    }

    private async void OnDeleteProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainController controller)
            return;
        // Bestätigte Löschung persistiert sich selbst (bedingt) — siehe CurveEditorController.DeleteProfile.
        await ConfirmThen(
            Localizer.Instance["Dialog.DeleteProfileTitle"],
            Localizer.Instance["Dialog.DeleteProfileMessage"],
            Localizer.Instance["MainWindow.Delete"],
            () => controller.Editor.DeleteProfileCommand.ExecuteAsync(null));
    }

    private async void OnDeleteCurve(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainController controller)
            return;
        await ConfirmThen(
            Localizer.Instance["Dialog.DeleteCurveTitle"],
            Localizer.Instance["Dialog.DeleteCurveMessage"],
            Localizer.Instance["MainWindow.Delete"],
            () => controller.Editor.DeleteCurveCommand.ExecuteAsync(null));
    }

    // Positions-Modal je Lüfterzeile: die Zeile kommt aus dem DataContext des Buttons; das Ergebnis wird
    // zurück in die gebundene Location geschrieben (reine UI — wie ConfirmThen). Abbrechen lässt sie unberührt.
    private async void OnPickLocation(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FanAssignRow row)
            return;
        try
        {
            FanLocation? picked = await new FanLocationDialog(row.Name, row.Location.Value, row.Manual)
                .ShowDialog<FanLocation?>(this);
            if (picked is { } loc)
                row.Location = FanLocationOption.For(loc);
        }
        catch
        {
            // Defensive: ein Dialog-Fehler darf diesen async-void-Handler nicht in einen Crash kippen.
        }
    }

    // --- Sensor group chip: explicit view->VM commit instead of a TwoWay LostFocus binding. ---
    // Why: with UpdateSourceTrigger=LostFocus, the transient focus loss towards the suggestion popup
    // committed the typed PREFIX (not the clicked suggestion); the resulting refresh of the shared
    // suggestion list then rebuilt the open popup mid-click, cancelling the selection (todo: group
    // select bug). Committing only while the dropdown is CLOSED lets the AutoCompleteBox finish the
    // click first (its adapter writes the suggestion into Text), and DropDownClosed commits the final
    // value; free-typed text commits via LostFocus once the popup is closed.

    private static void CommitGroupChip(AutoCompleteBox box)
    {
        // Raw text only — normalization (trim, empty -> null) stays controller-side (SensorOption.ToConfig).
        if (box.DataContext is SensorOption row)
            row.Group = box.Text ?? "";
    }

    private void OnGroupChipDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is AutoCompleteBox box)
            CommitGroupChip(box);
    }

    private void OnGroupChipLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is AutoCompleteBox { IsDropDownOpen: false } box)
            CommitGroupChip(box);
    }

    // --- Sicherung: Export/Import über den StorageProvider (reine UI); die Logik liegt im BackupController. ---

    private static readonly FilePickerFileType JsonFileType = new("JSON") { Patterns = new[] { "*.json" } };

    private async void OnExportBackup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainController controller)
            return;
        try
        {
            IStorageProvider? storage = StorageProvider;
            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Localizer.Instance["Settings.Export"],
                SuggestedFileName = BackupController.DefaultFileName,
                DefaultExtension = "json",
                FileTypeChoices = new[] { JsonFileType },
            });
            if (file is null)
                return; // abgebrochen

            await using Stream stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(controller.Backup.BuildBackupJson());
            controller.Backup.ReportExported();
        }
        catch
        {
            // Defensive: ein Dialog-/Schreibfehler darf diesen async-void-Handler nicht in einen Crash kippen.
            controller.Backup.ReportExportFailed();
        }
    }

    private async void OnImportBackup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainController controller)
            return;
        try
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localizer.Instance["Settings.Import"],
                AllowMultiple = false,
                FileTypeFilter = new[] { JsonFileType },
            });
            if (files.Count == 0)
                return; // abgebrochen

            await using Stream stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            string json = await reader.ReadToEndAsync();
            await controller.Backup.ImportFromJsonAsync(json);
        }
        catch
        {
            controller.Backup.ReportImportReadFailed();
        }
    }

    private async void OnResetConfig(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainController controller)
            return;
        await ConfirmThen(
            Localizer.Instance["Settings.ResetConfirmTitle"],
            Localizer.Instance["Settings.ResetConfirmMessage"],
            Localizer.Instance["Settings.ResetConfirm"],
            () => controller.Backup.ResetAsync());
    }

    private async Task ConfirmThen(string title, string message, string confirmText, Func<Task> onConfirmed)
    {
        try
        {
            if (await new ConfirmDialog(title, message, confirmText).ShowDialog<bool>(this))
                await onConfirmed();
        }
        catch
        {
            // Defensive: ein Dialog-/Save-Fehler darf diesen async-void-Aufrufer nicht in einen Crash kippen.
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainController controller)
            controller.PropertyChanged += OnControllerPropertyChanged;
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainController.Onboarding))
            return;

        if (sender is not MainController controller)
            return;

        if (controller.Onboarding is { } onboarding)
        {
            if (_onboardingWindow is not null)
                return; // bereits offen

            _onboardingWindow = new OnboardingWindow { DataContext = onboarding };
            _onboardingWindow.Closed += (_, _) => _onboardingWindow = null;
            _ = _onboardingWindow.ShowDialog(this);
        }
        else
        {
            // Controller hat Onboarding auf null gesetzt (nach Skip/Finish) → Fenster schließen
            _onboardingWindow?.Close();
            _onboardingWindow = null;
        }
    }
}
