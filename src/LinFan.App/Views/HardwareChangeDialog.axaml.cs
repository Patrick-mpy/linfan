// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Interactivity;
using LinFan.App.Controllers;

namespace LinFan.App.Views;

/// <summary>
/// Replaced and missing hardware: one card per device the daemon reports. The state lives in
/// <see cref="HardwareChangeController"/>, which follows the snapshots and asks to close once no card is left; this view
/// only asks the confirmations it requests and hands a „yes" back.
/// </summary>
public partial class HardwareChangeDialog : Window
{
    private readonly HardwareChangeController? _controller;

    public HardwareChangeDialog() => InitializeComponent();

    public HardwareChangeDialog(HardwareChangeController controller) : this()
    {
        _controller = controller;
        DataContext = controller;
        controller.CloseRequested += OnCloseRequested;
        controller.ConfirmationRequested += OnConfirmationRequested;
        Closed += (_, _) =>
        {
            controller.CloseRequested -= OnCloseRequested;
            controller.ConfirmationRequested -= OnConfirmationRequested;
            controller.Close();
        };
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    // Not guarded: what fails here reaches the app's error barrier (recorded, and the user told). The send behind it
    // reports its own failure on the card and does not throw.
    private async void OnConfirmationRequested(object? sender, HardwareConfirmRequest request)
    {
        if (await new ConfirmDialog(request.Title, request.Message, request.ConfirmLabel).ShowDialog<bool>(this)
            && _controller is not null)
            await _controller.ConfirmedAsync(request);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
