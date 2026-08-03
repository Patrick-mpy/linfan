// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LinFan.App.Views;

/// <summary>Was der Nutzer beim Schließen mit ungespeicherten Änderungen entschieden hat.</summary>
public enum UnsavedChoice
{
    Cancel = 0, // Default (auch bei Fenster-X) → Schließen abbrechen, nichts verlieren
    Discard,
    Save,
}

/// <summary>
/// Kleiner modaler Bestätigungsdialog beim Schließen mit ungespeicherten Änderungen.
/// Liefert die Wahl über <c>ShowDialog&lt;UnsavedChoice&gt;</c> zurück. Reine UI, keine Domain-Logik.
/// </summary>
public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog() => InitializeComponent();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Cancel);
    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Discard);
    private void OnSave(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Save);
}
