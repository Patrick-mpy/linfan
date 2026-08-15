// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.Core.Tests;

/// <summary>Tests für <see cref="JsonConfigStore.Exists"/> (First-Run-Signal).</summary>
// Serialisiert mit JsonConfigStoreTests über LINFAN_CONFIG - siehe Hinweis dort.
[Collection("env-config")]
public sealed class JsonConfigStoreExistsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"linfan-exists-test-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_dir, "config.json");

    [Fact]
    public void Exists_BeforeSave_ReturnsFalse()
    {
        var store = new JsonConfigStore(ConfigPath);

        Assert.False(store.Exists);
    }

    [Fact]
    public void Exists_AfterSave_ReturnsTrue()
    {
        var store = new JsonConfigStore(ConfigPath);
        store.Save(AppConfig.Empty);

        Assert.True(store.Exists);
    }

    [Fact]
    public void Exists_HonorsLinfanConfigEnvOverride()
    {
        string? previous = Environment.GetEnvironmentVariable("LINFAN_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("LINFAN_CONFIG", ConfigPath);

            var store = new JsonConfigStore(); // kein expliziter Pfad → Override greift
            Assert.False(store.Exists);

            store.Save(AppConfig.Empty);
            Assert.True(store.Exists);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LINFAN_CONFIG", previous);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
