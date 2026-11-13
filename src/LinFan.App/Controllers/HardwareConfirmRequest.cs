// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// A question the hardware dialog asks before it sends (<see cref="HardwareChangeController.ConfirmationRequested"/>):
/// what the view shows, and what a „yes" sends once handed back to <see cref="HardwareChangeController.ConfirmedAsync"/>.
/// </summary>
/// <param name="Forget">Removes the card's entries; otherwise keeps them for the new device.</param>
/// <param name="ResetSensorNames">With a keep: also clears the sensors' own names.</param>
public sealed record HardwareConfirmRequest(
    HardwareChangeCard Card, bool Forget, bool ResetSensorNames, string Title, string Message, string ConfirmLabel);
