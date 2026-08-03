// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using LinFan.App.Localization;
using LinFan.Core.Models;

namespace LinFan.App.Controls;

/// <summary>
/// Reine Geometrie + Positions-Algebra für <see cref="FanLocationDiagram"/>: bildet jede Gehäuse-Position
/// auf ein klickbares Rechteck ab — Silhouette (Seitenansicht) mit Rand-Zonen, seitlichem Einlass und
/// gestapelten internen Bauteilen; darunter zwei Chips für „nicht zugeordnet"/„Sonstige". Jede Kante ist
/// <b>ein Mount</b> mit umschaltbarer Richtung (Einlass/Auslass) — die beiden Varianten teilen sich eine
/// Zone (<see cref="Mount"/>/<see cref="Flip"/>). Keine Render- oder Domain-Logik, damit Hit-Test und
/// Positions-Algebra unabhängig vom Zeichnen unit-testbar sind.
/// </summary>
public static class FanLocationLayout
{
    /// <summary>Ein klickbarer Bereich: welche (Mount-)Position, sein Rechteck und das Label zum Zeichnen.</summary>
    public sealed record Region(FanLocation Location, Rect Bounds, string Label);

    // Anteile am Gehäuse-Rechteck.
    private const double EdgeV = 0.16;    // Höhe der oberen/unteren Bänder
    private const double EdgeH = 0.14;    // Breite der vorderen/hinteren Bänder
    private const double SideFrac = 0.26; // Breite der seitlichen Einlass-Spalte (Anteil am Innenraum)

    private const double Pad = 6, ChipH = 30, ChipGap = 8, CaseGap = 10;

    // Einschritt-Cache je Größe: Render + Hit feuern bei jeder Hover-/Zeiger-Bewegung, die Geometrie
    // (Bounds/Location) hängt aber allein von der Größe ab. Deshalb nur neu bauen, wenn sich die Größe ändert.
    // (Region.Label käme aus dem Localizer, wird aber nirgends konsumiert — das Diagramm leitet Labels beim
    // Zeichnen frisch aus ShortLabel ab; die Geometrie bleibt der einzige genutzte, größen-deterministische Output.)
    private static Size _cachedSize;
    private static IReadOnlyList<Region>? _cachedRegions;

    /// <summary>
    /// Liefert die Bereiche (ein Mount je Gehäuse-Kante, in konventioneller Richtung) für die Fläche. Zu
    /// klein → leer. Die Bereiche kacheln überschneidungsfrei, die Reihenfolge ist für den Hit-Test egal.
    /// </summary>
    public static IReadOnlyList<Region> Build(Size size)
    {
        IReadOnlyList<Region>? cached = _cachedRegions;
        if (cached is not null && _cachedSize == size)
            return cached;

        IReadOnlyList<Region> regions = BuildCore(size);
        _cachedRegions = regions;
        _cachedSize = size;
        return regions;
    }

    private static IReadOnlyList<Region> BuildCore(Size size)
    {
        var regions = new List<Region>(11);

        double caseH = size.Height - Pad - CaseGap - ChipH;
        double caseW = size.Width - 2 * Pad;
        if (caseW < 80 || caseH < 60)
            return regions; // kein Platz für die Silhouette

        var box = new Rect(Pad, Pad, caseW, caseH);
        double topH = box.Height * EdgeV;
        double botH = box.Height * EdgeV;
        double leftW = box.Width * EdgeH;
        double rightW = box.Width * EdgeH;

        double innerX = box.X + leftW;
        double innerY = box.Y + topH;
        double innerW = box.Width - leftW - rightW;
        double innerH = box.Height - topH - botH;

        void Add(FanLocation loc, Rect rect) => regions.Add(new(loc, rect, ShortLabel(loc)));

        // Rand-Zonen (konventionelle Richtung; umschaltbar). Ober-/Unterband voll breit, vorn/hinten nur mittig.
        Add(FanLocation.CaseTopExhaust, new Rect(box.X, box.Y, box.Width, topH));
        Add(FanLocation.CaseBottomIntake, new Rect(box.X, box.Bottom - botH, box.Width, botH));
        Add(FanLocation.CaseFrontIntake, new Rect(box.X, innerY, leftW, innerH));
        Add(FanLocation.CaseRearExhaust, new Rect(box.Right - rightW, innerY, rightW, innerH));

        // Innenraum: seitlicher Einlass als linke Spalte, dann der Bauteil-Stapel.
        double sideW = innerW * SideFrac;
        Add(FanLocation.CaseSideIntake, new Rect(innerX, innerY, sideW, innerH));

        double compX = innerX + sideW;
        double compW = innerW - sideW;
        FanLocation[] stack = { FanLocation.CpuCooler, FanLocation.GpuCooler, FanLocation.Radiator, FanLocation.Psu };
        double q = innerH / stack.Length;
        for (int i = 0; i < stack.Length; i++)
            Add(stack[i], new Rect(compX, innerY + i * q, compW, q));

        // Chips unter dem Gehäuse für die positionslosen Werte.
        double chipTop = size.Height - ChipH;
        double chipW = (box.Width - ChipGap) / 2;
        Add(FanLocation.Unspecified, new Rect(box.X, chipTop, chipW, ChipH));
        Add(FanLocation.Other, new Rect(box.X + chipW + ChipGap, chipTop, chipW, ChipH));

        return regions;
    }

    /// <summary>(Mount-)Position unter dem Punkt — konventionelle Richtung —, oder <c>null</c> wenn daneben.</summary>
    public static FanLocation? Hit(Point p, Size size)
    {
        foreach (Region r in Build(size))
            if (r.Bounds.Contains(p))
                return r.Location;
        return null;
    }

    // ── Positions-Algebra (Mount + Richtung) ─────────────────────────────────────

    /// <summary>Kurzes Diagramm-Label; spiegelt Position <b>und</b> Richtung (für den umschaltbaren Mount).</summary>
    public static string ShortLabel(FanLocation loc) => loc switch
    {
        FanLocation.CaseFrontIntake => Localizer.Instance["FanDiagram.FrontIntake"],
        FanLocation.CaseFrontExhaust => Localizer.Instance["FanDiagram.FrontExhaust"],
        FanLocation.CaseBottomIntake => Localizer.Instance["FanDiagram.BottomIntake"],
        FanLocation.CaseBottomExhaust => Localizer.Instance["FanDiagram.BottomExhaust"],
        FanLocation.CaseSideIntake => Localizer.Instance["FanDiagram.SideIntake"],
        FanLocation.CaseSideExhaust => Localizer.Instance["FanDiagram.SideExhaust"],
        FanLocation.CaseTopExhaust => Localizer.Instance["FanDiagram.TopExhaust"],
        FanLocation.CaseTopIntake => Localizer.Instance["FanDiagram.TopIntake"],
        FanLocation.CaseRearExhaust => Localizer.Instance["FanDiagram.RearExhaust"],
        FanLocation.CaseRearIntake => Localizer.Instance["FanDiagram.RearIntake"],
        FanLocation.CpuCooler => Localizer.Instance["FanDiagram.Cpu"],
        FanLocation.GpuCooler => Localizer.Instance["FanDiagram.Gpu"],
        FanLocation.Radiator => Localizer.Instance["FanDiagram.Radiator"],
        FanLocation.Psu => Localizer.Instance["FanDiagram.Psu"],
        FanLocation.Other => Localizer.Instance["FanDiagram.Other"],
        _ => Localizer.Instance["FanDiagram.Unspecified"], // Unspecified
    };

    /// <summary>Das Richtungs-Gegenstück einer Gehäuse-Position; für alles andere die Position selbst.</summary>
    public static FanLocation Flip(FanLocation loc) => loc switch
    {
        FanLocation.CaseFrontIntake => FanLocation.CaseFrontExhaust,
        FanLocation.CaseFrontExhaust => FanLocation.CaseFrontIntake,
        FanLocation.CaseBottomIntake => FanLocation.CaseBottomExhaust,
        FanLocation.CaseBottomExhaust => FanLocation.CaseBottomIntake,
        FanLocation.CaseSideIntake => FanLocation.CaseSideExhaust,
        FanLocation.CaseSideExhaust => FanLocation.CaseSideIntake,
        FanLocation.CaseTopExhaust => FanLocation.CaseTopIntake,
        FanLocation.CaseTopIntake => FanLocation.CaseTopExhaust,
        FanLocation.CaseRearExhaust => FanLocation.CaseRearIntake,
        FanLocation.CaseRearIntake => FanLocation.CaseRearExhaust,
        _ => loc,
    };

    /// <summary>Ob die Position eine umschaltbare Gehäuse-Position ist (Einlass ⇄ Auslass).</summary>
    public static bool CanFlip(FanLocation loc) => Flip(loc) != loc;

    /// <summary>
    /// Kanonischer Mount-Repräsentant: beide Richtungen einer Gehäuse-Kante teilen ihn (entspricht dem
    /// Wert, den <see cref="Build"/> für diese Zone benutzt). Für alles andere die Position selbst.
    /// </summary>
    public static FanLocation Mount(FanLocation loc) => loc switch
    {
        FanLocation.CaseFrontExhaust => FanLocation.CaseFrontIntake,
        FanLocation.CaseBottomExhaust => FanLocation.CaseBottomIntake,
        FanLocation.CaseSideExhaust => FanLocation.CaseSideIntake,
        FanLocation.CaseTopIntake => FanLocation.CaseTopExhaust,
        FanLocation.CaseRearIntake => FanLocation.CaseRearExhaust,
        _ => loc,
    };

    /// <summary>Ob zwei Positionen denselben Gehäuse-Mount meinen (oder schlicht identisch sind).</summary>
    public static bool SameMount(FanLocation a, FanLocation b) => Mount(a) == Mount(b);
}
