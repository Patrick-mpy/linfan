// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;

namespace LinFan.App.Controllers;

// The hardware dialog: replaced and missing devices, opened from the overview's hints. The dialog is a view; this part
// builds its controller, feeds it every snapshot while it is open, and lends it the editor's rebuild.
public partial class MainController
{
    private HardwareChangeController? _hardwareChanges;

    /// <summary>Asks the view to show the hardware dialog for the given controller.</summary>
    public event EventHandler<HardwareChangeController>? HardwareDialogRequested;

    /// <summary>
    /// Opens the hardware dialog for the changes the daemon reports - once at a time, and only while there is something to
    /// resolve and a daemon to send it to.
    /// </summary>
    public void ReviewHardwareChanges()
    {
        if (_hardwareChanges is not null || !Connected || _lastLive is not { } live || _sink is null)
            return;

        HardwareChangeController changes = new(
            _sink.SendAcceptHardwareAsync,
            _sink.SendForgetHardwareAsync,
            ArmConfigResync,
            CancelConfigResync,
            () => Editor.HasUnsavedChanges,
            id => _fanRows.GetValueOrDefault(id),
            onClosed: () => _hardwareChanges = null);
        changes.Apply(live);
        if (changes.Cards.Count == 0)
            return;
        _hardwareChanges = changes;
        HardwareDialogRequested?.Invoke(this, changes);
    }
}
