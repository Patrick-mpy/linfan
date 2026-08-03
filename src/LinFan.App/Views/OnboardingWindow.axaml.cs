// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Interactivity;
using LinFan.App.Controllers;
using LinFan.Core.Models;

namespace LinFan.App.Views;

/// <summary>
/// Onboarding-Wizard. Code-Behind bleibt minimal (nur reine UI-Belange).
/// Beim Schließen des Fensters wird der Skip-Pfad des Controllers angestoßen, falls das Onboarding
/// noch nicht abgeschlossen wurde — so wird <c>OnboardingCompleted = true</c> immer gesetzt.
/// </summary>
public partial class OnboardingWindow : Window
{
    private bool _closeConfirmed;

    public OnboardingWindow() => InitializeComponent();

    // Positions-Modal je Lüfterzeile: die Zeile kommt aus dem DataContext des Buttons; das Ergebnis wird zurück
    // in die gebundene Location geschrieben (reine UI — wie im Geräte-Tab). Abbrechen lässt sie unberührt.
    private async void OnPickLocation(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not OnboardingFanRow row)
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

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Schließen einmalig aufhalten, bis der Skip-Pfad (OnboardingCompleted = true) gesendet wurde —
        // sonst kann das Senden mit dem Fenster-Schließen wettrennen. Der Latch im Controller verhindert
        // ein Doppel-Senden, falls bereits per Finish/Skip geschlossen wurde.
        if (_closeConfirmed || DataContext is not OnboardingController controller)
            return;

        e.Cancel = true;
        await controller.SkipCommand.ExecuteAsync(null);
        _closeConfirmed = true;
        Close();
    }
}
