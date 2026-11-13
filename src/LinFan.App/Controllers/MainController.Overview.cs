// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

// The overview tab: status tiles, hints, control loops, temperature tiles and the fan table. Everything is derived from
// the last connected snapshot - offline it stays as it was (frozen, dimmed) instead of emptying.
public partial class MainController
{
    private const int MaxLoopHistory = 60;

    private readonly Dictionary<string, ObservableCollection<double>> _loopHistory = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openGroupKeys = new(StringComparer.OrdinalIgnoreCase);
    private string _loopsSignature = "";
    private string _visibleFansSignature = "";
    private MonitorSnapshot? _lastLive;
    private DateTimeOffset _lastLiveAt;
    private bool _hintsConnected;

    /// <summary>One row per curve of the running profile that has a sensor and a fan.</summary>
    public ObservableCollection<LoopRow> Loops { get; } = new();

    /// <summary>The hints behind the status tile, heaviest first.</summary>
    public ObservableCollection<OverviewHint> Hints { get; } = new();

    /// <summary>Asks the view to scroll to a section (pure UI, done in the code-behind).</summary>
    public event EventHandler<OverviewSection>? OverviewScrollRequested;

    /// <summary>True once a connected snapshot arrived - before that the overview says "Connecting …".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverviewConnecting), nameof(OverviewFrozen), nameof(ShowNoSensors), nameof(ShowNoFans))]
    private bool _hasLiveData;

    /// <summary>No connected snapshot yet: the overview shows "Connecting …", never "no sensors detected".</summary>
    public bool OverviewConnecting => !HasLiveData;

    /// <summary>Offline after having been connected: the overview shows the last state, dimmed, with its time.</summary>
    public bool OverviewFrozen => HasLiveData && !Connected;

    /// <summary>"As of 14:03:12" - when the frozen state was received.</summary>
    [ObservableProperty] private string _frozenAtText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIsNotice), nameof(StatusIsCritical))]
    private OverviewStatus _overviewStatus = new(OverviewLevel.Ok, "", "", null);

    public bool StatusIsNotice => OverviewStatus.Level == OverviewLevel.Notice;
    public bool StatusIsCritical => OverviewStatus.Level == OverviewLevel.Critical;

    /// <summary>The hottest-sensor, fans and profile tiles and the section captions.</summary>
    [ObservableProperty] private OverviewTiles _tiles = OverviewTiles.Empty;

    /// <summary>The hint list below the tiles is open (only possible while there are hints).</summary>
    [ObservableProperty] private bool _isHintsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoLoops))]
    private bool _hasLoops;

    public bool ShowNoLoops => !HasLoops;

    [RelayCommand(CanExecute = nameof(CanToggleHints))]
    private void ToggleHints() => IsHintsOpen = !IsHintsOpen;

    private bool CanToggleHints() => Hints.Count > 0;

    [RelayCommand]
    private void ShowTemperatures() => OverviewScrollRequested?.Invoke(this, OverviewSection.Temperatures);

    [RelayCommand]
    private void ShowFans() => OverviewScrollRequested?.Invoke(this, OverviewSection.Fans);

    /// <summary>The profile tile: the running profile in the curves tab's profile pane.</summary>
    [RelayCommand]
    private void OpenRunningProfile()
    {
        SelectRunningProfile();
        Editor.Pane = CurveTabPane.Profile;
        SelectedTabIndex = 1;
    }

    /// <summary>
    /// Opens a curve of the running profile in the editor. A curve the editor no longer has (deleted there, not saved
    /// yet) falls back to the running profile's page instead of whatever curve happens to be selected.
    /// </summary>
    public void EditCurve(string curveId)
    {
        SelectRunningProfile();
        if (Editor.Curves.FirstOrDefault(c => c.Id == curveId) is { } row)
        {
            Editor.SelectedCurve = row;
            Editor.Pane = CurveTabPane.Curve;
        }
        else
        {
            Editor.Pane = CurveTabPane.Profile;
        }
        SelectedTabIndex = 1;
    }

    /// <summary>Opens a profile - and optionally one of its curves - in the curves tab (links of the airflow page).</summary>
    public void OpenInEditor(EditorJump jump)
    {
        if (Editor.Profiles.FirstOrDefault(p => p.Id == jump.ProfileId) is { } profile && !ReferenceEquals(Editor.SelectedProfile, profile))
            Editor.SelectedProfile = profile;
        if (jump.CurveId is { } curveId && Editor.Curves.FirstOrDefault(c => c.Id == curveId) is { } curve)
        {
            Editor.SelectedCurve = curve;
            Editor.Pane = CurveTabPane.Curve;
        }
        else
        {
            Editor.Pane = CurveTabPane.Profile;
        }
        SelectedTabIndex = 1;
    }

    /// <summary>The overview mirrors the daemon, so its links lead to the running profile, not to the one being edited.</summary>
    private void SelectRunningProfile()
    {
        if (Editor.ActiveProfile is { } active && !ReferenceEquals(Editor.SelectedProfile, active))
            Editor.SelectedProfile = active;
    }

    /// <summary>Recomputes the overview from the last connected snapshot (every tick, connected or not).</summary>
    private void ApplyOverview(MonitorSnapshot snapshot)
    {
        if (snapshot.Connected)
        {
            _lastLive = snapshot;
            _lastLiveAt = _clock.GetLocalNow();
            HasLiveData = true;
        }
        if (_lastLive is not { } live)
            return;

        FrozenAtText = Localizer.Instance.Format("Overview.FrozenAt",
            _lastLiveAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        // Every tick, also offline: the signature holds the "ungrouped" label, so a language switch renames that tile.
        RebuildSensorGroupsIfChanged(live.Config.SensorGroups);
        ApplyFanControl(live);
        RebuildLoopsIfChanged(live);
        foreach (LoopRow loop in Loops)
        {
            bool daemonEnabled = live.Config.Curves.FirstOrDefault(c => c.Id == loop.Curve.Id)?.Enabled ?? loop.Curve.Enabled;
            loop.Refresh(canToggle: Connected, daemonEnabled);
        }
        foreach (SensorGroup tile in SensorGroups)
            tile.Refresh();
        ApplyHints(live);
        Tiles = OverviewTiles.Build(live, Connected, Editor.HasUnsavedChanges);
    }

    private void ApplyFanControl(MonitorSnapshot live)
    {
        string blocked = DeviceActionBlock.Reason(Connected, live.DryRun, DeviceActionBlock.Busy(live));
        foreach (FanReading reading in live.Fans)
        {
            if (!_fanRows.TryGetValue(reading.Id, out FanRow? row))
                continue;
            row.Control = FanControlResolver.Resolve(reading, live, Connected);
            row.OverriddenCurveName = FanControlResolver.AssignedCurve(reading.Id, live.Config) is { Enabled: true } curve
                ? curve.Name
                : null;
            row.IsOffline = !Connected;
            row.IsDryRun = live.DryRun;
            row.CalibrateBlockedReason = DeviceActionBlock.CalibrateReason(blocked, live, reading);
            row.RefreshTexts();
        }
    }

    private void ApplyHints(MonitorSnapshot live)
    {
        OverviewHints.Sync(Hints, OverviewHints.Build(live, Connection.Problem));
        foreach (OverviewHint hint in Hints)
        {
            if (hint.Command is null)
            {
                OverviewHint bound = hint;
                hint.Command = new RelayCommand(() => ActOnHint(bound), () => !bound.NeedsDaemon || Connected);
            }
            else if (_hintsConnected != Connected)
            {
                ((RelayCommand)hint.Command).NotifyCanExecuteChanged();
            }
        }
        _hintsConnected = Connected;
        ToggleHintsCommand.NotifyCanExecuteChanged();
        if (Hints.Count == 0)
            IsHintsOpen = false;
        OverviewStatus = OverviewStatus.From(Hints);
        ApplyNavBadge();
    }

    private void ActOnHint(OverviewHint hint)
    {
        switch (hint.Action)
        {
            case HintAction.ShowTemperatureGroup:
                string key = hint.Target ?? "";
                _openGroupKeys.Add(key);
                foreach (SensorGroup tile in SensorGroups.Where(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)))
                    tile.IsOpen = true;
                OverviewScrollRequested?.Invoke(this, OverviewSection.Temperatures);
                break;
            case HintAction.BackToCurve when hint.Target is { } fanId && _fanRows.TryGetValue(fanId, out FanRow? fan):
                if (fan.BackToCurveCommand.CanExecute(null))
                    fan.BackToCurveCommand.Execute(null);
                break;
            case HintAction.EnableCurve when Connected && Editor.LiveCurves.FirstOrDefault(c => c.Id == hint.Target) is { } curve:
                Editor.SetCurveEnabled(curve, true);
                break;
            case HintAction.OpenCurve when hint.Target is { } curveId:
                EditCurve(curveId);
                break;
            case HintAction.ShowFans:
                OverviewScrollRequested?.Invoke(this, OverviewSection.Fans);
                break;
            // The fan row's own command: the same lock (offline, dry run, another device action) as its button.
            case HintAction.Calibrate when hint.Target is { } fanId && _fanRows.TryGetValue(fanId, out FanRow? fan):
                fan.CalibrateCommand.Execute(null);
                break;
            case HintAction.ReviewHardware:
                ReviewHardwareChanges();
                break;
        }
    }

    /// <summary>
    /// Brings the control loops in line with the daemon's set (<c>Editor.LiveCurves</c> + the config's assignments) - not
    /// the editor: another profile may be open there, and even the running one may carry unsaved edits that regulate
    /// nothing yet. Every fan the curve drives counts, hidden ones too. Rows are kept per curve id and re-pointed at the
    /// rebuilt curve, so a toggle (which rebuilds the set once the daemon confirms it) does not take the focus away.
    /// </summary>
    private void RebuildLoopsIfChanged(MonitorSnapshot live)
    {
        var fansByCurve = new Dictionary<string, List<FanRow>>(StringComparer.Ordinal);
        foreach (FanConfig fan in live.Config.Fans)
        {
            if (fan.AssignedCurveId is not { } curveId || _fanRows.GetValueOrDefault(fan.FanId) is not { } row)
                continue;
            if (!fansByCurve.TryGetValue(curveId, out List<FanRow>? list))
                fansByCurve[curveId] = list = new List<FanRow>();
            if (!list.Contains(row)) // a hand-edited config may list a fan twice
                list.Add(row);
        }

        var rows = new List<(CurveEditRow Curve, List<FanRow> Fans)>();
        foreach (CurveEditRow curve in Editor.LiveCurves)
        {
            if (curve.Sources.Count > 0 && fansByCurve.TryGetValue(curve.Id, out List<FanRow>? fans) && fans.Count > 0)
                rows.Add((curve, fans));
        }

        // The revision belongs in the signature: an edited and saved curve keeps its id, but its row is replaced - without
        // it the loop would stay on the discarded row, which no longer receives live values.
        string signature = Editor.LiveCurvesRevision + "␝" + string.Join("|", rows.Select(r =>
            $"{r.Curve.Id}␟{string.Join(",", r.Fans.Select(f => f.FanId))}␟{string.Join(",", r.Curve.Sources.Select(s => s.Id))}"));
        if (signature != _loopsSignature)
        {
            _loopsSignature = signature;
            var existing = Loops.ToDictionary(l => l.Curve.Id, StringComparer.Ordinal);
            var target = new List<LoopRow>();
            foreach (var (curve, fans) in rows)
            {
                if (existing.TryGetValue(curve.Id, out LoopRow? kept))
                {
                    kept.Rebind(curve, fans);
                    target.Add(kept);
                }
                else
                {
                    string curveId = curve.Id;
                    target.Add(new LoopRow(curve, fans, LoopHistory(curveId),
                        enabled => SetLoopEnabled(curveId, enabled),
                        () => EditCurve(curveId)));
                }
            }
            for (int i = Loops.Count - 1; i >= 0; i--)
            {
                if (!target.Contains(Loops[i]))
                    Loops.RemoveAt(i);
            }
            for (int i = 0; i < target.Count; i++)
            {
                int at = Loops.IndexOf(target[i]);
                if (at < 0)
                    Loops.Insert(i, target[i]);
                else if (at != i)
                    Loops.Move(at, i);
            }
            HasLoops = Loops.Count > 0;
            foreach (string stale in _loopHistory.Keys.Where(id => Editor.LiveCurves.All(c => c.Id != id)).ToList())
                _loopHistory.Remove(stale);
        }

        if (!Connected)
            return; // frozen: no new points
        foreach (CurveEditRow curve in Editor.LiveCurves)
        {
            if (double.IsNaN(curve.LiveTemperature))
                continue;
            ObservableCollection<double> history = LoopHistory(curve.Id);
            history.Add(curve.LiveTemperature);
            while (history.Count > MaxLoopHistory)
                history.RemoveAt(0);
        }
    }

    private ObservableCollection<double> LoopHistory(string curveId)
    {
        if (!_loopHistory.TryGetValue(curveId, out ObservableCollection<double>? history))
            _loopHistory[curveId] = history = new ObservableCollection<double>();
        return history;
    }

    /// <summary>
    /// The loop switch. Offline it is locked in the view and refused by the editor (see <c>SetCurveEnabled</c>); the row
    /// itself returns to the daemon's state on the next ticks if the toggle is not confirmed.
    /// </summary>
    private void SetLoopEnabled(string curveId, bool enabled)
    {
        if (Editor.LiveCurves.FirstOrDefault(c => c.Id == curveId) is { } curve)
            Editor.SetCurveEnabled(curve, enabled);
    }

    /// <summary>The visible fans in the table's order: by position (coolers, intakes, exhausts, …, none last), then as discovered.</summary>
    private void SyncVisibleFans(IReadOnlySet<string> hidden)
    {
        List<FanRow> visible = _fanOrder.Where(r => !hidden.Contains(r.FanId)).OrderBy(r => r.LocationRank).ToList();
        string signature = string.Join("|", visible.Select(r => r.FanId));
        if (signature == _visibleFansSignature)
            return;
        _visibleFansSignature = signature;
        Fans.Clear();
        foreach (FanRow row in visible)
            Fans.Add(row);
    }

    /// <summary>
    /// Builds the temperature tiles when membership, the configured group order or the language changes: the saved order
    /// (<see cref="SensorGroupOrder.Merge"/>), "ungrouped" last. Case-insensitive, so "cpu"/"CPU" are one tile. Which
    /// tiles are open is kept per group key.
    /// </summary>
    private void RebuildSensorGroupsIfChanged(IReadOnlyList<string> configuredGroups)
    {
        string signature = string.Join("|", configuredGroups) + "␝" + GroupLabels.Ungrouped + "␝"
                           + string.Join("|", Temperatures.Select(s => $"{s.GroupKey}␟{s.Name}"));
        if (signature == _sensorGroupSignature)
            return;
        _sensorGroupSignature = signature;

        // Tell duplicate display names apart (e.g. two "amdgpu edge"): only on a collision, with the hardware id as a
        // quiet suffix. Follows the signature, so it runs only when names or groups change.
        HashSet<string> duplicateNames = Temperatures
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SensorRow s in Temperatures)
            s.Disambiguator = duplicateNames.Contains(s.Name) ? s.Id : "";

        IReadOnlyList<string> names = SensorGroupOrder.Merge(
            configuredGroups, Temperatures.Select(s => s.GroupKey).Where(k => k.Length > 0));
        SensorGroups.Clear();
        foreach (string key in names.Append(""))
        {
            List<SensorRow> members = Temperatures
                .Where(s => string.Equals(s.GroupKey, key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (members.Count == 0)
                continue;
            var tile = new SensorGroup(key.Length == 0 ? GroupLabels.Ungrouped : key, key, OnTileToggled)
            {
                IsOpen = _openGroupKeys.Contains(key),
            };
            foreach (SensorRow sensor in members)
                tile.Sensors.Add(sensor);
            tile.Refresh();
            SensorGroups.Add(tile);
        }
    }

    private void OnTileToggled(SensorGroup tile)
    {
        if (tile.IsOpen)
            _openGroupKeys.Add(tile.Key);
        else
            _openGroupKeys.Remove(tile.Key);
    }
}
