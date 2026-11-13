// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controllers;

/// <summary>
/// One change of the airflow proposal for the target profile: a fan, the curve it follows there now (struck through)
/// and the one its position suggests. <see cref="Apply"/> is the user's tick - unticked rows stay as they are.
/// </summary>
public sealed partial class AirflowChangeRow : ObservableObject
{
    public AirflowChangeRow(AirflowChange change, string fanName, string positionText, AirflowDirection direction,
                            string fromText, string toText, bool isNewCurve, bool isCurveOff, bool apply)
    {
        Change = change;
        FanName = fanName;
        PositionText = positionText;
        Direction = direction;
        FromText = fromText;
        ToText = toText;
        IsNewCurve = isNewCurve;
        IsCurveOff = isCurveOff;
        _apply = apply;
    }

    public AirflowChange Change { get; }
    public string FanName { get; }

    /// <summary>The fan's position, short („Vorn · Einlass", „ohne Position").</summary>
    public string PositionText { get; }

    /// <summary>Colours the dot before the position.</summary>
    public AirflowDirection Direction { get; }

    public string FromText { get; }
    public string ToText { get; }

    /// <summary>The target profile lacks the suggested curve - taking the row adds it from the template.</summary>
    public bool IsNewCurve { get; }

    /// <summary>
    /// The target profile has the suggested curve, but switched off: taking the row leaves the fan on hardware auto, not
    /// on the curve the arrow names.
    /// </summary>
    public bool IsCurveOff { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNew), nameof(ShowCurveOff), nameof(AutomationName))]
    private bool _apply;

    /// <summary>The „neu" mark only while the row is ticked (unticked, nothing gets created).</summary>
    public bool ShowNew => IsNewCurve && Apply;

    /// <summary>The „ausgeschaltet" mark, like <see cref="ShowNew"/> only while the row is ticked.</summary>
    public bool ShowCurveOff => IsCurveOff && Apply;

    /// <summary>
    /// The change the tick stands for, for screen readers („Front oben: Lüfterkurve → Airflow · Einlass"), with the
    /// „ausgeschaltet" mark when the screen shows it.
    /// </summary>
    public string AutomationName => ShowCurveOff
        ? $"{FanName}: {FromText} → {ToText} ({Localizer.Instance["Airflow.CurveOff"]})"
        : $"{FanName}: {FromText} → {ToText}";
}
