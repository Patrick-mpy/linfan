// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Answers "who controls this fan right now" for the overview from what the daemon reported for its last tick
/// (<see cref="FanReading.ControlState"/>). The GUI does not guess: only the daemon knows whether it drove the curve's
/// value, held the last one for want of a readable sensor, or put the fans into the fail-safe. Without a report - offline,
/// an older daemon, a state this GUI does not know - the answer is "unknown". Pure, so plain unit tests cover every state.
/// </summary>
public static class FanControlResolver
{
    /// <param name="fan">The fan as the snapshot reported it.</param>
    /// <param name="snapshot">The snapshot the fan comes from (the last connected one while offline): curve names, dry run.</param>
    /// <param name="connected">Whether the daemon is reachable now - offline nothing is known.</param>
    public static FanControl Resolve(FanReading fan, MonitorSnapshot snapshot, bool connected)
    {
        Localizer l = Localizer.Instance;
        if (!connected || fan.ControlState is not { } state)
            return new FanControl(null, l["Overview.ControlUnknown"], l["Overview.ControlUnknownTooltip"], ControlTone.Muted);

        string? curveId = fan.ControlCurveId;
        string name = curveId is null ? l["Overview.ControlCurveUnnamed"] : CurveName(curveId, snapshot.Config);
        if (curveId is null && state is FanControlState.Curve or FanControlState.CurveHolding)
        {
            // The daemon names the curve with these states; without it there is nothing to open.
            return new FanControl(state, state == FanControlState.Curve ? name : l.Format("Overview.ControlHolding", name),
                l["Overview.ControlCurveUnnamedTooltip"],
                state == FanControlState.Curve ? ControlTone.Normal : ControlTone.Warning);
        }
        switch (state)
        {
            case FanControlState.Curve when snapshot.DryRun:
                // In a dry run "Curve" means "would drive" - nothing is written.
                return new FanControl(state, l.Format("Overview.ControlCurveDryRun", name),
                    l.Format("Overview.ControlCurveDryRunTooltip", name), ControlTone.Normal, curveId);
            case FanControlState.Curve:
                return new FanControl(state, name, l.Format("Overview.ControlCurveTooltip", name), ControlTone.Normal, curveId);
            case FanControlState.CurveHolding:
                return new FanControl(state, l.Format("Overview.ControlHolding", name),
                    l.Format("Overview.ControlHoldingTooltip", name), ControlTone.Warning, curveId);
            case FanControlState.Manual:
                return new FanControl(state, l["Overview.ControlManual"], l["Overview.ControlManualTooltip"], ControlTone.Warning);
            case FanControlState.HardwareAuto:
                // With a curve id the assigned curve is switched off; without one there is no (known) curve.
                return new FanControl(state, l["Overview.ControlHardwareAuto"],
                    curveId is null
                        ? l["Overview.ControlHardwareAutoTooltip"]
                        : l.Format("Overview.ControlHardwareAutoCurveOffTooltip", name),
                    ControlTone.Muted);
            case FanControlState.Calibrating:
                return new FanControl(state, l["Overview.ControlCalibrating"], l["MainWindow.CalibrationRunning"], ControlTone.Accent);
            case FanControlState.Identifying:
                return new FanControl(state, l["Overview.ControlIdentifying"], l["Overview.ControlIdentifyTooltip"], ControlTone.Accent);
            case FanControlState.IdentifyThrottled:
                return new FanControl(state, l["Overview.ControlIdentifyThrottled"], l["Overview.ControlIdentifyTooltip"], ControlTone.Accent);
            case FanControlState.Coupling:
                return new FanControl(state, l["Overview.ControlCoupling"], l["Overview.ControlCouplingTooltip"], ControlTone.Accent);
            case FanControlState.CouplingThrottled:
                return new FanControl(state, l["Overview.ControlCouplingThrottled"], l["Overview.ControlCouplingTooltip"], ControlTone.Accent);
            case FanControlState.FailSafe when snapshot.DryRun:
                // The watchdog tripped, but a dry run hands nothing to the firmware.
                return new FanControl(state, l["Overview.ControlFailSafeDryRun"], l["Overview.ControlFailSafeDryRunTooltip"],
                    ControlTone.Danger);
            case FanControlState.FailSafe:
                return new FanControl(state, l["Overview.ControlFailSafe"], l["Overview.ControlFailSafeTooltip"], ControlTone.Danger);
            case FanControlState.Failed:
                return new FanControl(state, l["Overview.ControlFailed"], l["Overview.ControlFailedTooltip"], ControlTone.Danger);
            case FanControlState.NotControllable:
                return new FanControl(state, l["Overview.ControlNotControllable"],
                    ReadOnlyReasonText.Of(fan.ReadOnlyReason) ?? l["Overview.ControlNotControllableTooltip"], ControlTone.Muted);
            default:
                return new FanControl(null, l["Overview.ControlUnknown"], l["Overview.ControlUnknownTooltip"], ControlTone.Muted);
        }
    }

    /// <summary>The curve of the running set the fan is assigned to, if it exists (config, not control state).</summary>
    public static CurveConfig? AssignedCurve(string fanId, AppConfig config)
    {
        string? curveId = config.Fans.FirstOrDefault(f => f.FanId == fanId)?.AssignedCurveId;
        return curveId is null ? null : config.Curves.FirstOrDefault(c => c.Id == curveId);
    }

    /// <summary>A curve's name for display; its id if the config does not (or no longer) know it.</summary>
    public static string CurveName(string curveId, AppConfig config) =>
        config.Curves.FirstOrDefault(c => c.Id == curveId)?.Name is { Length: > 0 } name ? name : curveId;
}
