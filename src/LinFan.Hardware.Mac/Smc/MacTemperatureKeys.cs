// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Hardware.Mac.Smc;

/// <summary>
/// Kuratierte Tabelle bekannter SMC-Temperatur-Keys (4-Zeichen-Code → Anzeigename). Bewusst eine
/// <b>Positivliste</b> statt einer Heuristik: Auf Apple Silicon ist der <c>flt</c>-Typ massiv überladen
/// (Watt, Volt, Prozente, Drehzahlen), eine Typ-/Bereichs-Heuristik lieferte auf echter Hardware ~500
/// Rausch-Kanäle. Die hier gelisteten Keys sind über Jahre in verbreiteten Tools (smcFanControl, iStat,
/// Macs Fan Control) dokumentiert und stabil — überwiegend Intel-Macs. Nur Keys, die zur Laufzeit
/// <b>existieren und einen plausiblen Wert liefern</b>, werden zu Sensoren (siehe <see cref="MacSmcBackend"/>).
/// <para>
/// Bekannte Grenze: Apple Silicon exponiert kaum stabile SMC-Temp-Keys — dort bleibt die Liste meist leer
/// (Lüfter-RPM ist weiterhin lesbar). Ein per-Chip-Ausbau bzw. IOReport ist Folgearbeit (siehe todo.md).
/// </para>
/// </summary>
internal static class MacTemperatureKeys
{
    /// <summary>Kuratierte (Key, Anzeigename)-Paare. Reihenfolge = Anzeigereihenfolge.</summary>
    public static readonly IReadOnlyList<(string Key, string Name)> Known = new (string, string)[]
    {
        ("TC0P", "CPU Proximity"),
        ("TC0D", "CPU Die"),
        ("TC0E", "CPU"),
        ("TC0F", "CPU"),
        ("TC0H", "CPU Heatsink"),
        ("TCXC", "CPU PECI"),
        ("TCGC", "CPU GFX Core"),
        ("TG0P", "GPU Proximity"),
        ("TG0D", "GPU Die"),
        ("TG0H", "GPU Heatsink"),
        ("TA0P", "Ambient"),
        ("TA1P", "Ambient 2"),
        ("Th0H", "Heatpipe 1"),
        ("Th1H", "Heatpipe 2"),
        ("Th2H", "Heatpipe 3"),
        ("Tm0P", "Mainboard Proximity"),
        ("TM0P", "Memory Proximity"),
        ("Tp0P", "Power Supply Proximity"),
        ("Ts0P", "Palm Rest"),
        ("Ts1P", "Palm Rest 2"),
        ("TB0T", "Battery"),
        ("TB1T", "Battery 2"),
        ("TW0P", "Airport / Wi-Fi"),
        ("TL0P", "Display Proximity"),
        ("TN0D", "Northbridge Die"),
        ("TN0P", "Northbridge Proximity"),
        ("TPCD", "Platform Controller Hub Die"),
        ("TH0P", "Drive Bay 1"),
        ("TH1P", "Drive Bay 2"),
    };
}
