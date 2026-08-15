// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using LinFan.App.Localization;
using LinFan.Core.Models;

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
        ["airflow-cpu"] = Localizer.Instance["CurveEditorCtrl.AirflowCurveCpu"],
        ["airflow-gpu"] = Localizer.Instance["CurveEditorCtrl.AirflowCurveGpu"],
        ["airflow-intake"] = Localizer.Instance["CurveEditorCtrl.AirflowCurveIntake"],
        ["airflow-exhaust"] = Localizer.Instance["CurveEditorCtrl.AirflowCurveExhaust"],
        ["airflow-default"] = Localizer.Instance["CurveEditorCtrl.AirflowCurveDefault"],
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
        AirflowHint.NoCaseFans => Localizer.Instance["CurveEditorCtrl.AirflowHintNoCaseFans"],
        AirflowHint.CountEstimateOnly => Localizer.Instance["CurveEditorCtrl.AirflowHintCountEstimate"],
        AirflowHint.NoIntakeFan => Localizer.Instance["CurveEditorCtrl.AirflowHintNoIntake"],
        AirflowHint.NoExhaustFan => Localizer.Instance["CurveEditorCtrl.AirflowHintNoExhaust"],
        AirflowHint.NegativePressure => Localizer.Instance["CurveEditorCtrl.AirflowHintNegativePressure"],
        AirflowHint.NoCpuSensorDetected => Localizer.Instance["CurveEditorCtrl.AirflowHintNoCpuSensor"],
        _ => hint.ToString(), // unbekannter Code: roh anzeigen statt verschlucken
    };

    public static string DescribePressure(AirflowTuneResult r)
    {
        string weights = Localizer.Instance.Format("CurveEditorCtrl.AirflowWeights",
            r.IntakeWeight.ToString("0", CultureInfo.InvariantCulture),
            r.ExhaustWeight.ToString("0", CultureInfo.InvariantCulture));
        return r.Pressure switch
        {
            PressureBalance.Positive => Localizer.Instance["CurveEditorCtrl.PressurePositive"] + weights,
            PressureBalance.Negative => Localizer.Instance["CurveEditorCtrl.PressureNegative"] + weights,
            PressureBalance.Balanced => Localizer.Instance["CurveEditorCtrl.PressureBalanced"] + weights,
            _ => Localizer.Instance["CurveEditorCtrl.PressureUnknown"],
        };
    }
}
