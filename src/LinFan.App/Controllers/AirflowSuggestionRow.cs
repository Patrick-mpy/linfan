// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;

namespace LinFan.App.Controllers;

/// <summary>
/// Eine Zeile der Airflow-Vorschau: ein Lüfter, seine erkannte Position und die vorgeschlagene Kurve.
/// <see cref="Apply"/> (Checkbox) steuert, ob der Vorschlag für diesen Lüfter übernommen wird.
/// </summary>
public partial class AirflowSuggestionRow : ObservableObject
{
    public string FanId { get; }
    public string FanName { get; }
    public string LocationDisplay { get; }
    public string CurveName { get; }
    public string Reason { get; }

    /// <summary>Ob dieser Vorschlag beim „Übernehmen" angewendet wird (vom Nutzer abwählbar).</summary>
    [ObservableProperty] private bool _apply;

    public AirflowSuggestionRow(string fanId, string fanName, string locationDisplay,
                                string curveName, string reason, bool apply)
    {
        FanId = fanId;
        FanName = fanName;
        LocationDisplay = locationDisplay;
        CurveName = curveName;
        Reason = reason;
        _apply = apply;
    }
}
