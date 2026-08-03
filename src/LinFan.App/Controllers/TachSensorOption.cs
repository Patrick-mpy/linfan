// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.App.Controllers;

/// <summary>
/// Ein Eintrag im „Drehzahl-Sensor"-Dropdown einer Lüfterzeile: ein verfügbarer RPM-Sensor bzw. der
/// „keiner"-Eintrag (<see cref="Id"/> == <c>null</c>), der die manuelle Zuordnung löscht. <see cref="Name"/>
/// ist der Anzeigetext. Referenzstabil (geteilte Controller-Liste) → taugt als <c>SelectedItem</c>-Ziel.
/// </summary>
public sealed record TachSensorOption(string? Id, string Name);
