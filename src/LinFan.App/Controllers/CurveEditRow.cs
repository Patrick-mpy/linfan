// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>Editierbare Kurve: Name, Quell-Sensor-Mix (mehrere Sensoren + Aggregation), Hysterese, Glättung und Stützpunkte.</summary>
public partial class CurveEditRow : ObservableObject
{
    public string Id { get; }

    /// <summary>Alle Sensoren (zum Auflösen der Quellen) - Referenz auf die Liste des Controllers.</summary>
    public ObservableCollection<SensorOption> Sensors { get; }

    /// <summary>Alle Lüfter (gemeinsame Liste des Controllers) - für das Aktiv-Badge (ist dieser Kurve ein Lüfter zugeordnet?).</summary>
    public ObservableCollection<FanAssignRow> Fans { get; }

    /// <summary>Quell-Auswahl: pro sichtbarem Sensor eine Checkbox (mehrere Sensoren mischbar).</summary>
    public ObservableCollection<SensorCheck> SensorChecks { get; } = new();

    /// <summary>
    /// Die im Editor angezeigte Teilmenge der <see cref="SensorChecks"/>: aktive (ausgewählte) zuerst,
    /// eingeklappt auf die ersten <see cref="CollapsedCount"/>, ausgeklappt alle. Hält die Quell-Liste
    /// übersichtlich, wenn viele Sensoren existieren.
    /// </summary>
    public ObservableCollection<SensorCheck> DisplayedSensorChecks { get; } = new();

    /// <summary>Die angezeigten Quell-Sensoren (<see cref="DisplayedSensorChecks"/>) nach Gruppe gebündelt -
    /// wie die Dashboard-Sensorgruppen; Container für die gruppierte Anzeige im Kurven-Tab.</summary>
    public ObservableCollection<SensorCheckGroup> DisplayedSensorGroups { get; } = new();

    private const int CollapsedCount = 3;

    /// <summary>True = alle Quell-Sensoren zeigen; false = nur die ersten <see cref="CollapsedCount"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleSensorsLabel))]
    private bool _showAllSensors;

    /// <summary>Ob es mehr Sensoren als die eingeklappte Anzahl gibt (steuert die Sichtbarkeit des Toggle-Buttons).</summary>
    public bool HasCollapsibleSensors => SensorChecks.Count > CollapsedCount;

    /// <summary>Beschriftung des Einblenden/Ausblenden-Buttons.</summary>
    public string ToggleSensorsLabel => ShowAllSensors ? Localizer.Instance["CurveEditRow.ShowLess"] : Localizer.Instance.Format("CurveEditRow.ShowAll", SensorChecks.Count);

    public ObservableCollection<PointRow> Points { get; } = new();

    /// <summary>
    /// Kurven-Auswertung (Temp → %) für den gebundenen <see cref="Controls.CurveChart"/>: kapselt die
    /// <see cref="CurveEngine"/>-Auswertung des Daemons, damit die View sie über den Controller bezieht,
    /// statt LinFan.Core.Services direkt zu referenzieren.
    /// </summary>
    public Func<Curve, double, double> CurveEvaluator => CurveEngine.Evaluate;

    /// <summary>Ob die Stützpunkt-Liste aufgeklappt ist - reiner View-Zustand pro Zeile, analog
    /// <see cref="FanAssignRow"/>.ShowAdvanced. Default eingeklappt: der Graph bleibt die primäre
    /// Editierfläche, die Zahlenliste ist sekundäres Detail.</summary>
    [ObservableProperty] private bool _showPoints;

    /// <summary>Zusammenfassung der eingeklappten Punkte-Sektion (Header-Beschriftung).</summary>
    public string PointsLabel => Points.Count == 1
        ? Localizer.Instance["CurveEditRow.PointsOne"]
        : Localizer.Instance.Format("CurveEditRow.PointsMany", Points.Count);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _name;

    [ObservableProperty] private decimal _hysteresis;

    /// <summary>
    /// Averaging window (seconds) for the curve's input temperature, <c>0</c> = off. Initialized to the Core
    /// default so a newly created curve carries it without every call site having to pass it.
    /// </summary>
    [ObservableProperty] private decimal _smoothingSeconds = (decimal)CurveConfig.DefaultSmoothingSeconds;

    /// <summary>
    /// Ob die Kurve aktiv regelt. <c>false</c> = stillgelegt (zugeordnete Lüfter → Hardware-Auto im Daemon).
    /// Am Dashboard live umschaltbar; persistiert. Bewusst <b>aus dem Dirty-Vergleich ausgeklammert</b>
    /// (siehe <see cref="CurveEditorController.SetCurveEnabled"/>) - der Toggle ist sofort persistiert.
    /// </summary>
    [ObservableProperty] private bool _enabled = true;

    /// <summary>Wie die ausgewählten Quell-Sensoren zusammengefasst werden (Max/Avg).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedAggregation))]
    private SensorAggregation _aggregation;

    /// <summary>Auswählbare Aggregationen (für die ComboBox).</summary>
    public IReadOnlyList<AggregationOption> Aggregations => AggregationOption.All;

    /// <summary>Aggregation als Anzeige-Option (für die ComboBox-Bindung).</summary>
    public AggregationOption SelectedAggregation
    {
        get => AggregationOption.For(Aggregation);
        set => Aggregation = value?.Value ?? SensorAggregation.Max;
    }

    /// <summary>Interpolationsmodus der Kurve (Linear oder Spline).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedInterpolation))]
    private InterpolationMode _interpolationMode;

    /// <summary>Auswählbare Interpolationsmodi (für die ComboBox).</summary>
    public IReadOnlyList<InterpolationOption> Interpolations => InterpolationOption.All;

    /// <summary>Interpolationsmodus als Anzeige-Option (für die ComboBox-Bindung).</summary>
    public InterpolationOption SelectedInterpolation
    {
        get => InterpolationOption.For(InterpolationMode);
        set => InterpolationMode = value?.Value ?? InterpolationMode.Linear;
    }

    /// <summary>Aggregierte Live-Temperatur der Quell-Sensoren (live), oder NaN - für den Arbeitspunkt im Graph.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTemperatureDisplay))]
    private double _liveTemperature = double.NaN;

    /// <summary>Formatierte Live-Temperatur fürs Dashboard-Panel (z. B. „48 °C"); „-" ohne lesbare Quelle.</summary>
    public string LiveTemperatureDisplay =>
        double.IsNaN(LiveTemperature) ? "-" : $"{LiveTemperature:0} °C";

    /// <summary>
    /// True, wenn die nach Temperatur sortierten Stützpunkte irgendwo eine <b>sinkende</b> Leistung haben
    /// (percent[i] &lt; percent[i-1]) - d. h. der Lüfter kühlt bei höherer Temperatur weniger. Flache Abschnitte
    /// (gleiche Prozente) sind ok. Sicherheitsrelevanter Hinweis; treibt die Warnung im Kurven-Tab.
    /// </summary>
    [ObservableProperty] private bool _hasDecreasingPercent;

    /// <summary>Aktuell als Quelle ausgewählte Sensoren.</summary>
    public IReadOnlyList<SensorOption> Sources =>
        SensorChecks.Where(c => c.Selected).Select(c => c.Sensor).ToList();

    /// <summary>
    /// True, wenn kein Quell-Sensor ausgewählt ist. Dann bleibt <see cref="LiveTemperature"/> NaN und der
    /// Live-Arbeitspunkt im Graph fehlt - der Kurven-Tab blendet dazu einen Hinweis ein. Change-notifiziert
    /// über <see cref="OnSourceSelectionChanged"/> bei Quell-Auswahländerungen.
    /// </summary>
    public bool HasNoSource => Sources.Count == 0;

    /// <summary>
    /// True, wenn die Kurve „aktiv" ist: mindestens ein Quell-Sensor UND mindestens ein zugeordneter Lüfter.
    /// Treibt das grün/grau-Badge im Seitenmenü. Quelländerung notifiziert über <see cref="OnSourceSelectionChanged"/>;
    /// Zuordnungswechsel notifiziert der Controller über <see cref="NotifyActiveChanged"/>.
    /// </summary>
    public bool IsActive => ProfileIsActive && Sources.Count > 0 && Fans.Any(f => ReferenceEquals(f.Selected, this));

    /// <summary>
    /// Whether the profile this curve belongs to is the one the daemon regulates with. Curves of a profile
    /// that is merely being edited never drive a fan, however complete they look - the badge has to say so.
    /// Set by the controller when a profile is loaded into the editor; true by default, because the curves
    /// mirroring the daemon's live set are active by definition.
    /// </summary>
    public bool ProfileIsActive { get; private set; } = true;

    /// <summary>Setzt <see cref="ProfileIsActive"/> (Controller beim Laden eines Profils in den Editor).</summary>
    public void SetProfileActive(bool active)
    {
        if (ProfileIsActive == active)
            return;
        ProfileIsActive = active;
        NotifyActiveChanged();
    }

    /// <summary>Tooltip-Text zum Aktiv-Badge - erklärt, warum die Kurve aktiv/inaktiv ist.</summary>
    public string ActivityHint => IsActive
        ? Localizer.Instance["CurveEditRow.ActiveHint"]
        : !ProfileIsActive
            ? Localizer.Instance["CurveEditRow.InactiveProfile"]
            : Sources.Count == 0
                ? Localizer.Instance["CurveEditRow.InactiveNoSource"]
                : Localizer.Instance["CurveEditRow.InactiveNoFan"];

    /// <summary>Re-evaluiert das Aktiv-Badge (vom Controller bei Zuordnungswechsel aufgerufen).</summary>
    public void NotifyActiveChanged()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ActivityHint));
        OnPropertyChanged(nameof(AssignedFans));
    }

    /// <summary>Die dieser Kurve zugeordneten Lüfter - für die Kurven-Übersicht im Profil-Editor.</summary>
    public IReadOnlyList<FanAssignRow> AssignedFans =>
        Fans.Where(f => ReferenceEquals(f.Selected, this)).ToList();

    /// <summary>Kurz-Beschreibung der Quell-Sensoren (Name bzw. „n Sensoren"); „-" ohne Quelle.</summary>
    public string SourceSummary => Sources.Count switch
    {
        0 => "-",
        1 => Sources[0].Name,
        int n => Localizer.Instance.Format("CurveEditRow.SourceCount", n),
    };

    /// <summary>Anzeige im Seitenmenü: „Name - Quelle(n)".</summary>
    public string Label
    {
        get
        {
            var srcs = Sources;
            return srcs.Count switch
            {
                0 => Name,
                1 => $"{Name} - {srcs[0].Name}",
                _ => $"{Name} - {Localizer.Instance.Format("CurveEditRow.SourceCount", srcs.Count)}",
            };
        }
    }

    /// <summary>
    /// Feuert, sobald sich ein <b>persistenz-relevantes</b> Feld dieser Kurve ändert (Name, Hysterese,
    /// Aggregation, Interpolation, Quell-Auswahl, Stützpunkte). Der Controller hängt daran seine
    /// Dirty-Erkennung. Bewusst NICHT für <see cref="Enabled"/> (sofort persistiert, siehe
    /// <see cref="CurveEditorController.SetCurveEnabled"/>) oder reine Live-/View-Properties.
    /// </summary>
    public event EventHandler? ConfigChanged;

    private void RaiseConfigChanged() => ConfigChanged?.Invoke(this, EventArgs.Empty);

    // Persistenz-relevante Felder → ConfigChanged. (Enabled bewusst ausgeklammert: rebaselined sich selbst.)
    partial void OnNameChanged(string value) => RaiseConfigChanged();
    partial void OnHysteresisChanged(decimal value) => RaiseConfigChanged();
    partial void OnSmoothingSecondsChanged(decimal value) => RaiseConfigChanged();
    partial void OnAggregationChanged(SensorAggregation value) => RaiseConfigChanged();
    partial void OnInterpolationModeChanged(InterpolationMode value) => RaiseConfigChanged();

    public CurveEditRow(string id, string name, IEnumerable<string> sourceIds, SensorAggregation aggregation,
                        decimal hysteresis, ObservableCollection<SensorOption> sensors,
                        InterpolationMode interpolationMode = InterpolationMode.Linear,
                        ObservableCollection<FanAssignRow>? fans = null)
    {
        Id = id;
        _name = name;
        _aggregation = aggregation;
        _hysteresis = hysteresis;
        _interpolationMode = interpolationMode;
        Sensors = sensors;
        Fans = fans ?? new ObservableCollection<FanAssignRow>();

        BuildSensorChecks(new HashSet<string>(sourceIds));

        Points.CollectionChanged += OnPointsChanged;
    }

    /// <summary>
    /// Rebuilds the source checkboxes after a global visibility change; keeps the current selection.
    /// Called by the controller when a sensor's eye toggle flips (mirror of the fan list, a057c3d).
    /// </summary>
    public void RebuildSensorChecks()
    {
        // Collect the selection BEFORE clearing - Sources reads SensorChecks.
        BuildSensorChecks(new HashSet<string>(Sources.Select(s => s.Id)));
        // Both are plain getters over SensorChecks.Count; the count is no longer constant.
        OnPropertyChanged(nameof(HasCollapsibleSensors));
        OnPropertyChanged(nameof(ToggleSensorsLabel));
    }

    /// <summary>
    /// Hidden sensors are not offered - unless they are a source of THIS curve: hidden is display-only
    /// (regulation keeps running), so an active source stays visible and removable instead of being
    /// silently dropped on save. Iterates the full list to preserve discovery order.
    /// </summary>
    private void BuildSensorChecks(HashSet<string> selected)
    {
        SensorChecks.Clear();
        foreach (SensorOption opt in Sensors.Where(o => o.Visible || selected.Contains(o.Id)))
            SensorChecks.Add(new SensorCheck(opt, selected.Contains(opt.Id)) { SelectionChanged = OnSourceSelectionChanged });
        RebuildDisplayedSensors();
    }

    private void OnSourceSelectionChanged()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(HasNoSource));
        NotifyActiveChanged(); // Quelländerung kann das Aktiv-Badge kippen
        RaiseConfigChanged();  // Quell-Sensor-Mix fließt in SourceSensorIds
    }

    [RelayCommand]
    private void ToggleShowAllSensors() => ShowAllSensors = !ShowAllSensors;

    partial void OnShowAllSensorsChanged(bool value) => RebuildDisplayedSensors();

    /// <summary>Baut <see cref="DisplayedSensorChecks"/> neu: aktive zuerst, eingeklappt auf <see cref="CollapsedCount"/>.</summary>
    private void RebuildDisplayedSensors()
    {
        // OrderBy ist stabil → gleiche Selektion behält die ursprüngliche Reihenfolge; aktive nach vorn.
        IEnumerable<SensorCheck> ordered = SensorChecks.OrderByDescending(c => c.Selected);
        IEnumerable<SensorCheck> shown = ShowAllSensors ? ordered : ordered.Take(CollapsedCount);

        DisplayedSensorChecks.Clear();
        foreach (SensorCheck c in shown)
            DisplayedSensorChecks.Add(c);

        RebuildDisplayedSensorGroups();
    }

    /// <summary>Bündelt die angezeigten Quell-Sensoren nach Gruppe (Ungruppiert zuletzt) - wie die Dashboard-Sensorgruppen.</summary>
    private void RebuildDisplayedSensorGroups()
    {
        static string Key(SensorCheck c) =>
            string.IsNullOrWhiteSpace(c.Sensor.Group) ? SensorGroup.Ungrouped : c.Sensor.Group.Trim();

        DisplayedSensorGroups.Clear();
        IEnumerable<SensorCheck> ordered = DisplayedSensorChecks
            .OrderBy(c => Key(c) == SensorGroup.Ungrouped ? "￿" : Key(c), StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Sensor.Name, StringComparer.OrdinalIgnoreCase);
        // Case-insensitive so "cpu"/"CPU" merge into one block (first-seen casing names the header).
        foreach (IGrouping<string, SensorCheck> grp in ordered.GroupBy(Key, StringComparer.OrdinalIgnoreCase))
        {
            var group = new SensorCheckGroup(grp.Key);
            foreach (SensorCheck c in grp)
                group.Sensors.Add(c);
            DisplayedSensorGroups.Add(group);
        }
    }

    public void AddPointRow(decimal temperature, decimal percent)
    {
        var row = new PointRow(temperature, percent);
        row.RemoveCommand = new RelayCommand(() => Points.Remove(row));
        Points.Add(row);
    }

    /// <summary>Hält die Warnung aktuell: Werteänderung eines Punkts (Add/Remove abonnieren/abmelden) → neu prüfen.</summary>
    private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is { } removed)
            foreach (PointRow row in removed.OfType<PointRow>())
                row.PropertyChanged -= OnPointPropertyChanged;
        if (e.NewItems is { } added)
            foreach (PointRow row in added.OfType<PointRow>())
                row.PropertyChanged += OnPointPropertyChanged;

        OnPropertyChanged(nameof(PointsLabel)); // Zähler im eingeklappten Header folgt Add/Remove
        RecomputeDecreasing();
        RaiseConfigChanged();                   // Stützpunkt hinzugefügt/entfernt
    }

    private void OnPointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PointRow.Temperature) or nameof(PointRow.Percent))
        {
            RecomputeDecreasing();
            RaiseConfigChanged();               // Stützpunkt-Wert geändert
        }
    }

    /// <summary>Sortiert wie beim Speichern nach Temperatur und prüft, ob die Leistung dabei je echt sinkt.</summary>
    private void RecomputeDecreasing()
    {
        List<decimal> percents = Points
            .OrderBy(p => p.Temperature)
            .Select(p => p.Percent)
            .ToList();

        bool decreasing = false;
        for (int i = 1; i < percents.Count; i++)
        {
            if (percents[i] < percents[i - 1])
            {
                decreasing = true;
                break;
            }
        }

        HasDecreasingPercent = decreasing;
    }

    [RelayCommand]
    private void AddPoint() => AddPointRow(50, 50);

    public static CurveEditRow From(CurveConfig c, ObservableCollection<SensorOption> sensors,
                                    ObservableCollection<FanAssignRow>? fans = null)
    {
        // Schema-2-Quellen bevorzugen; sonst (Altbestand) aus dem alten Einzelfeld migrieren.
        IEnumerable<string> sourceIds = CurveSourceResolver.ResolveSources(c.SourceSensorId, c.SourceSensorIds);

        var row = new CurveEditRow(c.Id, c.Name, sourceIds, c.Aggregation, (decimal)c.HysteresisC,
                                   sensors, c.InterpolationMode, fans)
        {
            Enabled = c.Enabled,
            SmoothingSeconds = (decimal)c.SmoothingSeconds,
        };
        foreach (CurvePoint p in c.Points.OrderBy(p => p.TemperatureC))
            row.AddPointRow((decimal)p.TemperatureC, (decimal)p.Percent);
        return row;
    }

    public CurveConfig ToConfig() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        SourceSensorIds = Sources.Select(s => s.Id).ToList(),
        Aggregation = Aggregation,
        HysteresisC = (double)Hysteresis,
        SmoothingSeconds = (double)SmoothingSeconds,
        InterpolationMode = InterpolationMode,
        Points = Points
            .OrderBy(p => p.Temperature)
            .Select(p => new CurvePoint((double)p.Temperature, (double)p.Percent))
            .ToList(),
    };

    public override string ToString() => Name;
}
