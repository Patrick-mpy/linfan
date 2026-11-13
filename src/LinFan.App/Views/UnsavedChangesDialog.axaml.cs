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
/// Kleiner modaler Bestätigungsdialog bei ungespeicherten Änderungen - beim Schließen und vor einem Profilwechsel.
/// Liefert die Wahl über <c>ShowDialog&lt;UnsavedChoice&gt;</c> zurück. Reine UI, keine Domain-Logik.
/// </summary>
public partial class UnsavedChangesDialog : Window
{
    /// <summary>The question on closing the window: save, discard or stay.</summary>
    public UnsavedChangesDialog()
    {
        InitializeComponent();
        // The safe answer has the focus: Enter or Space cancel right away, Tab leads to the others.
        Opened += (_, _) => CancelButton.Focus();
    }

    /// <summary>
    /// The same three answers for another occasion (a profile switch): its own texts, a neutral discard button, and
    /// discarding disabled with <paramref name="discardBlockedReason"/> as its tooltip when it is not possible.
    /// </summary>
    public UnsavedChangesDialog(string title, string message, string discardLabel, string saveLabel, string? discardBlockedReason)
        : this()
    {
        Width = 520; // three long labels („Übernehmen & wechseln") in one row
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        DiscardButton.Content = discardLabel;
        DiscardButton.Classes.Remove("danger");
        SaveButton.Content = saveLabel;
        if (discardBlockedReason is not null)
        {
            DiscardButton.IsEnabled = false;
            ToolTip.SetTip(DiscardButton, discardBlockedReason);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Cancel);
    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Discard);
    private void OnSave(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Save);
}
