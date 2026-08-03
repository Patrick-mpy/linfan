// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Hardware.Mac.Smc;

/// <summary>
/// Kuratierte Tabelle bekannter SMC-Temperatur-Keys (4-Zeichen-Code → Anzeigename). Bewusst eine
/// <b>Positivliste</b> statt einer Heuristik: Auf Apple Silicon ist der <c>flt</c>-Typ massiv überladen
/// (Watt, Volt, Prozente, Drehzahlen), eine Typ-/Bereichs-Heuristik lieferte auf echter Hardware ~500
/// Rausch-Kanäle. Quelle der Key-Bedeutungen sind die über Jahre in verbreiteten Open-Source-Tools
/// (smcFanControl, iStats, exelban/stats, Macs Fan Control) dokumentierten Listen — nur Keys mit dort
/// gesicherter Bedeutung sind aufgenommen. Nur Keys, die zur Laufzeit <b>existieren und einen plausiblen
/// Wert liefern</b>, werden zu Sensoren (siehe <see cref="MacSmcBackend"/>).
/// <para>
/// <b>Reihenfolge = Anzeigereihenfolge</b> (CPU → GPU → SoC/Board/Memory → Storage → Battery →
/// Sonstiges): <see cref="MacSmcBackend"/> sortiert die Temperatur-Deskriptoren nach der Position in
/// dieser Tabelle, damit die GUI-Liste stabil gruppiert ist statt alphabetisch gewürfelt. Anzeigenamen
/// sind bewusst neutral englisch (Backend-Konvention); die GUI kann umbenennen.
/// </para>
/// <para>
/// <b>Apple Silicon braucht per-Familie-Tabellen</b> (<see cref="SelectAppleSiliconCluster"/>): dieselben
/// Keys bedeuten je nach Chip-Familie etwas anderes (<c>Tp09</c> = M1 E-Core 1, aber M2 P-Core 3;
/// <c>Tp0P</c> = M1 P-Core 6, aber Intel Netzteil-Proximity). Die Familie wird über die Präsenz ihrer
/// Keys erkannt — Chip-Familien schließen sich auf einer Maschine gegenseitig aus. M4-spezifische
/// Core-Keys fehlen bewusst (in den Referenz-Tools nicht eindeutig genug dokumentiert); dort greifen
/// die plattformübergreifenden Keys der flachen Liste.
/// </para>
/// </summary>
internal static class MacTemperatureKeys
{
    /// <summary>
    /// Kuratierte (Key, Anzeigename)-Paare für Intel-Macs und plattformübergreifende Sensoren.
    /// Reihenfolge = Anzeigereihenfolge; die Gruppen-Kommentare markieren die Anzeige-Gruppen.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Name)> Known = new (string, string)[]
    {
        // --- CPU (Intel) ------------------------------------------------------
        ("TC0P", "CPU Proximity"),
        ("TC0D", "CPU Die"),
        ("TC0E", "CPU Die (Virtual)"),
        ("TC0F", "CPU Die (Filtered)"),
        ("TC0H", "CPU Heatsink"),
        ("TCXC", "CPU PECI"),
        ("TCSA", "CPU System Agent"),

        // --- GPU (Intel/diskret) ----------------------------------------------
        ("TG0D", "GPU Die"),
        ("TG0P", "GPU Proximity"),
        ("TG0H", "GPU Heatsink"),
        ("TCGC", "Integrated Graphics"),

        // --- SoC / Board / Memory ---------------------------------------------
        ("TM0P", "Memory Proximity"),
        ("Tm0P", "Mainboard Proximity"),
        ("TN0D", "Northbridge Die"),
        ("TN0P", "Northbridge Proximity"),
        ("TPCD", "Platform Controller Hub Die"),
        ("Tp0P", "Power Supply Proximity"),

        // --- Storage ----------------------------------------------------------
        ("TH0x", "SSD (NAND)"),
        ("TH0P", "Drive Bay 1"),
        ("TH1P", "Drive Bay 2"),

        // --- Battery ----------------------------------------------------------
        ("TB0T", "Battery"),
        ("TB1T", "Battery 1"),
        ("TB2T", "Battery 2"),

        // --- Sonstiges (Ambient, Airflow, Gehäuse, Display, Funk) -------------
        ("TA0P", "Ambient"),
        ("TA1P", "Ambient 2"),
        ("TaLP", "Airflow Left"),
        ("TaRF", "Airflow Right"),
        ("Th0H", "Heatpipe 1"),
        ("Th1H", "Heatpipe 2"),
        ("Th2H", "Heatpipe 3"),
        ("Ts0P", "Palm Rest"),
        ("Ts1P", "Palm Rest 2"),
        ("TL0P", "Display Proximity"),
        ("TW0P", "Airport / Wi-Fi"),
    };

    /// <summary>
    /// Mindestzahl vorhandener Keys, damit eine Apple-Silicon-Familie als erkannt gilt. Verhindert,
    /// dass ein einzelner mehrdeutiger Key eine Familie auslöst (Intel-Macs haben <c>Tp0P</c> =
    /// Netzteil — ein Treffer in der M1-Tabelle, aber kein M1).
    /// </summary>
    private const int MinFamilyHits = 3;

    /// <summary>M1 / M1 Pro / M1 Max / M1 Ultra — Cluster-Reihenfolge: E-Cores → P-Cores → GPU.</summary>
    private static readonly (string Key, string Name)[] M1 =
    {
        ("Tp09", "CPU Efficiency Core 1"),
        ("Tp0T", "CPU Efficiency Core 2"),
        ("Tp01", "CPU Performance Core 1"),
        ("Tp05", "CPU Performance Core 2"),
        ("Tp0D", "CPU Performance Core 3"),
        ("Tp0H", "CPU Performance Core 4"),
        ("Tp0L", "CPU Performance Core 5"),
        ("Tp0P", "CPU Performance Core 6"),
        ("Tp0X", "CPU Performance Core 7"),
        ("Tp0b", "CPU Performance Core 8"),
        ("Tg05", "GPU 1"),
        ("Tg0D", "GPU 2"),
        ("Tg0L", "GPU 3"),
        ("Tg0T", "GPU 4"),
    };

    /// <summary>M2 / M2 Pro / M2 Max / M2 Ultra — Cluster-Reihenfolge: E-Cores → P-Cores → GPU.</summary>
    private static readonly (string Key, string Name)[] M2 =
    {
        ("Tp1h", "CPU Efficiency Core 1"),
        ("Tp1t", "CPU Efficiency Core 2"),
        ("Tp1p", "CPU Efficiency Core 3"),
        ("Tp1l", "CPU Efficiency Core 4"),
        ("Tp01", "CPU Performance Core 1"),
        ("Tp05", "CPU Performance Core 2"),
        ("Tp09", "CPU Performance Core 3"),
        ("Tp0D", "CPU Performance Core 4"),
        ("Tp0X", "CPU Performance Core 5"),
        ("Tp0b", "CPU Performance Core 6"),
        ("Tp0f", "CPU Performance Core 7"),
        ("Tp0j", "CPU Performance Core 8"),
        ("Tg0f", "GPU 1"),
        ("Tg0j", "GPU 2"),
    };

    /// <summary>M3 / M3 Pro / M3 Max — Cluster-Reihenfolge: E-Cores → P-Cores → GPU.</summary>
    private static readonly (string Key, string Name)[] M3 =
    {
        ("Te05", "CPU Efficiency Core 1"),
        ("Te0L", "CPU Efficiency Core 2"),
        ("Te0P", "CPU Efficiency Core 3"),
        ("Te0S", "CPU Efficiency Core 4"),
        ("Tf04", "CPU Performance Core 1"),
        ("Tf09", "CPU Performance Core 2"),
        ("Tf0A", "CPU Performance Core 3"),
        ("Tf0B", "CPU Performance Core 4"),
        ("Tf0D", "CPU Performance Core 5"),
        ("Tf0E", "CPU Performance Core 6"),
        ("Tf44", "CPU Performance Core 7"),
        ("Tf49", "CPU Performance Core 8"),
        ("Tf4A", "CPU Performance Core 9"),
        ("Tf4B", "CPU Performance Core 10"),
        ("Tf4D", "CPU Performance Core 11"),
        ("Tf4E", "CPU Performance Core 12"),
        ("Tf14", "GPU 1"),
        ("Tf18", "GPU 2"),
        ("Tf19", "GPU 3"),
        ("Tf1A", "GPU 4"),
        ("Tf24", "GPU 5"),
        ("Tf28", "GPU 6"),
        ("Tf29", "GPU 7"),
        ("Tf2A", "GPU 8"),
    };

    private static readonly (string Key, string Name)[][] AppleSiliconFamilies = { M1, M2, M3 };

    /// <summary>
    /// Wählt die Apple-Silicon-Familie mit den meisten vorhandenen Keys und liefert deren kuratierte
    /// Cluster-Tabelle (E-Cores → P-Cores → GPU, Reihenfolge = Anzeigereihenfolge). Unterhalb von
    /// <see cref="MinFamilyHits"/> Treffern gilt keine Familie als erkannt und das Ergebnis ist leer —
    /// lieber kein Sensor als ein falsch beschrifteter. Die Zählung nutzt nur die <b>Präsenz</b> der
    /// Keys (<paramref name="keyExists"/>), nicht ihre Werte.
    /// </summary>
    public static IReadOnlyList<(string Key, string Name)> SelectAppleSiliconCluster(
        Func<string, bool> keyExists)
    {
        (string Key, string Name)[] best = Array.Empty<(string, string)>();
        int bestHits = MinFamilyHits - 1;

        foreach (var family in AppleSiliconFamilies)
        {
            int hits = family.Count(e => keyExists(e.Key));
            if (hits > bestHits)
            {
                bestHits = hits;
                best = family;
            }
        }

        return best;
    }
}
