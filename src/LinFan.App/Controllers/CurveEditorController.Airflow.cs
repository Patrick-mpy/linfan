// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

// Airflow-Auto-Tune: Analyse, Übernahme, Druckbilanz-Text.
public partial class CurveEditorController
{
    // --- Airflow-Auto-Tune -----------------------------------------------------

    /// <summary>
    /// Analysiert die aktuelle Konfiguration (Lüfter-Positionen → Druckbilanz + rollenbasierte Kurven)
    /// und füllt die Vorschau. Schreibt nichts — der Vorschlag wird erst mit <see cref="ApplyAirflowCommand"/>
    /// in den Editor übernommen und dann gespeichert.
    /// </summary>
    [RelayCommand]
    private void AnalyzeAirflow()
    {
        if (!_initialized)
            return;

        // Lokalisierte Kurven-Namen hereinreichen — sie werden bei „Übernehmen" persistiert (Muster
        // wie bei den Onboarding-Profilen); Hints/Reasons kommen als Codes und werden hier übersetzt.
        AirflowTuneResult result = AirflowTuneService.Analyze(BuildConfig(), AirflowCurveNames());
        _lastAirflowResult = result;

        Dictionary<string, string> curveNames = result.SuggestedCurves.ToDictionary(c => c.Id, c => c.Name);
        AirflowSuggestions.Clear();
        foreach (AirflowFanSuggestion s in result.Fans)
        {
            string fanName = Fans.FirstOrDefault(f => f.FanId == s.FanId)?.Name ?? s.FanId;
            string curveName = s.SuggestedCurveId is { } id && curveNames.TryGetValue(id, out string? n)
                ? n
                : Localizer.Instance["CurveEditorCtrl.NoCurveHardwareAuto"];
            AirflowSuggestions.Add(new AirflowSuggestionRow(
                s.FanId, fanName, FanLocationOption.For(s.Location).Display, curveName,
                DescribeReason(s, curveName), apply: s.SuggestedCurveId is not null));
        }

        AirflowHints.Clear();
        foreach (AirflowHint hint in result.Hints)
            AirflowHints.Add(DescribeHint(hint));

        AirflowPressureText = DescribePressure(result);
        HasAirflowSuggestion = true;
    }

    /// <summary>
    /// Übernimmt die angekreuzten Airflow-Vorschläge in den Editor (Kurven + Zuordnungen) und markiert den
    /// Stand als ungespeichert. Aktiv wird er erst mit „Speichern" (Daemon = alleiniger Schreiber).
    /// </summary>
    [RelayCommand]
    private void ApplyAirflow()
    {
        if (_lastAirflowResult is not { } result || !_initialized)
            return;

        // Nur angekreuzte Lüfter übernehmen — die Kurven-Filterung liegt (testbar) im Core.
        IEnumerable<string> selectedFans = AirflowSuggestions.Where(r => r.Apply).Select(r => r.FanId);
        AirflowTuneResult filtered = AirflowTuneService.FilterToFans(result, selectedFans);
        AppConfig tuned = AirflowTuneService.Apply(BuildConfig(), filtered);

        // Übernommenen Stand in den Editor laden (gleicher Pfad wie ein Profilwechsel).
        var assignments = tuned.Fans.Select(f => new ProfileAssignment(f.FanId, f.AssignedCurveId)).ToList();
        ReloadEditor(tuned.Curves, assignments);

        HasAirflowSuggestion = false;
        AirflowSuggestions.Clear();
        AirflowHints.Clear();
        _lastAirflowResult = null;
        RefreshDirty();
        SetStatus(Localizer.Instance["CurveEditorCtrl.AirflowApplied"], autoHide: true);
    }

    /// <summary>Lokalisierte Namen der Airflow-Kurven, Key = stabile Kurven-Id des Core-Service.</summary>
    private static Dictionary<string, string> AirflowCurveNames() => new(StringComparer.Ordinal)
    {
        ["airflow-cpu"] = Localizer.Instance["CurveEditorCtrl.AirflowCurveCpu"],
        ["airflow-gpu"] = Localizer.Instance["CurveEditorCtrl.AirflowCurveGpu"],
        ["airflow-intake"] = Localizer.Instance["CurveEditorCtrl.AirflowCurveIntake"],
        ["airflow-exhaust"] = Localizer.Instance["CurveEditorCtrl.AirflowCurveExhaust"],
        ["airflow-default"] = Localizer.Instance["CurveEditorCtrl.AirflowCurveDefault"],
    };

    private static string DescribeReason(AirflowFanSuggestion s, string curveName) => s.Reason switch
    {
        AirflowReason.HardwareAuto => Localizer.Instance["CurveEditorCtrl.AirflowReasonHardwareAuto"],
        AirflowReason.NoPositionDefaultCurve => Localizer.Instance["CurveEditorCtrl.AirflowReasonNoPosition"],
        _ => Localizer.Instance.Format("CurveEditorCtrl.AirflowReasonLocationCurve",
            FanLocationOption.For(s.Location).Display, curveName),
    };

    private static string DescribeHint(AirflowHint hint) => hint switch
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

    private static string DescribePressure(AirflowTuneResult r)
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
