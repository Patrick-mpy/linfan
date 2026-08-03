// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>Ergebnis eines Regel-Ticks: ob der Fail-Safe griff, die höchste Temperatur und die Lüfter-Aktionen.</summary>
public sealed record ControlTick(
    bool FailSafeTriggered,
    double HottestTempC,
    IReadOnlyList<FanAction> Actions,
    string? FailSafeReason = null);
