// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.App.Services;

namespace LinFan.App.Controllers;

/// <summary>How the overview's hints and the hardware dialog name the device of a change.</summary>
public static class HardwareChangeText
{
    /// <summary>The device behind the ids now, else the one the entries recorded, else „Unbekanntes Gerät".</summary>
    public static string DeviceName(HardwareChangeReading change) =>
        Shown(change.NewName) ?? Shown(change.OldName) ?? Localizer.Instance["HardwareDialog.UnknownDevice"];

    /// <summary>
    /// The old and the new device of a replaced one: their names, or their fingerprints where the names are the same - a
    /// Linux backend names a device by its driver („amdgpu"), so only the fingerprint tells two graphics cards apart.
    /// </summary>
    public static (string Old, string New) Devices(HardwareChangeReading change)
    {
        string unknown = Localizer.Instance["HardwareDialog.UnknownDevice"];
        string? oldName = Shown(change.OldName);
        string? newName = Shown(change.NewName);
        if (!string.Equals(oldName, newName, StringComparison.Ordinal))
            return (oldName ?? unknown, newName ?? unknown);
        return (Shown(change.OldFingerprint) ?? oldName ?? unknown, Shown(change.NewFingerprint) ?? newName ?? unknown);
    }

    private static string? Shown(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
