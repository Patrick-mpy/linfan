// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Core.Services;

/// <summary>
/// Fasst die Live-Werte mehrerer Quell-Sensoren einer Kurve zu einem einzelnen Eingangswert zusammen.
/// Nicht lesbare Sensoren (<see cref="double.NaN"/>, z. B. EIO) werden ignoriert; sind alle Quellen
/// unlesbar (oder die Liste leer), ist das Ergebnis <see cref="double.NaN"/> — der Aufrufer behandelt
/// das wie „kein Wert" (kein PWM-Schreiben ohne gültige Temperatur).
/// </summary>
public static class SensorAggregator
{
    /// <summary>Liest die Quell-Sensoren über das Backend und fasst sie zusammen.</summary>
    public static double Aggregate(IReadOnlyList<string> ids, ISensorBackend backend, SensorAggregation mode)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(backend);

        return Aggregate(ids.Select(id => ReadOrNaN(backend, id)), mode);
    }

    /// <summary>
    /// Heißeste lesbare Temperatur über ALLE Temperatur-Sensoren des Backends — oder <see cref="double.NaN"/>,
    /// wenn keine lesbar ist. <b>Wirft nie</b> (Fail-Safe-Watchdog): eine werfende Discovery ODER ein einzelner
    /// werfender Sensor (EIO o. Ä.) darf den Watchdog-Tick nicht abreißen — Discovery-Fehler ⇒ NaN, ein
    /// kaputter Kanal wird übersprungen. Von Regel-Loop UND Kalibrier-Watchdog genutzt (eine Quelle statt
    /// Duplikaten). NaN führt beim Aufrufer über die Blind-Tick-Logik in den sicheren Zustand.
    /// </summary>
    public static double Hottest(ISensorBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        IReadOnlyList<SensorDescriptor> sensors;
        try { sensors = backend.DiscoverSensors(); }
        catch { return double.NaN; } // Discovery kaputt → „keine Temperatur lesbar"

        double max = double.NaN;
        foreach (SensorDescriptor s in sensors)
        {
            if (s.Kind != SensorKind.Temperature)
                continue;
            double v = ReadOrNaN(backend, s.Id.Value); // wirft nie → kaputter Kanal wird übersprungen
            if (!double.IsNaN(v) && (double.IsNaN(max) || v > max))
                max = v;
        }
        return max;
    }

    /// <summary>
    /// Liest einen Quell-Sensor defensiv: Die IDs stammen aus der gespeicherten Config und können auf einen
    /// Sensor zeigen, den das Backend gerade <b>nicht</b> kennt (z. B. weil sich die hwmon-Nummerierung seit
    /// dem Speichern geändert hat) — laut Vertrag darf <see cref="ISensorBackend.ReadValue"/> dafür werfen.
    /// <b>Jede</b> Backend-Exception (nicht nur <see cref="KeyNotFoundException"/>: auch <c>IOException</c>/EIO,
    /// unerwartete Fehler) zählt hier wie „nicht lesbar" → <see cref="double.NaN"/>, statt den ganzen Regel-Tick
    /// abzureißen (Fail-Safe: eine einzelne fehlende/kaputte Quelle darf nicht die Regelung ALLER Lüfter — und
    /// damit den Übertemp-Watchdog — stoppen).
    /// </summary>
    private static double ReadOrNaN(ISensorBackend backend, string id)
    {
        try
        {
            return backend.ReadValue(new SensorId(id));
        }
        catch
        {
            return double.NaN;
        }
    }

    /// <summary>
    /// Reine Aggregations-Kernregel über bereits gelesene Werte (für Aufrufer ohne <see cref="ISensorBackend"/>,
    /// z. B. die Live-Vorschau im Kurven-Editor). <see cref="double.NaN"/>-Werte werden ignoriert; ohne gültigen
    /// Wert ist das Ergebnis <see cref="double.NaN"/>.
    /// </summary>
    public static double Aggregate(IEnumerable<double> values, SensorAggregation mode)
    {
        ArgumentNullException.ThrowIfNull(values);

        double sum = 0;
        double max = double.NaN;
        int count = 0;

        foreach (double value in values)
        {
            if (double.IsNaN(value))
                continue;

            count++;
            sum += value;
            if (double.IsNaN(max) || value > max)
                max = value;
        }

        if (count == 0)
            return double.NaN;

        return mode == SensorAggregation.Avg ? sum / count : max;
    }
}
