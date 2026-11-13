// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>
/// Ergebnis eines Regel-Ticks: ob der Fail-Safe griff, die höchste Temperatur und die Lüfter-Aktionen.
/// <para>
/// <paramref name="FailSafeTriggered"/> is true only in the tick that tripped the fail-safe;
/// <paramref name="FailSafe"/> also stays set in the ticks after it while the fans remain in the safe state
/// (see <see cref="FailSafeState"/>).
/// </para>
/// </summary>
public sealed record ControlTick(
    bool FailSafeTriggered,
    double HottestTempC,
    IReadOnlyList<FanAction> Actions,
    string? FailSafeReason = null,
    FailSafeState FailSafe = FailSafeState.None)
{
    /// <summary>
    /// The control state this tick left a fan in. A read-only channel is <see cref="FanControlState.NotControllable"/>
    /// whatever the loop attempted. A fan the loop did not act on - one without a config entry that is neither
    /// manual nor suspended - is left to the firmware: <see cref="FanControlState.FailSafe"/> while the fail-safe is in
    /// force, otherwise <see cref="FanControlState.HardwareAuto"/>.
    /// </summary>
    public (FanControlState State, string? CurveId) ControlOf(string fanId, bool canControl)
    {
        if (!canControl)
            return (FanControlState.NotControllable, null);
        foreach (FanAction action in Actions)
        {
            if (action.FanId == fanId)
                return (action.State, action.CurveId);
        }
        return (FailSafe == FailSafeState.None ? FanControlState.HardwareAuto : FanControlState.FailSafe, null);
    }
}
