// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

// Settings › Sensoren: the sensor groups as sections - order, rename, delete, auto-group, moves, selection, drag.
// Every change to membership or to the group list is an editor edit (history, unsaved bar); the selection is not.
public partial class CurveEditorController
{
    /// <summary>How long a freshly created group stands out.</summary>
    private static readonly TimeSpan NewGroupHighlight = TimeSpan.FromSeconds(3);

    private bool _sensorSectionsStale; // a rebuild was asked for during a WithoutHistory scope
    private bool _sensorDragActive;

    /// <summary>
    /// The sensor list of the settings, one section per group: the configured order (<see cref="SensorGroups"/>), then
    /// groups only sensors name, alphabetically, and „Ungruppiert" last - that one only while it has sensors to show or
    /// a drag is running (it is a drop target then). While searching, groups without a match are left out. Kept in step
    /// incrementally, so a section - and the name field the user is typing in - survives a rebuild.
    /// </summary>
    public ObservableCollection<SensorGroupSection> SensorGroupSections { get; } = new();

    /// <summary>At least one sensor is ticked - the bulk bar shows.</summary>
    [ObservableProperty] private bool _hasSensorSelection;

    /// <summary>„3 Sensoren ausgewählt".</summary>
    [ObservableProperty] private string _sensorSelectionText = "";

    /// <summary>The bulk bar's „Verschieben nach ▾" entries: every group, „Ungruppiert", „+ Neue Gruppe …".</summary>
    [ObservableProperty] private IReadOnlyList<MenuChoice> _bulkMoveMenu = Array.Empty<MenuChoice>();

    /// <summary>„Automatisch gruppieren" has something to do: a sensor without a group.</summary>
    public bool CanAutoGroup => _initialized && Sensors.Any(s => GroupKeyOf(s).Length == 0);

    /// <summary>Room for another group - the daemon refuses a list beyond <see cref="ConfigLimits.MaxSensorGroups"/>.</summary>
    public bool CanAddGroup => _initialized && OrderedGroupNames().Count < ConfigLimits.MaxSensorGroups;

    /// <summary>Tooltip of „Automatisch gruppieren"; disabled, it says why.</summary>
    public string AutoGroupTooltip
    {
        get
        {
            int loose = Sensors.Count(s => GroupKeyOf(s).Length == 0);
            return loose == 0
                ? Localizer.Instance["SensorGroups.NothingToGroup"]
                : Localizer.Instance.Format("SensorGroups.AutoGroupTooltip", loose);
        }
    }

    private static bool SameGroup(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A sensor's group as a key: trimmed name, empty for none.</summary>
    private static string GroupKeyOf(SensorOption sensor) =>
        string.IsNullOrWhiteSpace(sensor.Group) ? "" : sensor.Group.Trim();

    /// <summary>All group names in display order - the configured list plus the names only sensors carry.</summary>
    private IReadOnlyList<string> OrderedGroupNames() =>
        SensorGroupOrder.Merge(SensorGroups, Sensors.Select(GroupKeyOf).Where(k => k.Length > 0));

    /// <summary>
    /// Replaces the group list - the only way it changes besides loading. Rebuilds both places that order by it: the
    /// settings sections and the source sections of the curve editor.
    /// </summary>
    private void SetSensorGroups(IReadOnlyList<string> names)
    {
        SensorGroups = names.Take(ConfigLimits.MaxSensorGroups).ToList();
        RebuildSensorSections();
        RebuildSourceSections();
    }

    // --- Sections ------------------------------------------------------------------------------

    private void RebuildSensorSections()
    {
        if (_nonEditDepth > 0)
        {
            _sensorSectionsStale = true; // a batch (step, restore, live tick) rebuilds once at its end
            return;
        }
        _sensorSectionsStale = false;

        IReadOnlyList<string> names = OrderedGroupNames();
        bool searching = !string.IsNullOrWhiteSpace(SensorSearch);
        var desired = new List<(string Name, bool Ungrouped, List<SensorOption> Rows, int Total, int Index)>();
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            List<SensorOption> rows = FilteredSensors.Where(s => SameGroup(GroupKeyOf(s), name)).ToList();
            if (searching && rows.Count == 0)
                continue;
            desired.Add((name, false, rows, Sensors.Count(s => SameGroup(GroupKeyOf(s), name)), i));
        }
        List<SensorOption> loose = FilteredSensors.Where(s => GroupKeyOf(s).Length == 0).ToList();
        if (loose.Count > 0 || _sensorDragActive)
            desired.Add((GroupLabels.Ungrouped, true, loose, Sensors.Count(s => GroupKeyOf(s).Length == 0), -1));

        for (int i = 0; i < desired.Count; i++)
        {
            (string name, bool ungrouped, List<SensorOption> rows, int total, int index) = desired[i];
            int at = IndexOfSection(name, ungrouped, from: i);
            SensorGroupSection section;
            if (at < 0)
            {
                section = new SensorGroupSection(this, name, ungrouped);
                SensorGroupSections.Insert(i, section);
            }
            else
            {
                section = SensorGroupSections[at];
                if (at != i)
                    SensorGroupSections.Move(at, i);
            }
            section.Update(name, rows, total, canMoveUp: index > 0, canMoveDown: index >= 0 && index < names.Count - 1,
                           SensorCountText(rows.Count, total));
        }
        while (SensorGroupSections.Count > desired.Count)
            SensorGroupSections.RemoveAt(SensorGroupSections.Count - 1);

        // Only what the list shows can be selected: a bulk action or a drag must never reach rows the search or
        // „hide here too" took out of view.
        var shown = new HashSet<SensorOption>(FilteredSensors);
        foreach (SensorOption sensor in Sensors.Where(s => s.IsSelected && !shown.Contains(s)))
            sensor.IsSelected = false;

        RefreshGroupMenus(names);
        RefreshSensorSelection();
        OnPropertyChanged(nameof(CanAutoGroup));
        OnPropertyChanged(nameof(CanAddGroup));
        OnPropertyChanged(nameof(AutoGroupTooltip));
        AutoGroupSensorsCommand.NotifyCanExecuteChanged();
        AddSensorGroupCommand.NotifyCanExecuteChanged();
    }

    private int IndexOfSection(string name, bool ungrouped, int from)
    {
        for (int j = from; j < SensorGroupSections.Count; j++)
        {
            SensorGroupSection s = SensorGroupSections[j];
            if (s.IsUngrouped == ungrouped && (ungrouped || SameGroup(s.Name, name)))
                return j;
        }
        return -1;
    }

    /// <summary>„leer" (no member at all), „2 ausgeblendet" (none shown), „1 Sensor" / „3 Sensoren".</summary>
    private static string SensorCountText(int shown, int total)
    {
        Localizer l = Localizer.Instance;
        if (total == 0)
            return l["SensorGroups.CountEmpty"];
        if (shown == 0)
            return l.Format("SensorGroups.CountHidden", total);
        return shown == 1 ? l["SensorGroups.CountOne"] : l.Format("SensorGroups.Count", shown);
    }

    /// <summary>Whether <paramref name="name"/> is the name of a group other than <paramref name="section"/>'s - the „Name doppelt" warning.</summary>
    internal bool IsOtherGroupName(SensorGroupSection section, string name)
    {
        string typed = name.Trim();
        return typed.Length > 0 && !section.IsUngrouped
               && OrderedGroupNames().Any(n => SameGroup(n, typed) && !SameGroup(n, section.Name));
    }

    // --- Group actions (each one undo step) ----------------------------------------------------

    /// <summary>
    /// „Neue Gruppe": an empty group at the end, uniquely named („Neue Gruppe 2" …), briefly highlighted. A running
    /// search ends first - it would hide the empty group just created.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddGroup))]
    private void AddSensorGroup()
    {
        SensorSearch = "";
        CreateGroup(Array.Empty<string>(), clearSelection: false);
    }

    /// <summary>„Automatisch gruppieren": every sensor without a group gets the one its driver suggests (an existing group of that name, case-insensitively, or a new one at the end). Grouped sensors stay where they are.</summary>
    [RelayCommand(CanExecute = nameof(CanAutoGroup))]
    private void AutoGroupSensors()
    {
        List<SensorOption> loose = Sensors.Where(s => GroupKeyOf(s).Length == 0).ToList();
        if (loose.Count == 0)
            return;
        SensorGroupPlan plan = SensorGroupOrder.Plan(OrderedGroupNames(), Sensors.Select(s => (s.Id, (string?)GroupKeyOf(s))));
        RecordStep(() =>
        {
            SetSensorGroups(plan.Names);
            foreach (SensorOption sensor in loose)
                sensor.Group = plan.GroupById[sensor.Id];
        });
        SetStatus(loose.Count == 1
            ? Localizer.Instance["SensorGroups.AutoGroupedOne"]
            : Localizer.Instance.Format("SensorGroups.AutoGrouped", loose.Count), autoHide: true);
    }

    /// <summary>
    /// Renames a group - or, when <paramref name="draft"/> names another group, merges it into that one (a toast offers
    /// „Rückgängig"). An empty or unchanged name only resets the field.
    /// </summary>
    internal void RenameGroup(SensorGroupSection section, string draft)
    {
        string from = section.Name;
        string to = draft.Trim();
        List<string> names = OrderedGroupNames().ToList();
        int index = names.FindIndex(n => SameGroup(n, from));
        if (section.IsUngrouped || !SensorGroupSections.Contains(section) || index < 0
            || to.Length == 0 || string.Equals(to, from, StringComparison.Ordinal))
        {
            section.DraftName = from;
            return;
        }

        List<SensorOption> members = Sensors.Where(s => SameGroup(GroupKeyOf(s), from)).ToList();
        string? existing = names.Where((n, i) => i != index && SameGroup(n, to)).FirstOrDefault();
        if (existing is not null)
        {
            names.RemoveAt(index);
            RecordStep(() =>
            {
                foreach (SensorOption sensor in members)
                    sensor.Group = existing;
                SetSensorGroups(names);
            });
            ShowDeletedToast(Localizer.Instance.Format("SensorGroups.Merged", from, existing));
            return;
        }

        // Re-key the section first, so the rebuild finds it under its new name and keeps it (with the field in it).
        section.Name = to;
        section.DraftName = to;
        names[index] = to;
        RecordStep(() =>
        {
            foreach (SensorOption sensor in members)
                sensor.Group = to;
            SetSensorGroups(names);
        });
    }

    /// <summary>↑/↓: moves the group one place in the list - the order the curve editor shows the groups in, too.</summary>
    internal void MoveGroup(SensorGroupSection section, int delta)
    {
        List<string> names = OrderedGroupNames().ToList();
        int from = names.FindIndex(n => SameGroup(n, section.Name));
        int to = from + delta;
        if (section.IsUngrouped || from < 0 || to < 0 || to >= names.Count)
            return;
        (names[from], names[to]) = (names[to], names[from]);
        RecordStep(() => SetSensorGroups(names));
    }

    /// <summary>Deletes a group without asking: its sensors become ungrouped, the toast offers „Rückgängig".</summary>
    internal void DeleteGroup(SensorGroupSection section)
    {
        if (section.IsUngrouped)
            return;
        string name = section.Name;
        List<SensorOption> members = Sensors.Where(s => SameGroup(GroupKeyOf(s), name)).ToList();
        List<string> names = OrderedGroupNames().Where(n => !SameGroup(n, name)).ToList();
        RecordStep(() =>
        {
            foreach (SensorOption sensor in members)
                sensor.Group = "";
            SetSensorGroups(names);
        });
        Localizer l = Localizer.Instance;
        ShowDeletedToast(members.Count switch
        {
            0 => l.Format("SensorGroups.Deleted", name),
            1 => l.Format("SensorGroups.DeletedOne", name),
            int n => l.Format("SensorGroups.DeletedMany", name, n),
        });
    }

    /// <summary>
    /// Moves sensors into a group (empty or null: ungrouped) - the target of the row menu, the bulk menu and a drop. One
    /// undo step; nothing happens when they are all in that group already. The list is written out with it, so a group
    /// the move empties stays as an empty group instead of vanishing.
    /// </summary>
    public void MoveSensors(IReadOnlyCollection<string> sensorIds, string? group)
    {
        string target = group?.Trim() ?? "";
        List<SensorOption> rows = Sensors.Where(s => sensorIds.Contains(s.Id)).ToList();
        if (rows.Count == 0 || rows.All(s => SameGroup(GroupKeyOf(s), target)))
            return;

        List<string> names = OrderedGroupNames().ToList();
        string spelling = target.Length == 0 ? "" : names.FirstOrDefault(n => SameGroup(n, target)) ?? target;
        if (spelling.Length > 0 && !names.Any(n => SameGroup(n, spelling)))
            names.Add(spelling);
        RecordStep(() =>
        {
            SetSensorGroups(names);
            foreach (SensorOption sensor in rows)
                sensor.Group = spelling;
        });

        string label = spelling.Length == 0 ? GroupLabels.Ungrouped : spelling;
        SetStatus(rows.Count == 1
            ? Localizer.Instance.Format("SensorGroups.MovedOne", label)
            : Localizer.Instance.Format("SensorGroups.Moved", rows.Count, label), autoHide: true);
    }

    /// <summary>Sensors dropped on a section: into its group, or out of every group on „Ungruppiert".</summary>
    public void MoveSensors(IReadOnlyCollection<string> sensorIds, SensorGroupSection target) =>
        MoveSensors(sensorIds, target.IsUngrouped ? null : target.Name);

    /// <summary>A new group at the end, with the given sensors already in it (none for „Neue Gruppe").</summary>
    private void CreateGroup(IReadOnlyCollection<string> sensorIds, bool clearSelection)
    {
        if (!CanAddGroup)
            return;
        List<string> names = OrderedGroupNames().ToList();
        string name = UniqueGroupName(names);
        names.Add(name);
        List<SensorOption> rows = Sensors.Where(s => sensorIds.Contains(s.Id)).ToList();
        RecordStep(() =>
        {
            SetSensorGroups(names);
            foreach (SensorOption sensor in rows)
                sensor.Group = name;
        });
        if (clearSelection)
            ClearSensorSelection();
        HighlightGroup(name);
        SetStatus(Localizer.Instance.Format("SensorGroups.Created", name), autoHide: true);
    }

    /// <summary>„Neue Gruppe", „Neue Gruppe 2", … - the first not taken (case-insensitively).</summary>
    private static string UniqueGroupName(IReadOnlyList<string> taken) =>
        UniqueName(Localizer.Instance["SensorGroups.NewGroupName"], name => taken.Any(t => SameGroup(t, name)));

    /// <summary>„Name", else „Name 2", „Name 3" … - the first one <paramref name="isTaken"/> does not claim.</summary>
    private static string UniqueName(string baseName, Func<string, bool> isTaken)
    {
        string name = baseName;
        for (int n = 2; isTaken(name); n++)
            name = $"{baseName} {n}";
        return name;
    }

    private void HighlightGroup(string name)
    {
        foreach (SensorGroupSection s in SensorGroupSections)
            s.IsHighlighted = false;
        if (SensorGroupSections.FirstOrDefault(s => !s.IsUngrouped && SameGroup(s.Name, name)) is not { } section)
            return;
        section.IsHighlighted = true;
        _ = EndHighlightAsync(section);
    }

    private async Task EndHighlightAsync(SensorGroupSection section)
    {
        await Task.Delay(NewGroupHighlight, _clock);
        section.IsHighlighted = false;
    }

    // --- Menus ---------------------------------------------------------------------------------

    /// <summary>Rebuilds the „Gruppe ▾" menu of every row (its own group marked) and the bulk bar's move menu.</summary>
    private void RefreshGroupMenus(IReadOnlyList<string> names)
    {
        bool canAdd = CanAddGroup;
        BulkMoveMenu = BuildGroupMenu(names, canAdd, current: null, () => SelectedSensorIds(), bulk: true);
        foreach (SensorOption sensor in Sensors)
            sensor.GroupMenu = BuildGroupMenu(names, canAdd, GroupKeyOf(sensor), () => new[] { sensor.Id }, bulk: false);
    }

    private List<MenuChoice> BuildGroupMenu(IReadOnlyList<string> names, bool canAdd, string? current,
                                            Func<IReadOnlyCollection<string>> ids, bool bulk)
    {
        // The bulk menu has no current value - its entries are plain actions, not radio items.
        MenuChoice Target(string header, string group) => bulk
            ? MenuChoice.Action(header, new RelayCommand(() => PickGroup(ids(), group, bulk)))
            : MenuChoice.Value(header, new RelayCommand(() => PickGroup(ids(), group, bulk)), SameGroup(current ?? "", group));

        var menu = new List<MenuChoice>(names.Count + 3);
        foreach (string name in names)
            menu.Add(Target(name, name));
        menu.Add(Target(GroupLabels.Ungrouped, ""));
        if (canAdd)
        {
            menu.Add(MenuChoice.Separator());
            menu.Add(MenuChoice.Action(Localizer.Instance["SensorGroups.NewGroupEntry"],
                                       new RelayCommand(() => CreateGroup(ids(), clearSelection: bulk))));
        }
        return menu;
    }

    private void PickGroup(IReadOnlyCollection<string> ids, string group, bool bulk)
    {
        MoveSensors(ids, group);
        if (bulk)
            ClearSensorSelection();
    }

    // --- Selection (view state, never an edit) -------------------------------------------------

    private List<string> SelectedSensorIds() => Sensors.Where(s => s.IsSelected).Select(s => s.Id).ToList();

    private void RefreshSensorSelection()
    {
        int n = Sensors.Count(s => s.IsSelected);
        HasSensorSelection = n > 0;
        SensorSelectionText = n == 1
            ? Localizer.Instance["SensorGroups.SelectedOne"]
            : Localizer.Instance.Format("SensorGroups.Selected", n);
        foreach (SensorGroupSection section in SensorGroupSections)
            section.RefreshSelection();
    }

    /// <summary>„Auswahl aufheben".</summary>
    [RelayCommand]
    private void ClearSensorSelection()
    {
        foreach (SensorOption sensor in Sensors)
            sensor.IsSelected = false;
    }

    // --- Drag at the grip (started and dropped by the view) -----------------------------------

    /// <summary>
    /// A drag starts at <paramref name="row"/>'s grip: a selected row takes the whole selection along. Marks the rows
    /// as dragged and shows „Ungruppiert" as a target. Returns the ids the drop hands to <see cref="MoveSensors"/>.
    /// </summary>
    public IReadOnlyList<string> BeginSensorDrag(SensorOption row)
    {
        List<SensorOption> dragged = row.IsSelected ? Sensors.Where(s => s.IsSelected).ToList() : new List<SensorOption> { row };
        foreach (SensorOption sensor in dragged)
            sensor.IsDragged = true;
        _sensorDragActive = true;
        RebuildSensorSections();
        return dragged.Select(s => s.Id).ToList();
    }

    /// <summary>The drag ended (dropped or cancelled).</summary>
    public void EndSensorDrag()
    {
        foreach (SensorOption sensor in Sensors)
            sensor.IsDragged = false;
        if (!_sensorDragActive)
            return;
        _sensorDragActive = false;
        RebuildSensorSections();
    }
}
