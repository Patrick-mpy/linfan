// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

// The case type: which positions the menus offer, which schema the airflow page and „Position wählen" draw, and what the
// pressure balance counts. Editor state like SensorGroups - loaded with the config, restored by RestoreFrom, written by
// BuildConfig, and a change is one history step (alone, or together with the position picked in the dialog).
public partial class CurveEditorController
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaseTypeLabel), nameof(CaseTypeChipName), nameof(CaseCaption), nameof(CaseTypeMenu))]
    private CaseType _caseType;

    /// <summary>The „Gehäuse ▾" menu of the airflow page.</summary>
    public IReadOnlyList<MenuChoice> CaseTypeMenu => CaseTypeText.Menu(CaseType, SetCaseType);

    public string CaseTypeLabel => CaseTypeText.Name(CaseType);

    public string CaseTypeChipName => CaseTypeText.ChipName(CaseType);

    public string CaseCaption => CaseTypeText.Caption(CaseType);

    // The airflow page follows with the dirty check every change runs; the position menus have to be rebuilt here.
    partial void OnCaseTypeChanged(CaseType value) => RefreshPositionMenus();

    // The names are translated.
    private void NotifyCaseTypeTexts()
    {
        OnPropertyChanged(nameof(CaseTypeLabel));
        OnPropertyChanged(nameof(CaseTypeChipName));
        OnPropertyChanged(nameof(CaseCaption));
        OnPropertyChanged(nameof(CaseTypeMenu));
    }

    /// <summary>Picks the case type (the airflow page's chip). Positions stay as they are; those the type lacks are named.</summary>
    public void SetCaseType(CaseType value)
    {
        if (value == CaseType)
            return;
        RecordStep(() => CaseType = value);
    }

    /// <summary>
    /// Takes over what „Position wählen" returned: the fan's position and the case type it was picked in, as one step.
    /// </summary>
    public void ApplyLocationPick(string fanId, LocationPick pick)
    {
        FanAssignRow? row = Fans.FirstOrDefault(f => f.FanId == fanId);
        bool moved = row is not null && row.Location.Value != pick.Location;
        if (!moved && pick.CaseType == CaseType)
            return;
        RecordStep(() =>
        {
            CaseType = pick.CaseType;
            if (moved)
                row!.Location = FanLocationOption.For(pick.Location);
        });
        if (moved)
            SetStatus(PositionStatus(1, pick.Location), autoHide: true);
    }
}
