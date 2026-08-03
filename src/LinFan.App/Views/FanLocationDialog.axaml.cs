// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LinFan.App.Controllers;
using LinFan.App.Controls;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Views;

/// <summary>
/// Modale Positions-Auswahl mit interaktiver Gehäuse-Vorschau (<see cref="Controls.FanLocationDiagram"/>)
/// plus Listen-Fallback. Liefert die gewählte <see cref="FanLocation"/> bzw. <c>null</c> (Abbrechen/Esc/
/// Fenster-X) über <c>ShowDialog&lt;FanLocation?&gt;</c> zurück. Reine UI, keine Domain-Logik — wie
/// <see cref="ConfirmDialog"/>. Diagramm und Liste binden two-way an dieselbe <see cref="SelectedLocation"/>.
/// </summary>
public partial class FanLocationDialog : Window
{
    public static readonly StyledProperty<FanLocation> SelectedLocationProperty =
        AvaloniaProperty.Register<FanLocationDialog, FanLocation>(nameof(SelectedLocation));

    /// <summary>Aktuelle Auswahl — gemeinsame Wahrheit für Diagramm und Listen-Fallback.</summary>
    public FanLocation SelectedLocation
    {
        get => GetValue(SelectedLocationProperty);
        set => SetValue(SelectedLocationProperty, value);
    }

    /// <summary>Optionen für den Listen-Fallback (dieselben wie im Geräte-Tab).</summary>
    public IReadOnlyList<FanLocationOption> Options => FanLocationOption.All;

    /// <summary>
    /// Optionale temporäre Manuell-Steuerung (geteilt mit der aufrufenden Lüfterzeile): erlaubt, den Lüfter
    /// anzustoßen, um zu sehen, welcher physisch reagiert, während die Position gewählt wird. <c>null</c> bzw.
    /// read-only → kein Slider. Beim Schließen wird sie zurückgesetzt (Auto/Kurve).
    /// </summary>
    public ManualControl? Manual { get; }

    /// <summary>Nur einen Manuell-Slider zeigen, wenn eine steuerbare Manuell-Steuerung übergeben wurde.</summary>
    public bool ShowManual => Manual is { CanControl: true };

    public FanLocationDialog()
    {
        InitializeComponent();
        DataContext = this;
        Closed += (_, _) => Manual?.Revert(); // jeder Schließweg (Übernehmen/Abbrechen/Esc/X) → zurück auf Auto/Kurve
    }

    public FanLocationDialog(string fanName, FanLocation current, ManualControl? manual = null) : this()
    {
        Manual = manual;
        Title = Localizer.Instance["FanLocationDialog.WindowTitle"];
        HeadingText.Text = string.IsNullOrWhiteSpace(fanName)
            ? Localizer.Instance["FanLocationDialog.HeadingGeneric"]
            : Localizer.Instance.Format("FanLocationDialog.HeadingFor", fanName);
        SelectedLocation = current;
    }

    // Den Richtungs-Schalter an die aktuelle Auswahl koppeln (reine UI): nur für umschaltbare Gehäuse-Positionen
    // sichtbar, Text spiegelt die Richtung. Läuft bei jeder Auswahländerung — egal ob aus Diagramm, Liste oder Schalter.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedLocationProperty)
            UpdateDirectionUi();
    }

    private void UpdateDirectionUi()
    {
        if (DirectionPanel is null)
            return; // vor InitializeComponent (theoretischer früher Property-Change)

        bool canFlip = FanLocationLayout.CanFlip(SelectedLocation);
        DirectionPanel.IsVisible = canFlip;
        if (canFlip)
            DirectionText.Text = FanLocationOption.DirectionOf(SelectedLocation) == AirflowDirection.Intake
                ? Localizer.Instance["FanLocation.Intake"]
                : Localizer.Instance["FanLocation.Exhaust"];
    }

    private void OnToggleDirection(object? sender, RoutedEventArgs e) =>
        SelectedLocation = FanLocationLayout.Flip(SelectedLocation);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close((FanLocation?)SelectedLocation);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close((FanLocation?)null);
}
