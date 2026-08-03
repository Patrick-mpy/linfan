// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Daemon.Tests;

/// <summary>In-Memory-<see cref="IConfigStore"/> für Tests: protokolliert gespeicherte Konfigurationen.</summary>
internal sealed class FakeConfigStore : IConfigStore
{
    public AppConfig Stored { get; set; } = AppConfig.Empty;
    public List<AppConfig> Saves { get; } = new();
    public int LoadCount { get; private set; }

    public string ConfigPath => "(in-memory)";

    /// <summary>
    /// Simuliert das Vorhandensein einer Config-Datei (First-Run-Signal). Frei setzbar, um eine
    /// bestehende Installation nachzustellen; ein <see cref="Save"/> setzt es automatisch auf <c>true</c>.
    /// Default <c>false</c> = frische Installation.
    /// </summary>
    public bool Exists { get; set; }

    public AppConfig Load()
    {
        LoadCount++;
        return Stored;
    }

    public void Save(AppConfig config)
    {
        Stored = config;
        Saves.Add(config);
        Exists = true;
    }
}
