// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

/// <summary>
/// Reine Pfad-Auflösung von <see cref="JsonConfigStore.ResolveDefaultPath"/> - deterministisch auf jedem
/// Host prüfbar (keine echten Umgebungsordner). Sichert insbesondere den Windows-Maschinenpfad
/// (<c>%ProgramData%</c>) gegen den per-User-<c>%AppData%</c>, der für einen SYSTEM-Dienst unsichtbar wäre.
/// </summary>
public sealed class JsonConfigStorePathTests
{
    private const string AppData = "/u/appdata";
    private const string CommonAppData = "/m/programdata";
    private const string UserProfile = "/u/home";

    [Fact]
    public void Override_Wins_OnEveryPlatform()
    {
        Assert.Equal("/custom/x.json",
            JsonConfigStore.ResolveDefaultPath("/custom/x.json", AppData, CommonAppData, UserProfile, windows: true));
        Assert.Equal("/custom/x.json",
            JsonConfigStore.ResolveDefaultPath("/custom/x.json", AppData, CommonAppData, UserProfile, windows: false));
    }

    [Fact]
    public void Windows_UsesMachineWideCommonAppData()
    {
        string path = JsonConfigStore.ResolveDefaultPath(null, AppData, CommonAppData, UserProfile, windows: true);
        Assert.Equal(Path.Combine(CommonAppData, "linfan", "config.json"), path);
    }

    [Fact]
    public void NonWindows_UsesPerUserAppData()
    {
        string path = JsonConfigStore.ResolveDefaultPath(null, AppData, CommonAppData, UserProfile, windows: false);
        Assert.Equal(Path.Combine(AppData, "linfan", "config.json"), path);
    }

    [Fact]
    public void EmptyBaseDir_FallsBackToUserProfileConfig()
    {
        string path = JsonConfigStore.ResolveDefaultPath(null, appData: "", CommonAppData, UserProfile, windows: false);
        Assert.Equal(Path.Combine(UserProfile, ".config", "linfan", "config.json"), path);
    }
}
