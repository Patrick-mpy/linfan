// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using LinFan.App.Controllers;

namespace LinFan.App.Views;

/// <summary>
/// Onboarding-Wizard. Code-Behind bleibt minimal (nur reine UI-Belange).
/// Beim Schließen des Fensters wird der Skip-Pfad des Controllers angestoßen, falls das Onboarding
/// noch nicht abgeschlossen wurde - so wird <c>OnboardingCompleted = true</c> immer gesetzt.
/// </summary>
public partial class OnboardingWindow : Window
{
    private bool _closeConfirmed;

    public OnboardingWindow()
    {
        InitializeComponent();
        // The primary-sensor menu is built as it opens, so its live values are current.
        if (PrimarySensorChip.Flyout is PopupFlyoutBase primaryMenu)
            primaryMenu.Opening += (_, _) => (DataContext as OnboardingController)?.RefreshPrimarySensorMenu();
    }

    // „Position wählen" per fan row: the row comes from the button's DataContext; the controller takes the result over
    // (position and case type, as in the settings). Cancel leaves both alone.
    private async void OnPickLocation(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not OnboardingFanRow row || DataContext is not OnboardingController controller)
            return;
        // A failing dialog reaches the app's error barrier (recorded, and the user told).
        LocationPickController pick = controller.CreateLocationPick(row);
        LocationPick? picked = await new FanLocationDialog(pick).ShowDialog<LocationPick?>(this);
        if (picked is not null)
            controller.ApplyLocationPick(row, picked);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Schließen einmalig aufhalten, bis der Skip-Pfad (OnboardingCompleted = true) gesendet wurde -
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
