// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HysteresisLabel), nameof(HasHysteresisBand))]
    private decimal _hysteresis;

    /// <summary>Caption of the hysteresis band in the chart, e.g. „±2°".</summary>
    public string HysteresisLabel => $"±{Hysteresis.ToString("0.#", CultureInfo.InvariantCulture)}°";

    /// <summary>
    /// Whether the chart shades the hysteresis band: it is drawn around the live value, so it needs one. The band is an
    /// approximation - the daemon's dead band is centred on the last applied (smoothed) temperature per fan, which the
    /// snapshot does not carry.
    /// </summary>
    public bool HasHysteresisBand => Hysteresis > 0 && !double.IsNaN(LiveTemperature);

    /// <summary>
    /// Averaging window (seconds) for the curve's input temperature, <c>0</c> = off. Initialized to the Core
    /// default so a newly created curve carries it without every call site having to pass it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmoothingHint))]
    private decimal _smoothingSeconds = (decimal)CurveConfig.DefaultSmoothingSeconds;

    /// <summary>What the smoothing does at its current value - „Aus …" at 0.</summary>
    public string SmoothingHint => SmoothingSeconds == 0
        ? Localizer.Instance["CurveEditRow.SmoothingOff"]
        : Localizer.Instance.Format("CurveEditRow.SmoothingOn", SmoothingSeconds.ToString("0.#", CultureInfo.InvariantCulture));

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

    /// <summary>The aggregations offered (the segmented switch).</summary>
    public IReadOnlyList<AggregationOption> Aggregations => AggregationOption.All;

    /// <summary>
    /// The aggregation as a display option (bound by the segmented switch). <c>null</c> is ignored: a list box writes it
    /// when it loses its selection (e.g. Ctrl+click), and that must not turn into a silent switch to Max.
    /// </summary>
    public AggregationOption SelectedAggregation
    {
        get => AggregationOption.For(Aggregation);
        set
        {
            if (value is not null)
                Aggregation = value.Value;
        }
    }

    /// <summary>Interpolationsmodus der Kurve (Linear oder Spline).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedInterpolation))]
    private InterpolationMode _interpolationMode;

    /// <summary>The interpolation modes offered (the segmented switch).</summary>
    public IReadOnlyList<InterpolationOption> Interpolations => InterpolationOption.All;

    /// <summary>The interpolation mode as a display option (bound by the segmented switch). <c>null</c> is ignored, see <see cref="SelectedAggregation"/>.</summary>
    public InterpolationOption SelectedInterpolation
    {
        get => InterpolationOption.For(InterpolationMode);
        set
        {
            if (value is not null)
                InterpolationMode = value.Value;
        }
    }

    /// <summary>Aggregierte Live-Temperatur der Quell-Sensoren (live), oder NaN - für den Arbeitspunkt im Graph.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveTemperatureDisplay), nameof(HasHysteresisBand))]
    private double _liveTemperature = double.NaN;

    /// <summary>Formatierte Live-Temperatur fürs Dashboard-Panel (z. B. „48 °C"); „-" ohne lesbare Quelle.</summary>
    public string LiveTemperatureDisplay =>
        double.IsNaN(LiveTemperature) ? "-" : $"{LiveTemperature:0} °C";

    /// <summary>
    /// Unsaved changes in this curve (its settings or the fans following it) compared with the saved state of
    /// its profile. Set by the controller's diff; display only, never part of the config.
    /// </summary>
    [ObservableProperty] private bool _isDirty;

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

    /// <summary>Two or more sources - only then does the mix (Max/Avg) matter, so the editor shows it only then.</summary>
    public bool HasMultipleSources => Sources.Count >= 2;

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

    /// <summary>The texts built in code follow a language switch (called by the controller).</summary>
    internal void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(PointsLabel));
        OnPropertyChanged(nameof(SmoothingHint));
        OnPropertyChanged(nameof(ActivityHint));
        OnPropertyChanged(nameof(LowerLimitLabel));
        OnPropertyChanged(nameof(UpperLimitLabel));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(Label));
        foreach (PointRow point in Points)
            point.NotifyLanguageChanged();
    }

    /// <summary>Re-evaluiert das Aktiv-Badge (vom Controller bei Zuordnungswechsel aufgerufen).</summary>
    public void NotifyActiveChanged()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ActivityHint));
        OnPropertyChanged(nameof(AssignedFans));
        NotifyLimitsChanged();
    }

    /// <summary>Die dieser Kurve zugeordneten Lüfter - für die Kurven-Übersicht im Profil-Editor.</summary>
    public IReadOnlyList<FanAssignRow> AssignedFans =>
        Fans.Where(f => ReferenceEquals(f.Selected, this)).ToList();

    // --- Power limits of the assigned fans (the chart's grey bands) ------------------------------

    // Only controllable fans: the daemon applies Min/Max when it writes PWM, a read-only fan never gets either.
    private IEnumerable<FanAssignRow> LimitingFans => Fans.Where(f => f.CanControl && ReferenceEquals(f.Selected, this));

    // The narrowest limit wins - the one the curve runs into first. Ties keep the first fan.
    private FanAssignRow? LowestFloorFan => LimitingFans.Where(f => f.MinPercent > 0).MaxBy(f => f.MinPercent);

    private FanAssignRow? LowestCeilingFan => LimitingFans.Where(f => f.MaxPercent < 100).MinBy(f => f.MaxPercent);

    /// <summary>The highest minimum (%) among the assigned controllable fans, or <c>null</c> when none has one.</summary>
    public double? LowerLimit => LowestFloorFan?.MinPercent;

    /// <summary>The lowest maximum (%) among the assigned controllable fans, or <c>null</c> when none has one.</summary>
    public double? UpperLimit => LowestCeilingFan?.MaxPercent;

    /// <summary>„Min 25 % · CPU" - the limit and the fan it comes from.</summary>
    public string? LowerLimitLabel => LowestFloorFan is { } fan
        ? Localizer.Instance.Format("CurveEditRow.LimitMin", fan.MinPercent, fan.DisplayName)
        : null;

    /// <summary>„Max 80 % · Front" - the limit and the fan it comes from.</summary>
    public string? UpperLimitLabel => LowestCeilingFan is { } fan
        ? Localizer.Instance.Format("CurveEditRow.LimitMax", fan.MaxPercent, fan.DisplayName)
        : null;

    /// <summary>Whether the chart shades a limit at all (drives its caption).</summary>
    public bool HasLimits => LowerLimit is not null || UpperLimit is not null;

    /// <summary>Re-reads the limits - called when an assignment or a fan's Min/Max/name changes.</summary>
    public void NotifyLimitsChanged()
    {
        OnPropertyChanged(nameof(LowerLimit));
        OnPropertyChanged(nameof(UpperLimit));
        OnPropertyChanged(nameof(LowerLimitLabel));
        OnPropertyChanged(nameof(UpperLimitLabel));
        OnPropertyChanged(nameof(HasLimits));
    }

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
    public void RebuildSensorChecks() =>
        // Collect the selection BEFORE clearing - Sources reads SensorChecks.
        BuildSensorChecks(new HashSet<string>(Sources.Select(s => s.Id)));

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
    }

    private void OnSourceSelectionChanged()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(HasNoSource));
        OnPropertyChanged(nameof(HasMultipleSources));
        NotifyActiveChanged(); // Quelländerung kann das Aktiv-Badge kippen
        RaiseConfigChanged();  // Quell-Sensor-Mix fließt in SourceSensorIds
    }

    // --- Points -----------------------------------------------------------------------------------

    /// <summary>At least one point stays: an empty curve has no shape (the engine would run the fans at 100 %).</summary>
    private bool CanRemovePoint => Points.Count > 1;

    /// <summary>Appends a point (loading, tests); the editor's own adds keep the list in temperature order.</summary>
    public void AddPointRow(decimal temperature, decimal percent) => Points.Add(CreatePointRow(temperature, percent));

    private PointRow CreatePointRow(decimal temperature, decimal percent)
    {
        var row = new PointRow(temperature, percent);
        // The row's own command, so the remove button and Delete in the chart share one rule.
        row.RemoveCommand = new RelayCommand(() =>
        {
            if (CanRemovePoint)
                Points.Remove(row);
        }, () => CanRemovePoint);
        return row;
    }

    /// <summary>Inserts before the first hotter point - a sorted list stays sorted, so the numbering follows the curve.</summary>
    private void InsertPoint(decimal temperature, decimal percent)
    {
        int index = 0;
        while (index < Points.Count && Points[index].Temperature <= temperature)
            index++;
        Points.Insert(index, CreatePointRow(temperature, percent));
    }

    private bool IsTemperatureFree(decimal temperature) => Points.All(p => p.Temperature != temperature);

    /// <summary>
    /// The point „Punkt hinzufügen" adds: 10 °C above the hottest point at its percentage; once that point is above
    /// 90 °C, halfway between it and the one before. <c>null</c> when there is no room (point limit, or the
    /// temperature is taken).
    /// </summary>
    private (decimal Temperature, decimal Percent)? NextPoint()
    {
        if (Points.Count >= ConfigLimits.MaxPointsPerCurve)
            return null;
        List<PointRow> sorted = Points.OrderBy(p => p.Temperature).ToList();
        if (sorted.Count == 0)
            return (50, 50);

        PointRow last = sorted[^1];
        (decimal Temperature, decimal Percent) next;
        if (last.Temperature <= 90)
        {
            next = (last.Temperature + 10, last.Percent);
        }
        else
        {
            decimal t0 = sorted.Count > 1 ? sorted[^2].Temperature : 0;
            decimal p0 = sorted.Count > 1 ? sorted[^2].Percent : 0;
            next = (Math.Round((t0 + last.Temperature) / 2, MidpointRounding.AwayFromZero),
                    Math.Round((p0 + last.Percent) / 2, MidpointRounding.AwayFromZero));
        }
        return IsTemperatureFree(next.Temperature) ? next : null;
    }

    private bool CanAddPoint() => NextPoint() is not null;

    [RelayCommand(CanExecute = nameof(CanAddPoint))]
    private void AddPoint()
    {
        if (NextPoint() is { } next)
            InsertPoint(next.Temperature, next.Percent);
    }

    /// <summary>A double-click in the chart may add a point unless the curve is full or the temperature is taken.</summary>
    private bool CanAddPointAt(ChartPoint point) =>
        Points.Count < ConfigLimits.MaxPointsPerCurve && IsTemperatureFree(point.Temperature);

    /// <summary>The chart's double-click (<c>CurveChart.AddPointCommand</c>).</summary>
    [RelayCommand(CanExecute = nameof(CanAddPointAt))]
    private void AddPointAt(ChartPoint point)
    {
        if (CanAddPointAt(point))
            InsertPoint(point.Temperature, Math.Clamp(point.Percent, 0, 100));
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

        for (int i = 0; i < Points.Count; i++)
        {
            Points[i].Number = i + 1;
            (Points[i].RemoveCommand as IRelayCommand)?.NotifyCanExecuteChanged(); // the count crossed 1 or not
        }
        NotifyAddPointChanged();
        OnPropertyChanged(nameof(PointsLabel)); // Zähler im eingeklappten Header folgt Add/Remove
        RecomputeDecreasing();
        RaiseConfigChanged();                   // Stützpunkt hinzugefügt/entfernt
    }

    private void OnPointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PointRow.Temperature) or nameof(PointRow.Percent))
        {
            if (e.PropertyName == nameof(PointRow.Temperature))
                NotifyAddPointChanged(); // which temperatures are taken changed
            RecomputeDecreasing();
            RaiseConfigChanged();               // Stützpunkt-Wert geändert
        }
    }

    private void NotifyAddPointChanged()
    {
        AddPointCommand.NotifyCanExecuteChanged();
        AddPointAtCommand.NotifyCanExecuteChanged();
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

    public static CurveEditRow From(CurveConfig c, ObservableCollection<SensorOption> sensors,
                                    ObservableCollection<FanAssignRow>? fans = null)
    {
        // Schema-2-Quellen bevorzugen; sonst (Altbestand) aus dem alten Einzelfeld migrieren.
        IEnumerable<string> sourceIds = CurveSourceResolver.ResolveSources(c.SourceSensorId, c.SourceSensorIds);

        var row = new CurveEditRow(c.Id, c.Name, sourceIds, c.Aggregation,
                                   ToEditor(c.HysteresisC, 0, ConfigSanitizer.MaxHysteresisC, CurveConfig.DefaultHysteresisC),
                                   sensors, c.InterpolationMode, fans)
        {
            Enabled = c.Enabled,
            SmoothingSeconds = ToEditor(c.SmoothingSeconds, 0, ConfigSanitizer.MaxSmoothingSeconds,
                                        CurveConfig.DefaultSmoothingSeconds),
        };
        foreach (CurvePoint p in c.Points.Where(p => double.IsFinite(p.TemperatureC) && double.IsFinite(p.Percent))
                                         .OrderBy(p => p.TemperatureC))
        {
            row.AddPointRow(ToEditor(p.TemperatureC, ConfigSanitizer.MinPointTempC, ConfigSanitizer.MaxPointTempC, 0),
                            ToEditor(p.Percent, 0, 100, 0));
        }
        return row;
    }

    /// <summary>
    /// The curve came through the daemon, which takes it from any client: clamp to the editor's range instead of
    /// a bare <c>(decimal)</c> cast, which throws above ~7.9e28 and on NaN - on the UI thread, ending the GUI.
    /// </summary>
    private static decimal ToEditor(double value, double min, double max, double fallback) =>
        (decimal)(double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback);

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
