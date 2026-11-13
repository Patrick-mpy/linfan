// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Interactivity;
using LinFan.App.Controllers;

namespace LinFan.App.Views;

/// <summary>
/// „Position wählen": the case schema as the choice, the case type, the grouped list and the direction switch, and the
/// nudge to find the fan. Returns the <see cref="LocationPick"/> or <c>null</c> (nothing changed, cancel, Esc, window
/// close) through <c>ShowDialog&lt;LocationPick?&gt;</c>. The state lives in <see cref="LocationPickController"/>; every way out ends a
/// nudge (back to curve/auto), and the controller can close it as a cancel (<see cref="LocationPickController.CloseRequested"/>).
/// </summary>
public partial class FanLocationDialog : Window
{
    private readonly LocationPickController? _controller;

    public FanLocationDialog() => InitializeComponent();

    public FanLocationDialog(LocationPickController controller) : this()
    {
        _controller = controller;
        DataContext = controller;
        controller.CloseRequested += OnCloseRequested;
        Closed += (_, _) =>
        {
            controller.CloseRequested -= OnCloseRequested;
            controller.Close();
        };
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(_controller?.Result);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
