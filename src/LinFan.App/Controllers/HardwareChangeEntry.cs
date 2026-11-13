// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>A fan a hardware change covers, as its card lists it.</summary>
/// <param name="Name">The fan's name: the shown or configured one, else its id.</param>
/// <param name="Fan">The overview's row of a present, controllable fan of a replaced device - its „Neu kalibrieren"
/// (the row's command, lock and tooltip, as in the fan table) and calibration state; <c>null</c> otherwise.</param>
public sealed record HardwareChangeEntry(string Name, FanRow? Fan)
{
    /// <summary>The entry offers „Neu kalibrieren" and shows the fan's calibration state.</summary>
    public bool HasFan => Fan is not null;

    /// <summary>Accessible name of the entry's „Neu kalibrieren" button.</summary>
    public string RecalibrateName => Localizer.Instance.Format("HardwareDialog.RecalibrateName", Name);
}
