// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Ipc.Messages;

/// <summary>
/// Betriebszustand des Daemons (für die Statuszeile der GUI). Codifiziert die früher als deutscher
/// String gesendete Meldung, damit die App sie lokalisieren kann.
/// </summary>
public enum DaemonStatus
{
    /// <summary>Daemon steuert die Hardware aktiv (privilegiert). Früher: „Aktiv".</summary>
    Active,

    /// <summary>
    /// Ohne erhöhte Rechte: es wird gerechnet/angezeigt, aber nichts geschrieben. Früher:
    /// „Dry-Run (kein &lt;Elevation-Term&gt;)". Der plattformspezifische Elevation-Term wird NICHT
    /// mitgesendet - die App formuliert die Meldung generisch. Das <c>DryRun</c>-Flag des
    /// <see cref="IpcSnapshot"/> bleibt zusätzlich erhalten.
    /// </summary>
    DryRun,
}
