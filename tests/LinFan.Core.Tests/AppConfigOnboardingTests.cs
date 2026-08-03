// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Core.Services;

namespace LinFan.Core.Tests;

/// <summary>Tests für das additiv hinzugefügte <see cref="AppConfig.OnboardingCompleted"/>-Feld.</summary>
public sealed class AppConfigOnboardingTests
{
    [Fact]
    public void Empty_OnboardingCompleted_IsNull()
    {
        AppConfig config = AppConfig.Empty;

        Assert.Null(config.OnboardingCompleted);
    }

    [Fact]
    public void OnboardingCompleted_CanBeSetToFalse()
    {
        AppConfig config = AppConfig.Empty with { OnboardingCompleted = false };

        Assert.False(config.OnboardingCompleted);
    }

    [Fact]
    public void OnboardingCompleted_CanBeSetToTrue()
    {
        AppConfig config = AppConfig.Empty with { OnboardingCompleted = true };

        Assert.True(config.OnboardingCompleted);
    }

    [Fact]
    public void OnboardingCompleted_RoundTripsViaJson()
    {
        var store = new JsonConfigStore(Path.Combine(
            Path.GetTempPath(), $"linfan-onboarding-test-{Guid.NewGuid():N}", "config.json"));

        try
        {
            AppConfig saved = AppConfig.Empty with { OnboardingCompleted = true };
            store.Save(saved);
            AppConfig loaded = store.Load();

            Assert.True(loaded.OnboardingCompleted);
        }
        finally
        {
            string? dir = Path.GetDirectoryName(store.ConfigPath);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OnboardingCompleted_NullRoundTripsViaJson()
    {
        var store = new JsonConfigStore(Path.Combine(
            Path.GetTempPath(), $"linfan-onboarding-test-{Guid.NewGuid():N}", "config.json"));

        try
        {
            AppConfig saved = AppConfig.Empty with { OnboardingCompleted = null };
            store.Save(saved);
            AppConfig loaded = store.Load();

            Assert.Null(loaded.OnboardingCompleted);
        }
        finally
        {
            string? dir = Path.GetDirectoryName(store.ConfigPath);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
