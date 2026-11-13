// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// One device whose configured entries no longer match the hardware (<see cref="HardwareChanges.Detect"/>).
/// </summary>
/// <param name="Key">Names the change from its kind and devices alone, so the same change keeps its key while the
/// entries it covers change; safe as an IPC command target.</param>
/// <param name="Kind">Replaced or missing.</param>
/// <param name="Old">The device the entries recorded; <c>null</c> for missing entries that never recorded one.</param>
/// <param name="New">The device behind the ids now; <c>null</c> for <see cref="HardwareChangeKind.Missing"/>.</param>
/// <param name="FanIds">The fan entries concerned, in ordinal order.</param>
/// <param name="SensorIds">The sensor entries concerned, in ordinal order.</param>
public sealed record HardwareChange(
    string Key,
    HardwareChangeKind Kind,
    HardwareDevice? Old,
    HardwareDevice? New,
    IReadOnlyList<string> FanIds,
    IReadOnlyList<string> SensorIds);
