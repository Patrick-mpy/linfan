// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

// Settings › Lüfter: one section per mounting position, the position menu, selection, bulk actions and drag.
// A position change is an editor edit (history, unsaved bar); the selection is not.
public partial class CurveEditorController
{
    private bool _fanSectionsStale; // a rebuild was asked for during a WithoutHistory scope
    private bool _deviceBusy;       // the daemon runs a calibration, identification or tach coupling
    private bool _dryRun;           // the daemon refuses every device action (it writes no fan)

    /// <summary>
    /// The fan list of the settings, one section per position in use (of the fans shown), ordered by
    /// <see cref="FanLocationOption.Rank"/> - „Ohne Position" last.
    /// </summary>
    public ObservableCollection<FanLocationSection> FanLocationSections { get; } = new();

    /// <summary>At least one fan is ticked - the bulk bar shows.</summary>
    [ObservableProperty] private bool _hasFanSelection;

    /// <summary>„2 Lüfter ausgewählt".</summary>
    [ObservableProperty] private string _fanSelectionText = "";

    /// <summary>„Ausblenden" while every selected fan is visible, otherwise „Einblenden".</summary>
    [ObservableProperty] private string _fanBulkVisibilityText = "";

    /// <summary>The bulk bar's „Position setzen ▾" entries (no current one, no diagram).</summary>
    [ObservableProperty] private IReadOnlyList<MenuChoice> _bulkPositionMenu = Array.Empty<MenuChoice>();

    /// <summary>
    /// The row menu's „Im Gehäuse-Diagramm wählen …" was picked: the view opens the case diagram for the fan and hands
    /// the result to <see cref="ApplyLocationPick"/>.
    /// </summary>
    public event EventHandler<FanAssignRow>? LocationDialogRequested;

    // The last „Position wählen" handed out - a resync closes it (see Resync). Once its dialog is closed, asking it to
    // close reaches no one.
    private LocationPickController? _locationPick;

    /// <summary>The „Position wählen" dialog for <paramref name="row"/>: the other visible fans stand dimmed in the schema.</summary>
    public LocationPickController CreateLocationPick(FanAssignRow row) => _locationPick = new(
        row.FanId, row.DisplayName, row.Location.Value, CaseType, row.Manual,
        Fans.Where(f => f.Visible && !ReferenceEquals(f, row))
            .Select(f => new CaseFan(f.FanId, f.DisplayName, f.Location.Value, f.Manual))
            .ToList());

    private void RebuildFanSections()
    {
        if (_nonEditDepth > 0)
        {
            _fanSectionsStale = true;
            return;
        }
        _fanSectionsStale = false;

        List<IGrouping<FanLocation, FanAssignRow>> groups = FilteredFans
            .GroupBy(f => f.Location.Value)
            .OrderBy(g => FanLocationOption.Rank(g.Key))
            .ToList();
        for (int i = 0; i < groups.Count; i++)
        {
            FanLocation location = groups[i].Key;
            int at = -1;
            for (int j = i; j < FanLocationSections.Count; j++)
            {
                if (FanLocationSections[j].Location == location)
                {
                    at = j;
                    break;
                }
            }
            FanLocationSection section;
            if (at < 0)
            {
                section = new FanLocationSection(location);
                FanLocationSections.Insert(i, section);
            }
            else
            {
                section = FanLocationSections[at];
                if (at != i)
                    FanLocationSections.Move(at, i);
            }
            section.Update(groups[i].ToList());
        }
        while (FanLocationSections.Count > groups.Count)
            FanLocationSections.RemoveAt(FanLocationSections.Count - 1);

        // Only what the list shows can be selected (as for the sensors): hiding selected fans with „hide here too" on
        // must not leave an invisible selection behind.
        var shown = new HashSet<FanAssignRow>(FilteredFans);
        foreach (FanAssignRow fan in Fans.Where(f => f.IsSelected && !shown.Contains(f)))
            fan.IsSelected = false;
        RefreshFanSelection();
    }

    /// <summary>
    /// Sets the position of fans - the target of the row menu, the bulk menu, the case diagram and a drop on a section.
    /// One undo step; nothing happens when they are all there already.
    /// </summary>
    public void SetFanLocations(IReadOnlyCollection<string> fanIds, FanLocation location)
    {
        List<FanAssignRow> rows = Fans.Where(f => fanIds.Contains(f.FanId)).ToList();
        if (rows.Count == 0 || rows.All(f => f.Location.Value == location))
            return;
        RecordStep(() =>
        {
            foreach (FanAssignRow fan in rows)
                fan.Location = FanLocationOption.For(location);
        });
        SetStatus(PositionStatus(rows.Count, location), autoHide: true);
    }

    // „Position: Gehäuse vorn · Einlass" / „3 Lüfter: …".
    private static string PositionStatus(int count, FanLocation location)
    {
        string label = location == FanLocation.Unspecified
            ? Localizer.Instance["FanSections.NoPosition"]
            : FanLocationOption.For(location).Display;
        return count == 1
            ? Localizer.Instance.Format("FanSections.PositionSetOne", label)
            : Localizer.Instance.Format("FanSections.PositionSet", count, label);
    }

    /// <summary>Fans dropped on a section: they move to its position.</summary>
    public void SetFanLocations(IReadOnlyCollection<string> fanIds, FanLocationSection target) =>
        SetFanLocations(fanIds, target.Location);

    // --- Position menu -------------------------------------------------------------------------

    /// <summary>The position menu (<see cref="PositionMenu"/>); a fan's own menu marks its position and ends with the case diagram.</summary>
    private List<MenuChoice> BuildPositionMenu(FanLocation? current, Action<FanLocation> pick, FanAssignRow? diagramFor)
    {
        Localizer l = Localizer.Instance;
        List<MenuChoice> menu = PositionMenu.Build(current, pick, CaseType);
        if (diagramFor is { } row)
        {
            menu.Add(MenuChoice.Separator());
            menu.Add(MenuChoice.Action(l["FanPositionMenu.PickInDiagram"],
                                       new RelayCommand(() => LocationDialogRequested?.Invoke(this, row))));
        }
        return menu;
    }

    private void RefreshPositionMenu(FanAssignRow fan) =>
        fan.PositionMenu = BuildPositionMenu(fan.Location.Value, loc => SetFanLocations(new[] { fan.FanId }, loc), fan);

    private void RefreshPositionMenus()
    {
        BulkPositionMenu = BuildPositionMenu(null, loc =>
        {
            SetFanLocations(SelectedFanIds(), loc);
            ClearFanSelection();
        }, diagramFor: null);
        foreach (FanAssignRow fan in Fans)
            RefreshPositionMenu(fan);
    }

    // --- Selection and bulk actions ------------------------------------------------------------

    private List<string> SelectedFanIds() => Fans.Where(f => f.IsSelected).Select(f => f.FanId).ToList();

    private void RefreshFanSelection()
    {
        List<FanAssignRow> selected = Fans.Where(f => f.IsSelected).ToList();
        HasFanSelection = selected.Count > 0;
        FanSelectionText = selected.Count == 1
            ? Localizer.Instance["FanSections.SelectedOne"]
            : Localizer.Instance.Format("FanSections.Selected", selected.Count);
        FanBulkVisibilityText = Localizer.Instance[selected.All(f => f.Visible) ? "FanSections.Hide" : "FanSections.Show"];
        foreach (FanLocationSection section in FanLocationSections)
            section.RefreshSelection();
    }

    /// <summary>„Auswahl aufheben".</summary>
    [RelayCommand]
    private void ClearFanSelection()
    {
        foreach (FanAssignRow fan in Fans)
            fan.IsSelected = false;
    }

    /// <summary>„Ausblenden" / „Einblenden": hides the selected fans while all of them are visible, otherwise shows them all. One undo step.</summary>
    [RelayCommand]
    private void ToggleSelectedFansVisible()
    {
        List<FanAssignRow> selected = Fans.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
            return;
        bool visible = !selected.All(f => f.Visible);
        RecordStep(() =>
        {
            foreach (FanAssignRow fan in selected)
                fan.Visible = visible;
        });
    }

    // --- Drag at the grip (started and dropped by the view) -----------------------------------

    /// <summary>A drag starts at <paramref name="row"/>'s grip: a selected row takes the whole selection along. Returns the ids for the drop.</summary>
    public IReadOnlyList<string> BeginFanDrag(FanAssignRow row)
    {
        List<FanAssignRow> dragged = row.IsSelected ? Fans.Where(f => f.IsSelected).ToList() : new List<FanAssignRow> { row };
        foreach (FanAssignRow fan in dragged)
            fan.IsDragged = true;
        return dragged.Select(f => f.FanId).ToList();
    }

    /// <summary>The drag ended (dropped or cancelled).</summary>
    public void EndFanDrag()
    {
        foreach (FanAssignRow fan in Fans)
            fan.IsDragged = false;
    }

    // --- Device actions (calibrate, identify, couple) ------------------------------------------

    // Whether calibrate, identify and couple are locked - the airflow page offers „Kalibrieren" only while they are not.
    private bool _deviceActionsBlocked;

    /// <summary>
    /// Calibrate, identify and couple wait for the service and for a running calibration, identification or tach
    /// coupling to end - the daemon refuses a second one with only a log line, and every one in a dry run. The reason
    /// becomes their tooltip.
    /// </summary>
    private void RefreshDeviceActionsBlocked()
    {
        string reason = DeviceActionBlock.Reason(IsConnected, _dryRun, _deviceBusy);
        foreach (FanAssignRow fan in Fans)
            fan.BlockDeviceActions(reason);
        if ((reason.Length > 0) == _deviceActionsBlocked)
            return;
        _deviceActionsBlocked = reason.Length > 0;
        if (_airflowResult is { } result) // none before Initialize has built the page
            AirflowHints = BuildAirflowHints(result); // the notices are not rebuilt per tick
    }
}
