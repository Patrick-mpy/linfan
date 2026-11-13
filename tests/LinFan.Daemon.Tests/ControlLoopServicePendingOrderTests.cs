// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Models;
using LinFan.Daemon;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// A full config (SaveConfig) and a profile switch or curve toggle that wait for the same tick: the command that
/// arrived last wins, in either order. The daemon applies one pending change per tick, so a fixed order of the pending
/// slots alone would let an older command overwrite a newer one.
/// </summary>
public partial class ControlLoopServiceTests
{
    [Fact]
    public async Task SaveThenSwitch_InOneTick_TheSwitchWins()
    {
        var hw = FanRig();
        FakeConfigStore store = TwoProfileStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        await EmitInOneTickAsync(hw, ipc,
            new IpcCommand(IpcCommand.SaveConfig, ConfigMapper.ToIpc(store.Stored)), // "pa" active
            new IpcCommand(IpcCommand.SetActiveProfile, Target: "pb"));

        IpcConfig live = await SettledConfigAsync(ipc, c => c.ActiveProfileId == "pb");
        Assert.Equal("pb", live.ActiveProfileId);
        Assert.Equal("cb", live.Fans.Single(f => f.FanId == "hwmon7/pwm1").AssignedCurveId);
        Assert.Equal("pb", store.Stored.ActiveProfileId);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task SwitchThenSave_InOneTick_TheSavesProfileWins_AndTheSwitchNeverRuns()
    {
        var hw = FanRig();
        FakeConfigStore store = TwoProfileStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.ClearSaves();

        await EmitInOneTickAsync(hw, ipc,
            new IpcCommand(IpcCommand.SetActiveProfile, Target: "pb"),
            new IpcCommand(IpcCommand.SaveConfig, Renamed(ConfigMapper.ToIpc(store.Stored)))); // "pa" active

        IpcConfig live = await SettledConfigAsync(ipc, IsRenamed);
        Assert.Equal("pa", live.ActiveProfileId);
        Assert.Equal("ca", live.Fans.Single(f => f.FanId == "hwmon7/pwm1").AssignedCurveId);
        Assert.DoesNotContain(store.Saves, s => s.ActiveProfileId == "pb"); // superseded, not applied for a tick

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task SaveAddingAProfile_ThenSwitchToIt_InOneTick_IsNotRejectedAsUnknown()
    {
        var hw = FanRig();
        FakeConfigStore store = TwoProfileStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        var curveC = new CurveConfig { Id = "cc", Name = "C", SourceSensorIds = new[] { "t" }, Points = new[] { new CurvePoint(40, 55) } };
        var profileC = new Profile
        {
            Id = "pc",
            Name = "C",
            Curves = new[] { curveC },
            Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", "cc") },
        };
        AppConfig withC = store.Stored with { Profiles = store.Stored.Profiles.Append(profileC).ToArray() };

        await EmitInOneTickAsync(hw, ipc,
            new IpcCommand(IpcCommand.SaveConfig, ConfigMapper.ToIpc(withC)),
            new IpcCommand(IpcCommand.SetActiveProfile, Target: "pc"));

        IpcConfig live = await SettledConfigAsync(ipc, c => c.ActiveProfileId == "pc");
        Assert.Equal("pc", live.ActiveProfileId);
        Assert.Equal("cc", live.Fans.Single(f => f.FanId == "hwmon7/pwm1").AssignedCurveId);
        Assert.Equal("pc", store.Stored.ActiveProfileId);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task SaveThenCurveToggle_InOneTick_TheToggleWins()
    {
        var hw = FanRig();
        FakeConfigStore store = TwoProfileStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        await EmitInOneTickAsync(hw, ipc,
            new IpcCommand(IpcCommand.SaveConfig, Renamed(ConfigMapper.ToIpc(store.Stored))), // "ca" on
            new IpcCommand(IpcCommand.SetCurveEnabled, Target: "ca", Value: 0));

        IpcConfig live = await SettledConfigAsync(ipc, c => IsRenamed(c) && !CurveAEnabled(c));
        Assert.True(IsRenamed(live));                 // the save went through
        Assert.False(CurveAEnabled(live));            // and the later toggle on top of it
        Assert.False(live.Profiles.Single(p => p.Id == "pa").Curves.Single(c => c.Id == "ca").Enabled);
        Assert.False(store.Stored.Curves.Single(c => c.Id == "ca").Enabled);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task CurveToggleThenSave_InOneTick_TheSavesFlagWins_AndTheToggleNeverRuns()
    {
        var hw = FanRig();
        FakeConfigStore store = TwoProfileStore();
        var ipc = new FakeIpcServer();
        var service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.ClearSaves();

        await EmitInOneTickAsync(hw, ipc,
            new IpcCommand(IpcCommand.SetCurveEnabled, Target: "ca", Value: 0),
            new IpcCommand(IpcCommand.SaveConfig, Renamed(ConfigMapper.ToIpc(store.Stored)))); // "ca" on

        IpcConfig live = await SettledConfigAsync(ipc, IsRenamed);
        Assert.True(CurveAEnabled(live));
        Assert.DoesNotContain(store.Saves, s => s.Curves.Any(c => c.Id == "ca" && !c.Enabled));

        await service.StopAsync(cts.Token);
    }

    /// <summary>Profiles "pa" (active, curve "ca") and "pb" (curve "cb"), each driving the rig's fan.</summary>
    private static FakeConfigStore TwoProfileStore()
    {
        var curveA = new CurveConfig { Id = "ca", Name = "A", SourceSensorIds = new[] { "t" }, Points = new[] { new CurvePoint(40, 30) } };
        var curveB = new CurveConfig { Id = "cb", Name = "B", SourceSensorIds = new[] { "t" }, Points = new[] { new CurvePoint(40, 80) } };
        return new FakeConfigStore
        {
            Exists = true,
            Stored = AppConfig.Empty with
            {
                Curves = new[] { curveA },
                Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "F", AssignedCurveId = "ca" } },
                Profiles = new[]
                {
                    new Profile
                    {
                        Id = "pa",
                        Name = "A",
                        Curves = new[] { curveA },
                        Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", "ca") },
                    },
                    new Profile
                    {
                        Id = "pb",
                        Name = "B",
                        Curves = new[] { curveB },
                        Assignments = new[] { new ProfileAssignment("hwmon7/pwm1", "cb") },
                    },
                },
                ActiveProfileId = "pa",
                OnboardingCompleted = true,
            },
        };
    }

    /// <summary>A visible edit, so a test can tell when the save itself has been applied.</summary>
    private static IpcConfig Renamed(IpcConfig config) =>
        config with { Fans = config.Fans.Select(f => f with { Name = "Renamed" }).ToArray() };

    private static bool IsRenamed(IpcConfig config) => config.Fans.Any(f => f.Name == "Renamed");

    private static bool CurveAEnabled(IpcConfig config) => config.Curves.Single(c => c.Id == "ca").Enabled;

    /// <summary>
    /// Emits <paramref name="commands"/> while the loop thread is held in a sensor read (after this tick's config
    /// step), so all of them are pending together when the next tick starts.
    /// </summary>
    private static async Task EmitInOneTickAsync(FakeHardware hw, FakeIpcServer ipc, params IpcCommand[] commands)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        hw.OnRead = () =>
        {
            held.TrySetResult();
            release.Wait();
        };
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (IpcCommand command in commands)
                ipc.Emit(command);
        }
        finally
        {
            hw.OnRead = null;
            release.Set();
        }
    }

    /// <summary>
    /// The live config once <paramref name="reached"/> holds and a few more ticks have passed - long enough for a
    /// change still pending behind it to show up.
    /// </summary>
    private static async Task<IpcConfig> SettledConfigAsync(FakeIpcServer ipc, Func<IpcConfig, bool> reached)
    {
        await WaitUntilAsync(() => LastSnapshot(ipc)?.Config is { } c && reached(c));
        int seen = ipc.Broadcasts.Count;
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= seen + 3);
        return LastSnapshot(ipc)!.Config!;
    }
}
