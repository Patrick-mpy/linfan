// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;

namespace LinFan.Core.Abstractions;

/// <summary>Lädt und speichert die <see cref="AppConfig"/> (JSON-Persistenz).</summary>
public interface IConfigStore
{
    /// <summary>Voller Pfad der Konfigurationsdatei.</summary>
    string ConfigPath { get; }

    /// <summary>
    /// Gibt an, ob bereits eine persistierte Konfiguration existiert - First-Run-Signal.
    /// </summary>
    bool Exists { get; }

    /// <summary>Lädt die Konfiguration; gibt <see cref="AppConfig.Empty"/> zurück, wenn keine existiert.</summary>
    AppConfig Load();

    /// <summary>Speichert die Konfiguration atomar.</summary>
    void Save(AppConfig config);
}
