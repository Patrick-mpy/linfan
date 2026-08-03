// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LinFan.App.Views;

/// <summary>
/// Kleiner modaler Bestätigungsdialog für destruktive Aktionen (Löschen/Verwerfen) — ersetzt die
/// alten Aufklapp-Flyouts. Liefert <c>true</c> (bestätigt) bzw. <c>false</c> (abgebrochen, auch bei
/// Fenster-X/Esc) über <c>ShowDialog&lt;bool&gt;</c> zurück. Reine UI, keine Domain-Logik.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    public ConfirmDialog(string title, string message, string confirmText, bool destructive = true)
        : this()
    {
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        if (destructive)
            ConfirmButton.Classes.Add("danger");
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
