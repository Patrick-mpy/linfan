// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

// Serialisiert mit JsonConfigStoreExistsTests: beide mutieren die prozess-globale Env-Var
// LINFAN_CONFIG; ohne gemeinsame Collection liefen sie parallel und überschrieben sich gegenseitig.
[Collection("env-config")]
public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"linfan-test-{Guid.NewGuid():N}");
    private string Path_ => System.IO.Path.Combine(_dir, "config.json");

    [Fact]
    public void Load_MissingFile_ReturnsEmptyDefaults()
    {
        var config = new JsonConfigStore(Path_).Load();

        Assert.Empty(config.Fans);
        Assert.Empty(config.Curves);
        Assert.Equal(1000, config.PollIntervalMs);
        Assert.Equal(90, config.FailSafeTempC);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var store = new JsonConfigStore(Path_);
        var config = new AppConfig
        {
            PollIntervalMs = 1500,
            FailSafeTempC = 88,
            Curves = new[]
            {
                new CurveConfig
                {
                    Id = "c1", Name = "Quiet", SourceSensorId = "hwmon6/temp1", HysteresisC = 3,
                    Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
                },
            },
            Fans = new[]
            {
                new FanConfig { FanId = "hwmon7/pwm1", Name = "CPU", AssignedCurveId = "c1", MinPwm = 60 },
            },
        };

        store.Save(config);
        var loaded = store.Load();

        Assert.Equal(1500, loaded.PollIntervalMs);
        Assert.Equal(88, loaded.FailSafeTempC);
        Assert.Equal("Quiet", Assert.Single(loaded.Curves).Name);
        Assert.Equal(2, loaded.Curves[0].Points.Count);
        Assert.Equal(3, loaded.Curves[0].HysteresisC);
        var fan = Assert.Single(loaded.Fans);
        Assert.Equal((byte)60, fan.MinPwm);
        Assert.Equal("c1", fan.AssignedCurveId);
    }

    [Fact]
    public void Save_IsAtomic_NoLeftoverTempFile()
    {
        var store = new JsonConfigStore(Path_);
        store.Save(AppConfig.Empty);
        Assert.True(File.Exists(Path_));
        Assert.False(File.Exists(Path_ + ".tmp"));
    }

    [Fact]
    public void Save_BareFilenameWithoutDirectory_WritesInCurrentDirectory()
    {
        // Path.GetDirectoryName("config.json") liefert "" (nicht null) — früher warf Save darüber in
        // Directory.CreateDirectory(""). Ein reiner Dateiname landet im aktuellen Arbeitsverzeichnis.
        Directory.CreateDirectory(_dir);
        string previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_dir);
            var store = new JsonConfigStore("config.json");

            var ex = Record.Exception(() => store.Save(AppConfig.Empty));

            Assert.Null(ex);
            Assert.True(File.Exists(System.IO.Path.Combine(_dir, "config.json")));
            Assert.False(File.Exists(System.IO.Path.Combine(_dir, "config.json.tmp")));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    [Fact]
    public void Load_CorruptFile_Throws()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ kein gültiges json");
        Assert.Throws<InvalidOperationException>(() => new JsonConfigStore(Path_).Load());
    }

    [Fact]
    public void DefaultPath_HonorsLinfanConfigEnv()
    {
        string? previous = Environment.GetEnvironmentVariable("LINFAN_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("LINFAN_CONFIG", Path_);

            Assert.Equal(Path_, JsonConfigStore.DefaultPath());
            Assert.Equal(Path_, new JsonConfigStore().ConfigPath); // ohne expliziten Pfad → Override greift
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
