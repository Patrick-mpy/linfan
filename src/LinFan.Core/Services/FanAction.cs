// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Services;

/// <summary>Ergebnis der Regelung für genau einen Lüfter in einem Tick.</summary>
public sealed record FanAction(
    string FanId,
    FanActionKind Kind,
    double TemperatureC,
    byte Pwm,
    string? Note = null)
{
    public static FanAction Applied(string fanId, double temp, byte pwm) => new(fanId, FanActionKind.Applied, temp, pwm);
    public static FanAction DryRun(string fanId, double temp, byte pwm) => new(fanId, FanActionKind.DryRun, temp, pwm);
    public static FanAction Held(string fanId, double temp, byte pwm) => new(fanId, FanActionKind.Held, temp, pwm);
    public static FanAction Manual(string fanId, byte pwm) => new(fanId, FanActionKind.Manual, double.NaN, pwm);
    public static FanAction Skipped(string fanId, string note) => new(fanId, FanActionKind.Skipped, double.NaN, 0, note);
    public static FanAction Failed(string fanId, string note) => new(fanId, FanActionKind.Failed, double.NaN, 0, note);
}
