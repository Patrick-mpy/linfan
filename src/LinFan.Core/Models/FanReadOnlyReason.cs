// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Why a fan channel is read-only (<see cref="FanDescriptor.CanControl"/> == <c>false</c>), as far as the backend knows.
/// Lets the GUI say what would help instead of only "not controllable". Sent across IPC by member name, so the list may
/// grow; a reason a GUI does not know reads as unknown there.
/// </summary>
public enum FanReadOnlyReason
{
    /// <summary>The process lacks the rights to write the channel (no root / admin - a dry run, for instance).</summary>
    NoWriteAccess,

    /// <summary>The driver refuses every write, root's included (Linux: <c>thinkpad_acpi</c> without <c>fan_control=1</c>).</summary>
    DriverLocked,

    /// <summary>The hardware offers no control for the channel - it only reports its speed.</summary>
    NoControlInterface,
}
