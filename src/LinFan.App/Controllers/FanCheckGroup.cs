// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>
/// A block of fans (by position) in the assignment of the selected curve, with a tri-state header that assigns or
/// removes the whole block. A fan's assignment lives on its <see cref="FanAssignRow"/>, so the controller calls
/// <see cref="Refresh"/> whenever one changes.
/// </summary>
public sealed partial class FanCheckGroup : ObservableObject
{
    public string Name { get; }

    public IReadOnlyList<FanCurveCheck> Fans { get; }

    private readonly Action<Action> _runAsStep;

    /// <param name="runAsStep">Runs the header toggle as an undo step of its own; without it the toggle just runs.</param>
    public FanCheckGroup(string name, IReadOnlyList<FanCurveCheck> fans, Action<Action>? runAsStep = null)
    {
        Name = name;
        Fans = fans;
        _runAsStep = runAsStep ?? (change => change());
    }

    private int AssignedCount => Fans.Count(f => f.Assigned);

    /// <summary>The header check: <c>true</c> = all assigned, <c>false</c> = none, <c>null</c> = some.</summary>
    public bool? AllState => AssignedCount switch
    {
        0 => false,
        int n when n == Fans.Count => true,
        _ => null,
    };

    /// <summary>Any fan of the block follows this curve - the block's border is tinted then.</summary>
    public bool AnyAssigned => AssignedCount > 0;

    /// <summary>„1 von 3" / „keiner".</summary>
    public string Summary => AssignedCount is int n and > 0
        ? Localizer.Instance.Format("FanCheckGroup.Assigned", n, Fans.Count)
        : Localizer.Instance["FanCheckGroup.NoneAssigned"];

    /// <summary>Accessible name of the header check.</summary>
    public string ToggleAllName => Localizer.Instance.Format("FanCheckGroup.ToggleAllName", Name, AssignedCount, Fans.Count);

    /// <summary>
    /// The header check: removes the block from the curve when all of it follows it, otherwise assigns all of it. One
    /// undo step, never merged into an edit just before it.
    /// </summary>
    [RelayCommand]
    private void ToggleAll()
    {
        bool assign = AllState != true;
        _runAsStep(() =>
        {
            foreach (FanCurveCheck fan in Fans)
                fan.Assigned = assign;
        });
    }

    /// <summary>Re-reads the assignments (the header and every row's check and hint).</summary>
    public void Refresh()
    {
        foreach (FanCurveCheck fan in Fans)
            fan.Refresh();
        OnPropertyChanged(nameof(AllState));
        OnPropertyChanged(nameof(AnyAssigned));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ToggleAllName));
    }
}
