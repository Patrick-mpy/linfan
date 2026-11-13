// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// Shared localization mappers for the airflow analysis - used by the settings' airflow section and
/// the onboarding profile step. The Core service stays language-neutral (stable ids + codes); every
/// user-facing airflow string funnels through here so both surfaces stay consistent.
/// </summary>
internal static class AirflowText
{
    /// <summary>Localized names of the airflow curves, key = the Core service's stable curve id.</summary>
    public static Dictionary<string, string> CurveNames() => new(StringComparer.Ordinal)
    {
        [AirflowTuneService.CpuCurveId] = Localizer.Instance["CurveEditorCtrl.AirflowCurveCpu"],
        [AirflowTuneService.GpuCurveId] = Localizer.Instance["CurveEditorCtrl.AirflowCurveGpu"],
        [AirflowTuneService.IntakeCurveId] = Localizer.Instance["CurveEditorCtrl.AirflowCurveIntake"],
        [AirflowTuneService.ExhaustCurveId] = Localizer.Instance["CurveEditorCtrl.AirflowCurveExhaust"],
        [AirflowTuneService.DefaultCurveId] = Localizer.Instance["CurveEditorCtrl.AirflowCurveDefault"],
    };

    public static string DescribeReason(AirflowFanSuggestion s, string curveName) => s.Reason switch
    {
        AirflowReason.HardwareAuto => Localizer.Instance["CurveEditorCtrl.AirflowReasonHardwareAuto"],
        AirflowReason.NoPositionDefaultCurve => Localizer.Instance["CurveEditorCtrl.AirflowReasonNoPosition"],
        _ => Localizer.Instance.Format("CurveEditorCtrl.AirflowReasonLocationCurve",
            FanLocationOption.For(s.Location).Display, curveName),
    };

    public static string DescribeHint(AirflowHint hint) => hint switch
    {
        AirflowHint.NoSensorsConfigured => Localizer.Instance["CurveEditorCtrl.AirflowHintNoSensors"],
        AirflowHint.NoCaseFans => Localizer.Instance["Airflow.HintNoCaseFans"],
        AirflowHint.CountEstimateOnly => Localizer.Instance["CurveEditorCtrl.AirflowHintCountEstimate"],
        AirflowHint.NoIntakeFan => Localizer.Instance["CurveEditorCtrl.AirflowHintNoIntake"],
        AirflowHint.NoExhaustFan => Localizer.Instance["CurveEditorCtrl.AirflowHintNoExhaust"],
        AirflowHint.NegativePressure => Localizer.Instance["CurveEditorCtrl.AirflowHintNegativePressure"],
        AirflowHint.NoCpuSensorDetected => Localizer.Instance["CurveEditorCtrl.AirflowHintNoCpuSensor"],
        AirflowHint.PositionNotInCase => Localizer.Instance["CurveEditorCtrl.AirflowHintNotInCase"],
        AirflowHint.MixedSide => Localizer.Instance["CurveEditorCtrl.AirflowHintMixedSide"],
        AirflowHint.AgainstCaseFlow => Localizer.Instance["CurveEditorCtrl.AirflowHintAgainstFlow"],
        _ => hint.ToString(), // unbekannter Code: roh anzeigen statt verschlucken
    };

    /// <summary>The verdict sentence of the pressure balance (Settings › Airflow and the setup assistant).</summary>
    public static string DescribeVerdict(AirflowTuneResult r) => Localizer.Instance[r.Verdict switch
    {
        PressureVerdict.OnlyExhaust => "Airflow.VerdictOnlyExhaust",
        PressureVerdict.OnlyIntake => "Airflow.VerdictOnlyIntake",
        PressureVerdict.Negative => "Airflow.VerdictNegative",
        PressureVerdict.Balanced => "Airflow.VerdictBalanced",
        PressureVerdict.SlightPositive => "Airflow.VerdictSlightPositive",
        PressureVerdict.StrongPositive => "Airflow.VerdictStrongPositive",
        PressureVerdict.NotApplicable => r.CaseType == CaseType.Laptop ? "Airflow.VerdictLaptop" : "Airflow.VerdictNotApplicable",
        PressureVerdict.FanWallOnly => "Airflow.VerdictFanWallOnly",
        _ => "Airflow.VerdictUnknown",
    }];

    private static StatusTone VerdictTone(PressureVerdict verdict) => verdict switch
    {
        PressureVerdict.SlightPositive => StatusTone.Ok,
        PressureVerdict.OnlyExhaust or PressureVerdict.OnlyIntake or PressureVerdict.Negative or PressureVerdict.StrongPositive => StatusTone.Warn,
        _ => StatusTone.Muted,
    };

    /// <summary>The pressure balance card: bar share, the two sides, the basis and the verdict.</summary>
    public static AirflowBalance Balance(AirflowTuneResult r)
    {
        var balance = new AirflowBalance
        {
            VerdictText = DescribeVerdict(r),
            VerdictTone = VerdictTone(r.Verdict),
        };
        if (r.IntakeShare is not { } share)
            return balance;

        int intake = (int)Math.Round(share * 100, MidpointRounding.AwayFromZero);
        return balance with
        {
            HasCaseFans = true,
            IntakePercent = intake,
            IntakeHead = r.IntakeFans == 1 ? Localizer.Instance["Airflow.IntakeOneFan"] : Localizer.Instance.Format("Airflow.IntakeFans", r.IntakeFans),
            ExhaustHead = r.ExhaustFans == 1 ? Localizer.Instance["Airflow.ExhaustOneFan"] : Localizer.Instance.Format("Airflow.ExhaustFans", r.ExhaustFans),
            IntakeValue = SideValue(intake, r.IntakeWeight, r.ByRpm),
            ExhaustValue = SideValue(100 - intake, r.ExhaustWeight, r.ByRpm),
            BasisText = Localizer.Instance[r.ByRpm ? "Airflow.BasisRpm" : "Airflow.BasisCount"],
            BasisTone = r.ByRpm ? StatusTone.Ok : StatusTone.Muted,
            BasisTooltip = Localizer.Instance[r.ByRpm ? "Airflow.BasisRpmTooltip" : "Airflow.BasisCountTooltip"],
            AutomationName = Localizer.Instance.Format("Airflow.BalanceName", intake, 100 - intake),
        };
    }

    private static string SideValue(int percent, double weight, bool byRpm) => byRpm
        ? Localizer.Instance.Format("Airflow.ShareRpm", percent, weight.ToString("0", CultureInfo.InvariantCulture))
        : Localizer.Instance.Format("Airflow.Share", percent);

    /// <summary>The role a role curve stands for („CPU / Radiator").</summary>
    public static string RoleTitle(string curveId) => Localizer.Instance[curveId switch
    {
        AirflowTuneService.CpuCurveId => "Airflow.RoleCpu",
        AirflowTuneService.GpuCurveId => "Airflow.RoleGpu",
        AirflowTuneService.IntakeCurveId => "Airflow.RoleIntake",
        AirflowTuneService.ExhaustCurveId => "Airflow.RoleExhaust",
        _ => "Airflow.RoleDefault",
    }];

    /// <summary>Which positions the role covers („alle Einlass-Positionen").</summary>
    public static string RoleWho(string curveId) => Localizer.Instance[curveId switch
    {
        AirflowTuneService.CpuCurveId => "Airflow.RoleCpuWho",
        AirflowTuneService.GpuCurveId => "Airflow.RoleGpuWho",
        AirflowTuneService.IntakeCurveId => "Airflow.RoleIntakeWho",
        AirflowTuneService.ExhaustCurveId => "Airflow.RoleExhaustWho",
        _ => "Airflow.RoleDefaultWho",
    }];

    /// <summary>A curve's sources: the sensor's name, „n Sensoren · Mittelwert/Maximum", or „kein Sensor".</summary>
    public static string Sources(CurveConfig curve, Func<string, string> sensorName) => curve.SourceSensorIds.Count switch
    {
        0 => Localizer.Instance["Airflow.NoSensor"],
        1 => sensorName(curve.SourceSensorIds[0]),
        int n => Localizer.Instance.Format(curve.Aggregation == SensorAggregation.Avg ? "Airflow.SensorsAvg" : "Airflow.SensorsMax", n),
    };
}
