// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Abstractions;
using LinFan.Core.Models;

namespace LinFan.Daemon.Tests;

/// <summary>In-Memory-<see cref="IConfigStore"/> für Tests: protokolliert gespeicherte Konfigurationen.</summary>
internal sealed class FakeConfigStore : IConfigStore
{
    private readonly object _gate = new();
    private readonly List<AppConfig> _saves = new();

    public AppConfig Stored { get; set; } = AppConfig.Empty;

    /// <summary>
    /// A copy of the configs saved so far: the service's loop thread saves while a test reads, so a test must never
    /// enumerate the live list (that threw "Collection was modified" in CI). Read-only, so a test cannot clear a copy by
    /// mistake - <see cref="ClearSaves"/> does that.
    /// </summary>
    public IReadOnlyList<AppConfig> Saves
    {
        get
        {
            lock (_gate) return _saves.ToArray();
        }
    }

    /// <summary>Forgets the saves so far, e.g. the one the start made, before the step under test.</summary>
    public void ClearSaves()
    {
        lock (_gate) _saves.Clear();
    }

    public int LoadCount { get; private set; }

    public string ConfigPath => "(in-memory)";

    /// <summary>
    /// Simuliert das Vorhandensein einer Config-Datei (First-Run-Signal). Frei setzbar, um eine
    /// bestehende Installation nachzustellen; ein <see cref="Save"/> setzt es automatisch auf <c>true</c>.
    /// Default <c>false</c> = frische Installation.
    /// </summary>
    public bool Exists { get; set; }

    /// <summary>Makes <see cref="Load"/> throw until <see cref="SetAside"/> runs, like a config the daemon cannot use.</summary>
    public Exception? LoadFailure { get; set; }

    public AppConfig Load()
    {
        LoadCount++;
        if (LoadFailure is { } failure)
            throw failure;
        return Stored;
    }

    /// <summary>The config that <see cref="SetAside"/> moved out of the way, if any.</summary>
    public AppConfig? SetAsideConfig { get; private set; }

    public bool ThrowOnSetAside { get; set; }

    public string SetAside()
    {
        if (ThrowOnSetAside)
            throw new IOException("Set aside failed (simulated).");
        SetAsideConfig = Stored;
        Stored = AppConfig.Empty;
        Exists = false;
        LoadFailure = null;
        return "(in-memory).invalid";
    }

    /// <summary>Makes every <see cref="Save"/> throw, like a config that fails to apply.</summary>
    public bool ThrowOnSave { get; set; }

    public void Save(AppConfig config)
    {
        if (ThrowOnSave)
            throw new IOException("Save failed (simulated).");
        Stored = config;
        lock (_gate) _saves.Add(config);
        Exists = true;
    }
}
