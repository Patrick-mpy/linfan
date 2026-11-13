// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;

namespace LinFan.App.Controllers;

// Side menu of the curves tab: profiles and the selected profile's curves as one list with one selection.
public partial class CurveEditorController
{
    private AddCurveNavItem? _addCurveNavItem;
    private int _navSelectionDepth; // > 0 while the side menu itself writes the selection - see SelectedNavItem

    /// <summary>
    /// The side menu: every profile, and below the selected one its curves plus the "add curve" row. Adjusted in place
    /// (insert, move, remove) and never rebuilt: selecting a profile changes this list right after a click in it, and a
    /// reset would drop the list box's selection and keyboard position.
    /// </summary>
    public ObservableCollection<CurveTabNavItem> NavItems { get; } = new();

    /// <summary>
    /// The highlighted row - derived from what the right-hand side shows (<see cref="Pane"/>, <see cref="SelectedProfile"/>,
    /// <see cref="SelectedCurve"/>), so exactly one row is marked: the curve while the curve editor is open, otherwise
    /// the profile. Setting it opens that row. <c>null</c> and the "add curve" row are ignored: the list box writes
    /// <c>null</c> while it removes the selected row, and the selection follows the state anyway.
    /// </summary>
    public CurveTabNavItem? SelectedNavItem
    {
        get
        {
            if (Pane == CurveTabPane.Curve && SelectedCurve is { } curve)
                return NavItems.OfType<CurveNavItem>().FirstOrDefault(i => ReferenceEquals(i.Curve, curve));
            return SelectedProfile is { } profile
                ? NavItems.OfType<ProfileNavItem>().FirstOrDefault(i => ReferenceEquals(i.Profile, profile))
                : null;
        }
        set
        {
            if (value is not { IsSelectable: true })
                return;
            // The list hears about the new state once, at the end, instead of being handed intermediate rows (opening
            // a profile selects its first curve on the way).
            _navSelectionDepth++;
            try
            {
                switch (value)
                {
                    case ProfileNavItem p:
                        SelectedProfile = p.Profile;
                        Pane = CurveTabPane.Profile;
                        break;
                    case CurveNavItem c:
                        SelectedCurve = c.Curve;
                        Pane = CurveTabPane.Curve;
                        break;
                }
            }
            finally
            {
                _navSelectionDepth--;
            }
            NotifyNavSelection();
        }
    }

    partial void OnPaneChanged(CurveTabPane value) => NotifyNavSelection();

    private void NotifyNavSelection()
    {
        if (_navSelectionDepth == 0)
            OnPropertyChanged(nameof(SelectedNavItem));
    }

    /// <summary>Brings <see cref="NavItems"/> in line with the profiles, the selected profile and its curves.</summary>
    private void SyncNavItems()
    {
        var target = new List<CurveTabNavItem>();
        if (Profiles.Count == 0)
            AppendCurveItems(target); // legacy config without profiles: the curves are the whole menu
        foreach (ProfileRow profile in Profiles)
        {
            ProfileNavItem item = NavItems.OfType<ProfileNavItem>().FirstOrDefault(i => ReferenceEquals(i.Profile, profile))
                                  ?? new ProfileNavItem(profile);
            item.IsExpanded = ReferenceEquals(profile, SelectedProfile);
            target.Add(item);
            if (item.IsExpanded)
                AppendCurveItems(target);
        }

        for (int i = NavItems.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(NavItems[i]))
                NavItems.RemoveAt(i);
        }
        // The "add curve" row is taken out and put back rather than moved past the profiles: moving a profile row
        // instead would recycle its container and drop the keyboard focus on it (arrowing from "+" to the next profile).
        if (_addCurveNavItem is { } add && NavItems.IndexOf(add) is int addAt && addAt >= 0 && addAt != target.IndexOf(add))
            NavItems.RemoveAt(addAt);
        for (int i = 0; i < target.Count; i++)
        {
            if (i < NavItems.Count && ReferenceEquals(NavItems[i], target[i]))
                continue;
            int at = NavItems.IndexOf(target[i]);
            if (at >= 0)
                NavItems.Move(at, i);
            else
                NavItems.Insert(i, target[i]);
        }
        NotifyNavSelection(); // a row the state points at may only exist from now on
    }

    private void AppendCurveItems(List<CurveTabNavItem> target)
    {
        foreach (CurveEditRow curve in Curves)
            target.Add(NavItems.OfType<CurveNavItem>().FirstOrDefault(i => ReferenceEquals(i.Curve, curve))
                       ?? new CurveNavItem(curve));
        target.Add(AddCurveRow);
    }

    private AddCurveNavItem AddCurveRow => _addCurveNavItem ??= new AddCurveNavItem(AddCurveFromTemplateCommand)
    {
        CanAdd = CanAddCurve,
        Tooltip = AddCurveTooltip,
    };
}
