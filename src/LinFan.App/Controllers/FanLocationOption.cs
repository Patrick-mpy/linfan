// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Localization;
using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

/// <summary>Anzeige-Option für <see cref="FanLocation"/> im Editor (Enum + lokalisierter Klartext).
/// <see cref="Display"/> wird zur Laufzeit aus <see cref="Localizer"/> berechnet, damit ein
/// Sprachwechsel live greift; der besitzende Controller muss seine <see cref="All"/>-ItemsSource
/// bei <c>Localizer.PropertyChanged</c> neu freigeben, damit die ComboBox die Items neu rendert.</summary>
public sealed record FanLocationOption(FanLocation Value, string Key)
{
    public static readonly IReadOnlyList<FanLocationOption> All = new[]
    {
        new FanLocationOption(FanLocation.Unspecified, "FanLocationOption.Unspecified"),
        new FanLocationOption(FanLocation.CpuCooler, "FanLocationOption.CpuCooler"),
        new FanLocationOption(FanLocation.GpuCooler, "FanLocationOption.GpuCooler"),
        new FanLocationOption(FanLocation.Radiator, "FanLocationOption.Radiator"),
        // Einlass-Varianten
        new FanLocationOption(FanLocation.CaseFrontIntake, "FanLocationOption.CaseFrontIntake"),
        new FanLocationOption(FanLocation.CaseBottomIntake, "FanLocationOption.CaseBottomIntake"),
        new FanLocationOption(FanLocation.CaseSideIntake, "FanLocationOption.CaseSideIntake"),
        new FanLocationOption(FanLocation.CaseTopIntake, "FanLocationOption.CaseTopIntake"),
        new FanLocationOption(FanLocation.CaseRearIntake, "FanLocationOption.CaseRearIntake"),
        // Auslass-Varianten
        new FanLocationOption(FanLocation.CaseRearExhaust, "FanLocationOption.CaseRearExhaust"),
        new FanLocationOption(FanLocation.CaseTopExhaust, "FanLocationOption.CaseTopExhaust"),
        new FanLocationOption(FanLocation.CaseFrontExhaust, "FanLocationOption.CaseFrontExhaust"),
        new FanLocationOption(FanLocation.CaseBottomExhaust, "FanLocationOption.CaseBottomExhaust"),
        new FanLocationOption(FanLocation.CaseSideExhaust, "FanLocationOption.CaseSideExhaust"),
        new FanLocationOption(FanLocation.Psu, "FanLocationOption.Psu"),
        new FanLocationOption(FanLocation.Other, "FanLocationOption.Other"),
    };

    public static FanLocationOption For(FanLocation value) =>
        All.FirstOrDefault(o => o.Value == value) ?? All[0];

    /// <summary>Lokalisierter Anzeigetext (berechnet aus <see cref="Key"/>).</summary>
    public string Display => Localizer.Instance[Key];

    /// <summary>Anzeigetext fürs Dashboard; <see cref="FanLocation.Unspecified"/> → leer (nichts anzeigen).</summary>
    public static string DisplayFor(FanLocation value) =>
        value == FanLocation.Unspecified ? "" : For(value).Display;

    /// <summary>
    /// Luftstrom-Richtung einer Position - App-seitige Brücke zur Domänen-Abbildung
    /// (<see cref="AirflowTuneService.DirectionOf"/>), damit die View (Diagramm/Dialog) die Richtung über die
    /// Controller-Schicht bezieht, statt LinFan.Core.Services direkt zu referenzieren.
    /// </summary>
    public static AirflowDirection DirectionOf(FanLocation value) => AirflowTuneService.DirectionOf(value);

    /// <summary>Knapper Gruppenname für die Auto-Gruppierung nach Position (kürzer als <see cref="DisplayFor"/>);
    /// <see cref="FanLocation.Unspecified"/> → leer.</summary>
    public static string GroupNameFor(FanLocation value) => value switch
    {
        FanLocation.CpuCooler => Localizer.Instance["FanLocationOption.Group.CpuCooler"],
        FanLocation.GpuCooler => Localizer.Instance["FanLocationOption.Group.GpuCooler"],
        FanLocation.Radiator => Localizer.Instance["FanLocationOption.Group.Radiator"],
        FanLocation.CaseFrontIntake => Localizer.Instance["FanLocationOption.Group.FrontIntake"],
        FanLocation.CaseBottomIntake => Localizer.Instance["FanLocationOption.Group.BottomIntake"],
        FanLocation.CaseSideIntake => Localizer.Instance["FanLocationOption.Group.SideIntake"],
        FanLocation.CaseTopIntake => Localizer.Instance["FanLocationOption.Group.TopIntake"],
        FanLocation.CaseRearIntake => Localizer.Instance["FanLocationOption.Group.RearIntake"],
        FanLocation.CaseRearExhaust => Localizer.Instance["FanLocationOption.Group.RearExhaust"],
        FanLocation.CaseTopExhaust => Localizer.Instance["FanLocationOption.Group.TopExhaust"],
        FanLocation.CaseFrontExhaust => Localizer.Instance["FanLocationOption.Group.FrontExhaust"],
        FanLocation.CaseBottomExhaust => Localizer.Instance["FanLocationOption.Group.BottomExhaust"],
        FanLocation.CaseSideExhaust => Localizer.Instance["FanLocationOption.Group.SideExhaust"],
        FanLocation.Psu => Localizer.Instance["FanLocationOption.Group.Psu"],
        FanLocation.Other => Localizer.Instance["FanLocationOption.Group.Other"],
        _ => "",
    };

    public override string ToString() => Display;
}
