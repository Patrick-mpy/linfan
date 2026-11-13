// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Services;

namespace LinFan.App.Services;

/// <summary>
/// A device whose configured entries no longer match the hardware, as the daemon reported it
/// (<see cref="MonitorSnapshot.HardwareChanges"/>). Names and fingerprints are display text, stripped of unsafe
/// characters; <paramref name="Key"/> and the ids go back to the daemon exactly as they came.
/// </summary>
/// <param name="Key">The command target that resolves the change (accept or forget).</param>
/// <param name="Kind">Replaced or missing; <c>null</c> = a kind this GUI does not know (a newer daemon) - nothing offers
/// to resolve it.</param>
/// <param name="OldName">The device the entries recorded; <c>null</c> when they never recorded one.</param>
/// <param name="NewName">The device behind the ids now; <c>null</c> for a missing one.</param>
/// <param name="FanIds">The fan entries concerned.</param>
/// <param name="SensorIds">The sensor entries concerned.</param>
public sealed record HardwareChangeReading(
    string Key,
    HardwareChangeKind? Kind,
    string? OldName,
    string? OldFingerprint,
    string? NewName,
    string? NewFingerprint,
    IReadOnlyList<string> FanIds,
    IReadOnlyList<string> SensorIds);
