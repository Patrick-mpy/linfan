// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

// Airflow-Auto-Tune: bisheriges Ergebnis, Analyse, Übernahme, Druckbilanz-Text.
public partial class CurveEditorController
{
    // --- Bisheriges Ergebnis ---------------------------------------------------

    /// <summary>
    /// Lüfter des bearbeiteten Profils, die bereits einer Airflow-Rollen-Kurve folgen - der „schon
    /// durchgeführt"-Zustand. Abgeleitet aus den stabilen <c>airflow-*</c>-Kurven-Ids statt aus einer
    /// eigenen Merker-Datei: die Zuordnung <i>ist</i> das Ergebnis, und sie gilt genauso nach dem
    /// Onboarding (das die Profile aus derselben Analyse baut).
    /// </summary>
    public ObservableCollection<AirflowStatusRow> AirflowStatus { get; } = new();

    /// <summary>True, sobald mindestens ein Lüfter einer Airflow-Kurve folgt (zeigt den Ergebnis-Block).</summary>
    [ObservableProperty] private bool _hasAirflowStatus;

    /// <summary>Druckbilanz der aktuellen Positionen - dieselbe Aussage wie in der Vorschau, laufend nachgezogen.</summary>
    [ObservableProperty] private string _airflowStatusPressureText = "";

    /// <summary>
    /// Wertet den Airflow-Zustand neu aus. Aufgerufen nach jedem Neuaufbau des Editors (Initialisierung,
    /// Profilwechsel, Airflow-Übernahme) und bei jeder Änderung, die ihn kippen kann: Zuordnung, Position
    /// (Druckbilanz) und Sichtbarkeit - ausgeblendete Lüfter sind für die Analyse „nicht vorhanden" und
    /// zählen hier ebenso wenig.
    /// </summary>
    private void RefreshAirflowStatus()
    {
        AirflowStatus.Clear();
        foreach (FanAssignRow fan in Fans)
        {
            if (fan.Visible && fan.Selected is { } curve && AirflowTuneService.IsAirflowCurveId(curve.Id))
                AirflowStatus.Add(new AirflowStatusRow(fan, curve));
        }

        HasAirflowStatus = AirflowStatus.Count > 0;
        AirflowStatusPressureText = HasAirflowStatus
            ? AirflowText.DescribePressure(AirflowTuneService.Analyze(BuildConfig()))
            : "";
    }

    // --- Airflow-Auto-Tune -----------------------------------------------------

    /// <summary>
    /// Analysiert die aktuelle Konfiguration (Lüfter-Positionen → Druckbilanz + rollenbasierte Kurven)
    /// und füllt die Vorschau. Schreibt nichts - der Vorschlag wird erst mit <see cref="ApplyAirflowCommand"/>
    /// in den Editor übernommen und dann gespeichert.
    /// </summary>
    [RelayCommand]
    private void AnalyzeAirflow()
    {
        if (!_initialized)
            return;

        // Lokalisierte Kurven-Namen hereinreichen - sie werden bei „Übernehmen" persistiert (Muster
        // wie bei den Onboarding-Profilen); Hints/Reasons kommen als Codes und werden hier übersetzt.
        AirflowTuneResult result = AirflowTuneService.Analyze(BuildConfig(), AirflowText.CurveNames());
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
                AirflowText.DescribeReason(s, curveName), apply: s.SuggestedCurveId is not null));
        }

        AirflowHints.Clear();
        foreach (AirflowHint hint in result.Hints)
            AirflowHints.Add(AirflowText.DescribeHint(hint));

        AirflowPressureText = AirflowText.DescribePressure(result);
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

        // Nur angekreuzte Lüfter übernehmen - die Kurven-Filterung liegt (testbar) im Core.
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

}
