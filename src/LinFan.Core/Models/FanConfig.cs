// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>Persistierte Konfiguration eines Lüfters: Name, PWM-Grenzen, zugeordnete Kurve, Kalibrierung.</summary>
public sealed record FanConfig
{
    public string FanId { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Untere PWM-Grenze (z. B. Anlaufpunkt), wird beim Anwenden der Kurve respektiert.</summary>
    public byte MinPwm { get; init; }

    public byte MaxPwm { get; init; } = 255;

    /// <summary>Id der zugeordneten Kurve, oder <c>null</c> wenn ungeregelt (Hardware-Auto).</summary>
    public string? AssignedCurveId { get; init; }

    /// <summary>Einbau-Position (für spätere Airflow-Optimierung).</summary>
    public FanLocation Location { get; init; } = FanLocation.Unspecified;

    /// <summary>Frei benennbare Gruppe zum Organisieren (z. B. „Gehäuse oben"), oder <c>null</c>.</summary>
    public string? Group { get; init; }

    /// <summary>Im Dashboard ausgeblendet (steuert nichts an der Regelung, nur Anzeige).</summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// Explizit zugeordneter Drehzahl-Sensor (Sensor-Id), der die Backend-Discovery-Heuristik
    /// <b>überschreibt</b> — gesetzt durch manuelles Zuordnen oder die automatische Sensor-Kopplung.
    /// <c>null</c> = keine Übersteuerung, es gilt der vom Backend gepaarte Tacho (oder keiner).
    /// Daemon-verwaltet: bleibt bei einem GUI-<c>SaveConfig</c>/Merge erhalten.
    /// </summary>
    public string? RpmSource { get; init; }

    public FanCalibration? Calibration { get; init; }
}
