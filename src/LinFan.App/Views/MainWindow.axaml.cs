// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LinFan.App.Controllers;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Views;

/// <summary>
/// Hauptfenster. Code-Behind bleibt minimal (nur reine UI-Belange) - Logik liegt im Controller.
/// Zuständig hier: Onboarding-Dialog (folgt <see cref="MainController.Onboarding"/>), die
/// Unsaved-Nachfrage beim Schließen und das Merken der Fenster-Geometrie (<see cref="UiSettingsStore"/>).
/// </summary>
public partial class MainWindow : Window
{
    private readonly UiSettingsStore _settingsStore;
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

    public MainWindow() : this(new UiSettingsStore())
    {
    }

    /// <summary>Store-Injektion für Tests (wie <see cref="SettingsController"/>) - sonst schriebe jeder Test,
    /// der das Fenster schließt, die echte Geometrie des Nutzers um.</summary>
    internal MainWindow(UiSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
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
        // Tunnel: Ctrl+S has to act before the focused control - see OnSaveShortcut.
        AddHandler(KeyDownEvent, OnSaveShortcut, RoutingStrategies.Tunnel);
        // Tunnel: the list item would take Enter for itself - see OnCurveNavKeyDown.
        CurveNavList.AddHandler(KeyDownEvent, OnCurveNavKeyDown, RoutingStrategies.Tunnel);
        // The profile menu is built as it opens, so its counts and marks are current.
        if (ProfileButton.Flyout is PopupFlyoutBase profileMenu)
            profileMenu.Opening += (_, _) => (DataContext as MainController)?.RefreshProfileMenu();

        // Drag at the grip of the settings lists - see OnGripPointerPressed.
        AddHandler(PointerPressedEvent, OnGripPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnGripPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => ForgetGripPress(), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragEnterEvent, OnSettingsDragOver);
        AddHandler(DragDrop.DragOverEvent, OnSettingsDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnSettingsDragLeave);
        AddHandler(DragDrop.DropEvent, OnSettingsDrop);
    }

    private static readonly KeyGesture SaveGesture = new(Key.S, KeyModifiers.Control);
    private static readonly KeyGesture UndoGesture = new(Key.Z, KeyModifiers.Control);
    private static readonly KeyGesture[] RedoGestures =
    {
        new(Key.Y, KeyModifiers.Control),
        new(Key.Z, KeyModifiers.Control | KeyModifiers.Shift),
    };

    /// <summary>
    /// Ctrl+S saves from anywhere, text fields included (while something is unsaved and the service is reachable -
    /// the command's CanExecute). The focus moves to the window first, so a field that commits on LostFocus writes
    /// its last value before the save, and returns afterwards so typing can go on.
    /// </summary>
    private void OnSaveShortcut(object? sender, KeyEventArgs e)
    {
        if (!SaveGesture.Matches(e) || DataContext is not MainController controller)
            return;
        e.Handled = true;

        IInputElement? focused = FocusManager?.GetFocusedElement();
        bool moveFocus = focused is not null && !ReferenceEquals(focused, this);
        if (moveFocus)
            Focus();
        if (controller.Editor.SaveCommand.CanExecute(null))
            controller.Editor.SaveCommand.Execute(null);
        if (moveFocus)
            focused!.Focus();
    }

    /// <summary>
    /// Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z step through the editor history - but only when the focused control left the
    /// key unhandled: a text box keeps its own undo, a stepper box (IsUndoEnabled=False) passes it on. Window key
    /// bindings cannot do this; Avalonia evaluates them before the focused control sees the key.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainController controller)
            return;

        System.Windows.Input.ICommand? command = UndoGesture.Matches(e) ? controller.Editor.UndoCommand
            : RedoGestures.Any(g => g.Matches(e)) ? controller.Editor.RedoCommand
            : null;
        if (command is null)
            return;
        e.Handled = true;
        if (command.CanExecute(null))
            command.Execute(null);
    }

    /// <summary>
    /// Click-away focus release: Avalonia only moves keyboard focus when a press lands on a focusable
    /// control, so a click into empty space used to leave the focus (accent border, pending LostFocus
    /// commits) on the last input forever. If the press target has no focusable ancestor, the window
    /// takes focus itself - the previous control gets its LostFocus. Popup contents (dropdowns, flyouts,
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

        // Position erst jetzt prüfen - die Screens stehen erst nach dem Anzeigen bereit. Liegt das Fenster
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

    /// <summary>
    /// The system asked the app to go away - logoff/shutdown, or an installer closing it through the
    /// Windows Restart Manager. From here on a close really closes: neither the minimize-to-tray path nor
    /// the unsaved-changes dialog may cancel it. Cancelling would answer the request with "no": the OS
    /// aborts the logoff, and the setup stops with "unable to automatically close all applications" while
    /// this process still holds the files it is about to replace. Unsaved editor changes are lost, same as
    /// in any other app the system closes - they only ever lived in the GUI, the daemon keeps running with
    /// the last saved config either way.
    /// </summary>
    public void PrepareForShutdown() => _allowClose = true;

    private bool MinimizeToTrayEnabled =>
        DataContext is MainController controller && controller.Settings.MinimizeToTray;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // „In den Tray minimieren": das Schließen abfangen und das Fenster nur verstecken - außer es wurde
        // ein echtes Beenden angefordert (Tray) oder es läuft bereits ein bestätigtes Schließen (_allowClose).
        // Nur mit tatsächlich vorhandenem Tray, sonst würde das Fenster unerreichbar. Die ungespeicherten
        // Änderungen bleiben im Speicher; die Geometrie wird erst beim echten Beenden persistiert.
        if (!_quitRequested && !_allowClose && TrayAvailable && MinimizeToTrayEnabled)
        {
            e.Cancel = true;
            (DataContext as MainController)?.OnHiddenToTray();
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
                return; // Fenster offen lassen - Geometrie NICHT speichern
            if (choice == UnsavedChoice.Save)
                await controller.Editor.SaveCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // The window closes anyway: a failed dialog or save must not keep it open (saving only goes through IPC, no
            // fan is left behind). Recorded - nobody would see a notice any more.
            controller.RecordError("Closing", "Asking about or saving the unsaved changes failed - closing anyway", ex);
        }

        _allowClose = true;
        Close(); // re-entrant: läuft erneut durch OnClosing → früher Pfad persistiert die Geometrie
    }

    // Bestätigung destruktiver Aktionen über einen modalen Dialog (statt der alten Aufklapp-Flyouts).
    // „Soll gefragt werden?" ist reine UI; die eigentliche Aktion bleibt ein Command auf dem Controller.
    // Deleting a curve or profile asks nothing: it is a history step with „Rückgängig" in its toast.

    private async void OnRevertChanges(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainController controller)
            return;
        // Discarding everything keeps its confirmation although Ctrl+Z brings it back - the dialog says so.
        await ConfirmThen(
            Localizer.Instance["Dialog.RevertTitle"],
            Localizer.Instance["Dialog.RevertMessage"],
            Localizer.Instance["MainWindow.Revert"],
            () => { controller.Editor.RevertCommand.Execute(null); return Task.CompletedTask; });
    }

    // "Copy" in the offline banner: the clipboard is a view service, the controller only shows "Copied". Without a
    // clipboard, or when writing fails, nothing claims success - the command stays selectable in the banner.
    private async void OnCopyDiagnosticCommand(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainController controller || Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(controller.Connection.DiagnosticCommand);
            controller.Connection.MarkCopied();
        }
        catch (Exception ex)
        {
            // A platform service failing: the banner shows no "Copied", which says enough.
            controller.RecordError("Clipboard", "Copying the diagnostic command failed", ex);
        }
    }

    /// <summary>
    /// A row picked in the curves tab's side menu goes to the controller - posted, because opening a profile changes
    /// this very list and the list box is still inside its selection change, which fails on a changed source. The list
    /// shows the controller's selection (one-way binding): whatever the controller does not open (it decides which rows
    /// can be opened) gets the controller's row back.
    /// </summary>
    private void OnCurveNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not MainController controller
            || ReferenceEquals(list.SelectedItem, controller.Editor.SelectedNavItem))
            return;
        Dispatcher.UIThread.Post(() =>
        {
            controller.Editor.SelectedNavItem = list.SelectedItem as CurveTabNavItem;
            if (!ReferenceEquals(list.SelectedItem, controller.Editor.SelectedNavItem))
                list.SelectedItem = controller.Editor.SelectedNavItem;
        });
    }

    /// <summary>
    /// The "add curve" row of the curves tab's side menu is an action, which the list box can only select: Enter or
    /// Space on the row opens its template menu instead, as a click on it does. Pure UI - the entries run the
    /// controller's command.
    /// </summary>
    private void OnCurveNavKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || e.KeyModifiers != KeyModifiers.None
            || e.Source is not ListBoxItem { DataContext: AddCurveNavItem } row)
            return;
        if (row.GetVisualDescendants().OfType<Button>().FirstOrDefault() is { IsEffectivelyEnabled: true, Flyout: { } flyout } button)
        {
            flyout.ShowAt(button);
            e.Handled = true;
        }
    }

    // "Pick in the case diagram" from a fan's position menu: the dialog is a view, the controller only asks for it.
    // The result goes back through the controller, as an edit like a menu pick. Cancel leaves the position alone.
    private async void OnLocationDialogRequested(object? sender, FanAssignRow row)
    {
        if (DataContext is not MainController controller)
            return;
        LocationPickController pick = controller.Editor.CreateLocationPick(row);
        LocationPick? picked = await new FanLocationDialog(pick).ShowDialog<LocationPick?>(this);
        if (picked is not null)
            controller.Editor.ApplyLocationPick(row.FanId, picked);
    }

    // Replaced and missing hardware, from an overview hint: the dialog is a view, the controller holds its state and follows
    // the snapshots while it is open.
    private async void OnHardwareDialogRequested(object? sender, HardwareChangeController changes) =>
        await new HardwareChangeDialog(changes).ShowDialog(this);

    // Switching the running profile with unsaved changes: the dialog is a view, the editor decides what each answer does
    // ("Übernehmen & wechseln" saves and switches in one config, "Verwerfen & wechseln" discards and switches live) and
    // reports a failure itself in its status toast. Anything else that goes wrong - the dialog itself included - reaches
    // the app's error barrier (recorded, and the user told) instead of vanishing in this handler.
    private async void OnProfileSwitchConfirmationRequested(object? sender, ProfileSwitchRequest request)
    {
        if (DataContext is not MainController controller)
            return;
        IInputElement? origin = FocusManager?.GetFocusedElement();
        UnsavedChoice choice = await new UnsavedChangesDialog(request.Title, request.Message, request.DiscardLabel,
            request.SaveLabel, request.DiscardBlockedReason).ShowDialog<UnsavedChoice>(this);

        // Back to where the switch started; a menu entry is gone by now - then to the button that opened the menu.
        if (origin is InputElement { IsEffectivelyVisible: true } start && TopLevel.GetTopLevel(start) == this)
            start.Focus();
        else
            ProfileButton.Focus();
        switch (choice)
        {
            case UnsavedChoice.Save:
                await controller.Editor.SaveAndSwitchAsync(request.ProfileId);
                break;
            case UnsavedChoice.Discard:
                controller.Editor.DiscardAndSwitch(request.ProfileId);
                break;
        }
    }

    // A group's name field commits when the focus leaves it (Enter commits through its key binding): typing only
    // drafts, so the duplicate warning can follow every keystroke before a rename merges anything.
    private void OnGroupNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SensorGroupSection section)
            section.CommitNameCommand.Execute(null);
    }

    // --- Drag at the grip (settings: sensor groups, fan positions) ---
    // Pure gesture plumbing: the controller decides what is dragged (a selected row takes the selection along) and
    // carries out the drop as an edit. The grip is a Button, which marks the press handled - hence handledEventsToo.

    private static readonly DataFormat<string[]> SensorIdsFormat = DataFormat.CreateInProcessFormat<string[]>("linfan-sensor-ids");
    private static readonly DataFormat<string[]> FanIdsFormat = DataFormat.CreateInProcessFormat<string[]>("linfan-fan-ids");

    /// <summary>Pointer travel before a press on a grip becomes a drag - a plain click stays a click.</summary>
    private const double DragThreshold = 4;

    private PointerPressedEventArgs? _gripPress;
    private Point _gripPressAt;
    private InputElement? _gripCapture; // holds the pointer capture of the press - losing it ends a pending drag
    private Border? _dropTarget;

    private void OnGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ForgetGripPress();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not { } button
            || !button.Classes.Contains("grip"))
            return;
        _gripPress = e;
        _gripPressAt = e.GetPosition(this);
        // Capture-lost is a direct event: listen where the capture actually is (the button, by the time we get here).
        _gripCapture = e.Pointer.Captured as InputElement;
        if (_gripCapture is not null)
            _gripCapture.PointerCaptureLost += OnGripCaptureLost;
    }

    private void OnGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ForgetGripPress();

    private void ForgetGripPress()
    {
        _gripPress = null;
        if (_gripCapture is not null)
            _gripCapture.PointerCaptureLost -= OnGripCaptureLost;
        _gripCapture = null;
    }

    private async void OnGripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_gripPress is not { } press || DataContext is not MainController controller)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ForgetGripPress(); // the release went somewhere we did not see
            return;
        }
        Point at = e.GetPosition(this);
        if (Math.Abs(at.X - _gripPressAt.X) < DragThreshold && Math.Abs(at.Y - _gripPressAt.Y) < DragThreshold)
            return;
        object? row = (press.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true)?.DataContext;
        ForgetGripPress();
        if (row is not (SensorOption or FanAssignRow))
            return;

        try
        {
            var data = new DataTransfer();
            if (row is SensorOption sensor)
                data.Add(DataTransferItem.Create(SensorIdsFormat, controller.Editor.BeginSensorDrag(sensor).ToArray()));
            else if (row is FanAssignRow fan)
                data.Add(DataTransferItem.Create(FanIdsFormat, controller.Editor.BeginFanDrag(fan).ToArray()));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            // A platform drag failing ends the drag, nothing else; the rows stay where they were.
            controller.RecordError("Drag", "The drag at a grip failed", ex);
        }
        finally
        {
            SetDropTarget(null);
            controller.Editor.EndSensorDrag();
            controller.Editor.EndFanDrag();
        }
    }

    /// <summary>The section under the pointer that takes this drag - sensors drop on groups, fans on positions.</summary>
    private static Border? DropSectionFor(DragEventArgs e)
    {
        Border? section = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("groupSection"));
        return section?.DataContext switch
        {
            SensorGroupSection when e.DataTransfer.Contains(SensorIdsFormat) => section,
            FanLocationSection when e.DataTransfer.Contains(FanIdsFormat) => section,
            _ => null,
        };
    }

    private void OnSettingsDragOver(object? sender, DragEventArgs e)
    {
        Border? section = DropSectionFor(e);
        e.DragEffects = section is null ? DragDropEffects.None : DragDropEffects.Move;
        SetDropTarget(section);
    }

    private void OnSettingsDragLeave(object? sender, DragEventArgs e) => SetDropTarget(null);

    private void OnSettingsDrop(object? sender, DragEventArgs e)
    {
        Border? section = DropSectionFor(e);
        SetDropTarget(null);
        if (section is null || DataContext is not MainController controller)
            return;
        switch (section.DataContext)
        {
            case SensorGroupSection group when e.DataTransfer.TryGetValue(SensorIdsFormat) is { } sensorIds:
                controller.Editor.MoveSensors(sensorIds, group);
                break;
            case FanLocationSection position when e.DataTransfer.TryGetValue(FanIdsFormat) is { } fanIds:
                controller.Editor.SetFanLocations(fanIds, position);
                break;
        }
        e.DragEffects = DragDropEffects.Move;
    }

    private void SetDropTarget(Border? section)
    {
        if (ReferenceEquals(section, _dropTarget))
            return;
        _dropTarget?.Classes.Remove("dropTarget");
        _dropTarget = section;
        _dropTarget?.Classes.Add("dropTarget");
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
        catch (Exception ex)
        {
            // A file the user picked could not be written (rights, disk): the toast says so, the log says why.
            controller.RecordError("Backup", "Exporting the backup failed", ex);
            controller.Backup.ReportExportFailed();
        }
    }

    // Read and checked before the confirmation (an unusable file needs none), which then names what the backup holds -
    // it replaces the whole configuration, like a reset.
    private async void OnImportBackup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainController controller)
            return;
        BackupImport? import;
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
            import = controller.Backup.ReadImport(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            controller.RecordError("Backup", "Reading the backup file failed", ex);
            controller.Backup.ReportImportReadFailed();
            return;
        }
        if (import is null)
            return; // the controller has said why
        await ConfirmThen(
            Localizer.Instance["Settings.ImportConfirmTitle"],
            import.ConfirmMessage,
            Localizer.Instance["Settings.ImportConfirm"],
            () => controller.Backup.ImportAsync(import));
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

    // Not guarded: what fails here reaches the app's error barrier (recorded, and the user told). The actions behind it
    // report their own failures and do not throw.
    private async Task ConfirmThen(string title, string message, string confirmText, Func<Task> onConfirmed)
    {
        if (await new ConfirmDialog(title, message, confirmText).ShowDialog<bool>(this))
            await onConfirmed();
    }

    private CurveEditorController? _editor;
    private MainController? _overview;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_editor is not null)
        {
            _editor.LocationDialogRequested -= OnLocationDialogRequested;
            _editor.ProfileSwitchConfirmationRequested -= OnProfileSwitchConfirmationRequested;
        }
        _editor = (DataContext as MainController)?.Editor;
        if (_editor is not null)
        {
            _editor.LocationDialogRequested += OnLocationDialogRequested;
            _editor.ProfileSwitchConfirmationRequested += OnProfileSwitchConfirmationRequested;
        }

        if (_overview is not null)
        {
            _overview.OverviewScrollRequested -= OnOverviewScrollRequested;
            _overview.HardwareDialogRequested -= OnHardwareDialogRequested;
        }
        _overview = DataContext as MainController;
        if (_overview is not null)
        {
            _overview.OverviewScrollRequested += OnOverviewScrollRequested;
            _overview.HardwareDialogRequested += OnHardwareDialogRequested;
        }

        if (DataContext is MainController controller)
            controller.PropertyChanged += OnControllerPropertyChanged;
    }

    /// <summary>
    /// A status tile or hint leads to a section of the overview: scroll it to the top of the view. Posted, so a tile the
    /// hint has just opened is laid out first.
    /// </summary>
    private void OnOverviewScrollRequested(object? sender, OverviewSection section)
    {
        Control target = section == OverviewSection.Temperatures ? OverviewTemperatures : OverviewFans;
        Dispatcher.UIThread.Post(() =>
        {
            if (target.TranslatePoint(new Point(0, 0), OverviewContent) is { } top)
                OverviewScroller.Offset = new Vector(OverviewScroller.Offset.X, top.Y);
        }, DispatcherPriority.Loaded);
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
