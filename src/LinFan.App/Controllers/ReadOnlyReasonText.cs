// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// Says why a fan is read-only, from the reason the daemon's backend reported - so the GUI can name what would help
/// instead of only "not controllable". Pure, so plain unit tests cover every reason.
/// </summary>
public static class ReadOnlyReasonText
{
    /// <summary>The reason as a sentence; <c>null</c> when it is not known.</summary>
    public static string? Of(FanReadOnlyReason? reason) => reason switch
    {
        FanReadOnlyReason.NoWriteAccess => Localizer.Instance["ReadOnly.NoWriteAccess"],
        FanReadOnlyReason.DriverLocked => Localizer.Instance["ReadOnly.DriverLocked"],
        FanReadOnlyReason.NoControlInterface => Localizer.Instance["ReadOnly.NoControlInterface"],
        _ => null,
    };

    /// <summary>A read-only note, followed by the reason where it is known.</summary>
    public static string Note(string note, FanReadOnlyReason? reason) => Of(reason) is { } why ? $"{note} {why}" : note;
}
