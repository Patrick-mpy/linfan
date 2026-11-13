// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Input;

namespace LinFan.App.Controllers;

/// <summary>
/// A notice of Settings › Airflow with the action that resolves it („Position setzen", „Kalibrieren", „Richtung
/// prüfen", „Zu den Lüftern"); <see cref="Action"/> is <c>null</c> for a plain notice.
/// </summary>
public sealed record AirflowHintRow(string Text, bool IsWarn, string ActionLabel = "", ICommand? Action = null, string Tooltip = "")
{
    public bool HasAction => Action is not null;
}
