// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Ipc.Messages;

namespace LinFan.App.Services;

/// <summary>
/// Zustand einer Lüfter-Identifikation für die GUI-Anzeige (gespiegelt aus dem Daemon-Snapshot). Der
/// Fehlergrund kommt <b>codifiziert</b> (<see cref="IdentifyFailReason"/> statt fertigem String);
/// <see cref="OverTempC"/>/<see cref="OverLimitC"/> tragen die Messwerte bei Übertemperatur, sonst <c>null</c>.
/// </summary>
public sealed record IdentifyStatus(
    string FanId,
    bool Running,
    IdentifyFailReason? FailReason,
    double? OverTempC = null,
    double? OverLimitC = null);
