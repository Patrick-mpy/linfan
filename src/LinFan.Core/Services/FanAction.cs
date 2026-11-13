// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>
/// Ergebnis der Regelung für genau einen Lüfter in einem Tick.
/// <para>
/// <paramref name="State"/> says who controls the fan after this tick. <paramref name="CurveId"/> names the assigned
/// curve when the state comes from it - <see cref="FanControlState.Curve"/>, <see cref="FanControlState.CurveHolding"/>,
/// <see cref="FanControlState.HardwareAuto"/> (the curve is switched off, or has no readable source and has not driven
/// the fan since it was last reset - the config's Enabled flag tells which) and <see cref="FanControlState.Failed"/>
/// (writing the curve's value failed) - and is <c>null</c> otherwise.
/// </para>
/// </summary>
public sealed record FanAction(
    string FanId,
    FanActionKind Kind,
    double TemperatureC,
    byte Pwm,
    FanControlState State,
    string? CurveId = null,
    string? Note = null)
{
    public static FanAction Applied(string fanId, string curveId, double temp, byte pwm) =>
        new(fanId, FanActionKind.Applied, temp, pwm, FanControlState.Curve, curveId);

    public static FanAction DryRun(string fanId, string curveId, double temp, byte pwm) =>
        new(fanId, FanActionKind.DryRun, temp, pwm, FanControlState.Curve, curveId);

    public static FanAction Held(string fanId, string curveId, double temp, byte pwm) =>
        new(fanId, FanActionKind.Held, temp, pwm, FanControlState.Curve, curveId);

    public static FanAction Manual(string fanId, byte pwm) =>
        new(fanId, FanActionKind.Manual, double.NaN, pwm, FanControlState.Manual);

    public static FanAction Skipped(string fanId, FanControlState state, string note, string? curveId = null) =>
        new(fanId, FanActionKind.Skipped, double.NaN, 0, state, curveId, note);

    /// <summary>Handed back at full speed in place of firmware auto, which the channel does not offer (<paramref name="note"/>: why).</summary>
    public static FanAction FullSpeed(string fanId, string note, string? curveId = null) =>
        new(fanId, FanActionKind.FullSpeed, double.NaN, byte.MaxValue, FanControlState.HardwareAuto, curveId, note);

    public static FanAction Failed(string fanId, string note, string? curveId = null) =>
        new(fanId, FanActionKind.Failed, double.NaN, 0, FanControlState.Failed, curveId, note);
}
