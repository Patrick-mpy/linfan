// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Core.Models;

/// <summary>
/// Wurzel der persistierten Konfiguration (eine JSON-Datei). <see cref="SchemaVersion"/> erlaubt
/// spätere Migrationen.
/// </summary>
public sealed record AppConfig
{
    /// <summary>
    /// Aktuelle Schema-Version. 3 = stabile <c>chip/channel</c>-Hardware-Ids (vorher <c>hwmonN/…</c>,
    /// instabil über Reboots). Older schemas only existed before the first release; their migration is gone - an
    /// <c>hwmonN/…</c> id today is the fallback key of a chip the backend cannot tell apart otherwise.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Poll-Intervall des Regel-Loops in Millisekunden.</summary>
    public int PollIntervalMs { get; init; } = 1000;

    /// <summary>Zentrale Default-Obergrenze für den Fail-Safe (eine Quelle für alle Fail-Safe-Defaults).</summary>
    public const double DefaultFailSafeTempC = 90.0;

    /// <summary>Temperatur-Obergrenze: darüber greift der Fail-Safe (Lüfter auf Hardware-Auto/100 %).</summary>
    public double FailSafeTempC { get; init; } = DefaultFailSafeTempC;

    public IReadOnlyList<SensorConfig> Sensors { get; init; } = [];

    /// <summary>
    /// Sensor group names in display order, including groups no sensor is in yet. A group's identity is its name,
    /// the one <see cref="SensorConfig.Group"/> carries, compared case-insensitively - a group that sensors use but
    /// this list lacks still exists. Missing in older JSON, the list reads as empty (no schema bump).
    /// </summary>
    public IReadOnlyList<string> SensorGroups { get; init; } = [];

    public IReadOnlyList<FanConfig> Fans { get; init; } = [];

    /// <summary>
    /// The kind of case: which case positions exist and whether a pressure balance applies. Display and airflow
    /// analysis only - the control loop does not read it. Missing in older JSON, it reads as <see cref="CaseType.Tower"/>.
    /// </summary>
    public CaseType CaseType { get; init; } = CaseType.Tower;
    public IReadOnlyList<CurveConfig> Curves { get; init; } = [];

    /// <summary>Umschaltbare Zuordnungs-Sets; das aktive (<see cref="ActiveProfileId"/>) steuert die Lüfter.</summary>
    public IReadOnlyList<Profile> Profiles { get; init; } = [];

    public string? ActiveProfileId { get; init; }

    /// <summary>
    /// Onboarding-Status. <c>null</c> = Status unbekannt (Altbestand ohne dieses Feld),
    /// <c>false</c> = First-Run (Onboarding-Assistent anzeigen), <c>true</c> = abgeschlossen oder übersprungen.
    /// </summary>
    public bool? OnboardingCompleted { get; init; }

    public static AppConfig Empty => new();
}
