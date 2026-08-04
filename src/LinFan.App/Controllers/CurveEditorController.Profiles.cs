// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

// Profil-Verwaltung (Anlegen/Duplizieren/Umbenennen/Löschen, Laden).
public partial class CurveEditorController
{
    // --- Profile ---------------------------------------------------------------

    partial void OnSelectedProfileChanged(ProfileRow? oldValue, ProfileRow? newValue)
    {
        IsNamingProfile = false; // jeder Profilwechsel schließt das Namensfeld (Anlegen/Duplizieren setzt es danach neu)
        if (_applyingProfile || newValue is null)
            return;
        // Aktuellen Editor-Stand ins bisherige Profil sichern, dann das neue Profil laden.
        if (oldValue is not null)
        {
            oldValue.Curves = CurrentCurveConfigs();
            oldValue.Assignments = CurrentAssignments();
        }
        ApplyProfileToEditor(newValue);
        _ = _activateProfile?.Invoke(newValue.Id); // bereits gespeicherte Profile sofort live umschalten
        MarkDirty(); // aktiver Profilwechsel ändert ActiveProfileId (ApplyProfileToEditor markiert die Kurven schon)
    }

    /// <summary>Legt ein <b>leeres</b> Profil an: genau eine Default-Kurve, keine Lüfter-Zuordnungen.</summary>
    [RelayCommand]
    private void AddProfile()
    {
        var row = new ProfileRow($"profile-{Guid.NewGuid():N}"[..16], Localizer.Instance["CurveEditorCtrl.NewProfileName"],
                                 new[] { BuildDefaultCurveConfig() }, Array.Empty<ProfileAssignment>());
        Profiles.Add(row);
        _applyingProfile = true;
        SelectedProfile = row;
        _applyingProfile = false;
        ApplyProfileToEditor(row); // die Default-Kurve in den Editor laden (Zuordnungen bleiben leer)
        IsNamingProfile = true;    // Namensfeld zum Benennen einblenden
    }

    /// <summary>Dupliziert das aktuelle Profil (Kurven + Zuordnungen) als „… (Kopie)".</summary>
    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is not { } source)
            return;
        var row = new ProfileRow($"profile-{Guid.NewGuid():N}"[..16], Localizer.Instance.Format("CurveEditorCtrl.CopySuffix", source.Name),
                                 CurrentCurveConfigs(), CurrentAssignments());
        Profiles.Add(row);
        _applyingProfile = true; // Kopie = aktueller Editor-Stand, kein Umschalten nötig
        SelectedProfile = row;
        _applyingProfile = false;
        IsNamingProfile = true;
    }

    /// <summary>Blendet das Namensfeld für das aktuelle Profil ein (Umbenennen).</summary>
    [RelayCommand]
    private void RenameProfile()
    {
        if (SelectedProfile is not null)
            IsNamingProfile = true;
    }

    /// <summary>Schließt das Namensfeld wieder (Fertig).</summary>
    [RelayCommand]
    private void FinishNamingProfile() => IsNamingProfile = false;

    /// <summary>Baut eine Default-Kurve (erster sichtbarer Sensor als Quelle, Standard-Stützpunkte).</summary>
    private CurveConfig BuildDefaultCurveConfig()
    {
        SensorOption? source = VisibleSensors.FirstOrDefault() ?? Sensors.FirstOrDefault();
        return new CurveConfig
        {
            Id = $"curve-{Guid.NewGuid():N}"[..14],
            Name = Localizer.Instance["CurveEditorCtrl.NewCurveName"],
            SourceSensorIds = source is null ? Array.Empty<string>() : new[] { source.Id },
            Aggregation = SensorAggregation.Max,
            HysteresisC = 2.0,
            InterpolationMode = InterpolationMode.Linear,
            Points = DefaultCurvePoints.Select(p => new CurvePoint((double)p.Temp, (double)p.Percent)).ToList(),
        };
    }

    /// <summary>Löscht das aktuelle Profil. Auto-Save-Verhalten wie bei <see cref="DeleteCurve"/> (bedingt, nur wenn vorher sauber).</summary>
    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (SelectedProfile is not { } removed)
            return;

        bool wasClean = !HasUnsavedChanges; // VOR jeder Mutation lesen

        Profiles.Remove(removed);
        ProfileRow? next = Profiles.FirstOrDefault();
        _applyingProfile = true;
        SelectedProfile = next;
        _applyingProfile = false;
        if (next is not null)
            ApplyProfileToEditor(next);

        if (wasClean)
            await Save();
    }

    /// <summary>Lädt die Kurven + Zuordnungen eines Profils in den Editor.</summary>
    private void ApplyProfileToEditor(ProfileRow profile) =>
        ReloadEditor(profile.Curves, profile.Assignments);

    /// <summary>Lädt Kurven + Lüfter-Zuordnungen in den Editor (für Profilwechsel und Airflow-Übernahme).</summary>
    private void ReloadEditor(IReadOnlyList<CurveConfig> curves, IReadOnlyList<ProfileAssignment> assignments)
    {
        Curves.Clear();
        foreach (CurveConfig c in curves)
            Curves.Add(CurveEditRow.From(c, Sensors, Fans));

        Dictionary<string, string?> map = assignments.ToDictionary(a => a.FanId, a => a.CurveId);
        foreach (FanAssignRow fan in Fans)
            fan.Selected = map.TryGetValue(fan.FanId, out string? curveId)
                ? Curves.FirstOrDefault(c => c.Id == curveId)
                : null;

        SelectedCurve = Curves.FirstOrDefault();
        RebuildSelectedCurveFans(); // auch wenn SelectedCurve unverändert null bleibt
    }

    private List<CurveConfig> CurrentCurveConfigs() => Curves.Select(c => c.ToConfig()).ToList();

    private List<ProfileAssignment> CurrentAssignments() =>
        Fans.Select(f => new ProfileAssignment(f.FanId, f.Selected?.Id)).ToList();
}
