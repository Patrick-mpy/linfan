// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// Eine Zeile des Airflow-Ergebnisses: ein Lüfter, der bereits einer Rollen-Kurve der Airflow-Analyse
/// folgt. Hält bewusst die Editor-Zeilen statt kopierter Texte - Umbenennen, Positionswechsel oder ein
/// Kurvenname ziehen so ohne Neuaufbau nach.
/// </summary>
public sealed record AirflowStatusRow(FanAssignRow Fan, CurveEditRow Curve);
