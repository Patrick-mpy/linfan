// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;
using LinFan.App.Services;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>
/// One sensor group in Settings › Sensoren: a section with its (filtered) rows and a header to select, rename, reorder
/// and delete the group. „Ungruppiert" is a section too, but fixed - no rename, no reorder, no delete. Built and kept
/// in step by <see cref="CurveEditorController"/>, which also carries out every action as an editor edit.
/// </summary>
public sealed partial class SensorGroupSection : ObservableObject
{
    private readonly CurveEditorController _owner;

    internal SensorGroupSection(CurveEditorController owner, string name, bool isUngrouped)
    {
        _owner = owner;
        _name = name;
        _draftName = name;
        IsUngrouped = isUngrouped;
    }

    /// <summary>The section of the sensors without a group - always last, never renamed, moved or deleted.</summary>
    public bool IsUngrouped { get; }

    /// <summary>Longest name the daemon accepts - a longer one would make it refuse the whole configuration.</summary>
    public int MaxNameLength => ConfigLimits.MaxTextLength;

    /// <summary>The group's name - its identity (compared case-insensitively), „Ungruppiert" for the fixed section.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectAllName))]
    private string _name;

    /// <summary>What the header's name field holds while typing; committed by <see cref="CommitNameCommand"/>.</summary>
    [ObservableProperty] private string _draftName;

    /// <summary>The typed name is already another group's - committing it merges the two.</summary>
    [ObservableProperty] private bool _isDuplicateName;

    /// <summary>The group's rows as the list shows them (search and „hide here too" applied).</summary>
    public ObservableCollection<SensorOption> Rows { get; } = new();

    /// <summary>„leer" / „2 ausgeblendet" / „3 Sensoren".</summary>
    [ObservableProperty] private string _countText = "";

    /// <summary>No sensor belongs to the group at all (not just none shown) - the section explains how to fill it.</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>The header check: <c>true</c> = all shown rows selected, <c>false</c> = none, <c>null</c> = some.</summary>
    [ObservableProperty] private bool? _allSelected = false;

    /// <summary>The section shows rows - an empty or all-hidden group has nothing to select, its header check is off.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>Accessible name of the header check („Gruppe CPU: alle auswählen").</summary>
    public string SelectAllName => Localizer.Instance.Format("SensorGroups.SelectAllName", Name);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    private bool _canMoveUp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private bool _canMoveDown;

    /// <summary>Just created - the section stands out for a moment, so the new (empty) group is found.</summary>
    [ObservableProperty] private bool _isHighlighted;

    partial void OnDraftNameChanged(string value) => IsDuplicateName = _owner.IsOtherGroupName(this, value);

    /// <summary>Selects all shown rows - or clears them when all are selected already. Selection only, no edit.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private void ToggleSelectAll()
    {
        bool select = AllSelected != true;
        foreach (SensorOption row in Rows.ToList())
            row.IsSelected = select;
        // The click already toggled the check box itself; when the state did not change (nothing to select), only a
        // fresh notification puts the box back in line with it.
        OnPropertyChanged(nameof(AllSelected));
    }

    /// <summary>Commits the typed name (focus left the field or Enter): renames the group, or merges it into the group of that name.</summary>
    [RelayCommand]
    private void CommitName() => _owner.RenameGroup(this, DraftName);

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _owner.MoveGroup(this, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _owner.MoveGroup(this, +1);

    /// <summary>Deletes the group without asking - its sensors become ungrouped; the toast offers „Rückgängig".</summary>
    [RelayCommand]
    private void Delete() => _owner.DeleteGroup(this);

    /// <summary>Takes a new state from the controller's rebuild (an undo may have renamed the group, too).</summary>
    internal void Update(string name, IReadOnlyList<SensorOption> rows, int total, bool canMoveUp, bool canMoveDown, string countText)
    {
        if (Name != name)
        {
            Name = name;
            DraftName = name;
        }
        FilteredListView.Sync(Rows, rows);
        OnPropertyChanged(nameof(HasRows));
        ToggleSelectAllCommand.NotifyCanExecuteChanged();
        IsEmpty = total == 0;
        CountText = countText;
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
        RefreshSelection();
    }

    /// <summary>The language changed: the accessible name has to be read again (the name itself comes with the rebuild).</summary>
    internal void NotifyLanguageChanged() => OnPropertyChanged(nameof(SelectAllName));

    /// <summary>Re-reads the selection of the rows for the header check.</summary>
    internal void RefreshSelection()
    {
        int selected = Rows.Count(r => r.IsSelected);
        AllSelected = selected == 0 ? false : selected == Rows.Count ? true : null;
    }
}
