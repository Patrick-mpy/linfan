// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.App.Tests;

public sealed class VersionControllerTests
{
    private const string App = "0.5.0+a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    private static MonitorSnapshot Connected(string? daemonVersion) =>
        new("x", Array.Empty<SensorReading>(), Array.Empty<FanReading>(), AppConfig.Empty, Connected: true,
            DaemonVersion: daemonVersion);

    private static MonitorSnapshot Offline() => MonitorSnapshot.Unavailable("x", ConnectionProblem.Unreachable);

    [Theory]
    [InlineData(App, "0.5.0 · a1b2c3d")]
    [InlineData("0.5.0-dev+716f3460570e", "0.5.0-dev · 716f346")]
    [InlineData("0.4.0+abc", "0.4.0 · abc")]
    [InlineData("0.4.0+", "0.4.0")]
    [InlineData("0.4.0", "0.4.0")]
    public void Short_CutsTheCommitToItsShortForm(string informational, string expected) =>
        Assert.Equal(expected, VersionController.Short(informational));

    [Fact]
    public void AppLabel_NamesTheBuild()
    {
        using var version = new VersionController(App);

        Assert.Equal("LinFan 0.5.0 · a1b2c3d", version.AppLabel);
    }

    [Fact]
    public void SameBuild_ShowsNoServiceLine()
    {
        using var version = new VersionController(App);

        version.Apply(Connected(App));

        Assert.False(version.ShowServiceMismatch);
        Assert.Equal("", version.ServiceMismatchText);
    }

    [Theory]
    [InlineData("0.4.0+b2c3d4e5f6", "Dienst 0.4.0 · b2c3d4e")]
    [InlineData("0.5.0+ffffffffff", "Dienst 0.5.0 · fffffff")] // same version, another commit: another build
    [InlineData(null, "Dienst: ältere Version")]                // a daemon that predates the field
    public void AnotherBuild_ShowsTheServiceLine(string? daemon, string expected)
    {
        using var version = new VersionController(App);

        version.Apply(Connected(daemon));

        Assert.True(version.ShowServiceMismatch);
        Assert.Equal(expected, version.ServiceMismatchText);
    }

    [Fact]
    public void Offline_HidesTheServiceLine_ItsVersionIsUnknown()
    {
        using var version = new VersionController(App);
        version.Apply(Connected("0.4.0+b2c3d4e"));
        var changed = new List<string?>();
        version.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        version.Apply(Offline());

        Assert.False(version.ShowServiceMismatch);
        Assert.Equal("", version.ServiceMismatchText);
        Assert.Contains(nameof(VersionController.ShowServiceMismatch), changed);
    }

    [Fact]
    public async Task OpenRepository_OpensTheGitHubRepository()
    {
        var opened = new List<string>();
        using var version = new VersionController(App, url =>
        {
            opened.Add(url);
            return Task.FromResult(true);
        });

        await version.OpenRepositoryCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "https://github.com/Patrick-mpy/linfan" }, opened);
    }

    [Fact]
    public void AnUnchangedSnapshot_RaisesNothing()
    {
        using var version = new VersionController(App);
        version.Apply(Connected("0.4.0+b2c3d4e"));
        var changed = new List<string?>();
        version.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        version.Apply(Connected("0.4.0+b2c3d4e"));

        Assert.Empty(changed); // a snapshot arrives every second
    }
}
