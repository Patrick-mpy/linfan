// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using LinFan.Core.Models;
using LinFan.Core.Services;
using LinFan.Ipc.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinFan.Daemon.Tests;

/// <summary>
/// Config validation at the IPC boundary (security audit run-2, package B): whatever a client can make the
/// daemon persist, the next start must be able to load, and a config the daemon cannot use must not keep it
/// from starting.
/// </summary>
public partial class ControlLoopServiceTests
{
    // Mirrors the parts of LinFan.Ipc's (internal) IpcJson options that matter here: camelCase names, named float
    // literals ("NaN", "Infinity") and the default null handling, which lets a JSON null into ids declared non-null.
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static IpcCommand Wire(string line) => JsonSerializer.Deserialize<IpcCommand>(line, WireJson)!;

    /// <summary>Sends the lines one by one, each followed by two ticks, then stops the daemon.</summary>
    private static async Task SendAndStopAsync(FakeHardware hw, FakeConfigStore store, params string[] lines)
    {
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        foreach (string line in lines)
        {
            // Parse before counting: the first deserialization can take longer than several 15 ms ticks, and
            // those must not count as the two ticks that follow the command.
            IpcCommand command = Wire(line);
            int ticks = ipc.Broadcasts.Count;
            ipc.Emit(command);
            await WaitUntilAsync(() => ipc.Broadcasts.Count >= ticks + 2);
        }
        await service.StopAsync(cts.Token);
    }

    /// <summary>Starts a second daemon on what the first one persisted; it must load it, not set it aside.</summary>
    private static async Task<AppConfig> RestartAsync(FakeHardware hw, FakeConfigStore store)
    {
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 2);
        await service.StopAsync(cts.Token);

        Assert.Null(store.SetAsideConfig);
        return store.Stored;
    }

    [Fact]
    public async Task SaveConfig_WithNonFiniteNumbers_IsPersisted_AsFiniteValuesTheConfigFileCanHold()
    {
        // The wire allows "NaN"/"Infinity", the config file does not: a value that got through would make the save
        // throw after its pending slot was taken, and the change would be lost.
        FakeHardware hw = FanRig();
        var store = new FakeConfigStore();

        await SendAndStopAsync(hw, store,
            """{"command":"saveConfig","config":{"curves":[{"id":"c","name":"C","sourceSensorId":"t","hysteresisC":"Infinity","smoothingSeconds":"NaN","points":[{"temperatureC":"-Infinity","percent":50},{"temperatureC":60,"percent":"NaN"},{"temperatureC":70,"percent":80}]}],"fans":[],"sensors":[],"profiles":[],"activeProfileId":null}}""");

        CurveConfig curve = Assert.Single(store.Stored.Curves);
        Assert.True(double.IsFinite(curve.HysteresisC));
        Assert.True(double.IsFinite(curve.SmoothingSeconds));
        Assert.NotEmpty(curve.Points);
        Assert.All(curve.Points, p => Assert.True(double.IsFinite(p.TemperatureC) && double.IsFinite(p.Percent)));

        string path = Path.Combine(Path.GetTempPath(), $"linfan-nonfinite-{Guid.NewGuid():N}.json");
        try
        {
            new JsonConfigStore(path).Save(store.Stored); // the real store's options reject named literals
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveConfig_SensorWithNullId_IsNotPersisted_AndTheNextStartLoads()
    {
        FakeHardware hw = FanRig();
        var store = new FakeConfigStore();

        await SendAndStopAsync(hw, store,
            """{"command":"saveConfig","config":{"curves":[],"fans":[],"sensors":[{"id":null,"name":"x"}],"profiles":[],"activeProfileId":null}}""");

        AppConfig loaded = await RestartAsync(hw, store);
        Assert.DoesNotContain(loaded.Sensors, s => s.SensorId is null);
    }

    [Fact]
    public async Task SaveConfig_SensorGroups_AreSanitized_AndASaveFromAnOlderGuiKeepsThem()
    {
        FakeHardware hw = FanRig();
        var store = new FakeConfigStore();

        await SendAndStopAsync(hw, store,
            """{"command":"saveConfig","config":{"curves":[],"fans":[],"sensors":[],"profiles":[],"activeProfileId":null,"sensorGroups":[" CPU ","cpu","",null,"Leer"]}}""",
            """{"command":"saveConfig","config":{"curves":[],"fans":[],"sensors":[],"profiles":[],"activeProfileId":null}}""");

        AppConfig loaded = await RestartAsync(hw, store);
        Assert.Equal(new[] { "CPU", "Leer" }, loaded.SensorGroups);
    }

    [Fact]
    public async Task SaveConfig_DuplicateInAnInactiveProfile_PlusUnknownActiveProfile_TheNextStartLoads()
    {
        FakeHardware hw = FanRig();
        var store = new FakeConfigStore();

        await SendAndStopAsync(hw, store,
            """{"command":"saveConfig","config":{"curves":[],"fans":[],"sensors":[],"profiles":[{"id":"P0","name":"P0","curves":[],"assignments":[{"fanId":"f1","curveId":null},{"fanId":"f1","curveId":null}]},{"id":"P1","name":"P1","curves":[],"assignments":[]}],"activeProfileId":"P1"}}""",
            """{"command":"setActiveProfile","target":"ghost"}""");

        AppConfig loaded = await RestartAsync(hw, store);
        Assert.Equal("P1", loaded.ActiveProfileId);
        Assert.Single(loaded.Profiles.Single(p => p.Id == "P0").Assignments);
    }

    [Fact]
    public async Task SaveConfig_HugeCurveValuesInTheActiveProfile_AreBoundedBeforeTheyAreRelayed()
    {
        // The active profile's curves used to bypass the sanitizer and reach every GUI unchecked (audit #9).
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);

        ipc.Emit(Wire("""{"command":"saveConfig","config":{"curves":[],"fans":[{"fanId":"hwmon7/pwm1","name":"CPU","minPwm":0,"maxPwm":255,"assignedCurveId":"q"}],"sensors":[],"profiles":[{"id":"p","name":"Std","assignments":[{"fanId":"hwmon7/pwm1","curveId":"q"}],"curves":[{"id":"q","name":"Quiet","hysteresisC":2,"smoothingSeconds":3,"sourceSensorIds":["t"],"points":[{"temperatureC":35,"percent":25},{"temperatureC":85,"percent":100}]},{"id":"x","name":"x","hysteresisC":1e30,"smoothingSeconds":1e30,"points":[{"temperatureC":1e30,"percent":50}]}]}],"activeProfileId":"p","onboardingCompleted":true}}"""));
        await WaitUntilAsync(() => LastSnapshot(ipc)?.Config?.Curves.Any(c => c.Id == "x") == true);

        IpcConfig relayed = LastSnapshot(ipc)!.Config!;
        foreach (IpcCurve x in relayed.Profiles.Single().Curves.Where(c => c.Id == "x").Append(relayed.Curves.Single(c => c.Id == "x")))
        {
            Assert.Equal(ConfigSanitizer.MaxHysteresisC, x.HysteresisC);
            Assert.Equal(ConfigSanitizer.MaxSmoothingSeconds, x.SmoothingSeconds);
            Assert.Equal(ConfigSanitizer.MaxPointTempC, Assert.Single(x.Points).TemperatureC);
        }

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task OnCommand_SetActiveProfile_Unknown_IsRejected()
    {
        var hw = FanRig();
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with
            {
                Profiles = new[] { new Profile { Id = "pa", Name = "A" } },
                ActiveProfileId = "pa",
                OnboardingCompleted = true,
            },
            Exists = true,
        };

        await SendAndStopAsync(hw, store, """{"command":"setActiveProfile","target":"ghost"}""");

        Assert.DoesNotContain(store.Saves, s => s.ActiveProfileId == "ghost");
        Assert.Equal("pa", store.Stored.ActiveProfileId);
    }

    [Fact]
    public async Task OnCommand_ResetConfig_KeepsTheOperatorsLimits()
    {
        var hw = FanRig();
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with
            {
                FailSafeTempC = 72,
                PollIntervalMs = 2500,
                OnboardingCompleted = true,
                Fans = new[] { new FanConfig { FanId = "hwmon7/pwm1", Name = "CPU" } },
            },
            Exists = true,
        };

        await SendAndStopAsync(hw, store, """{"command":"resetConfig"}""");

        AppConfig reset = store.Saves.Last();
        Assert.Empty(reset.Fans); // the reset did run
        Assert.Equal(72, reset.FailSafeTempC);
        Assert.Equal(2500, reset.PollIntervalMs);
    }

    [Fact]
    public async Task Start_WithAConfigItCannotLoad_SetsItAside_AndStartsLikeAFreshInstall()
    {
        var hw = FanRig();
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with { OnboardingCompleted = true },
            Exists = true,
            LoadFailure = new InvalidOperationException("corrupt (simulated)"),
        };
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 2); // IPC up, the loop and its watchdog tick

        Assert.NotNull(store.SetAsideConfig);
        AppConfig fresh = Assert.Single(store.Saves);
        Assert.Empty(fresh.Fans);
        Assert.False(fresh.OnboardingCompleted); // no config any more: the GUI offers the setup assistant

        await service.StopAsync(cts.Token);
    }

    /// <summary>
    /// The GUI says why the curves are gone - where the file went, or that the next change overwrites it - until a
    /// change has been saved.
    /// </summary>
    [Theory]
    [InlineData(false, "SetAside", "(in-memory).invalid")]
    [InlineData(true, "NotLoaded", null)] // the move failed: the file is still in place
    public async Task Start_WithAConfigItCannotUse_ReportsIt_UntilAChangeIsSaved(bool setAsideFails, string problem,
                                                                               string? path)
    {
        var hw = FanRig();
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with { OnboardingCompleted = true },
            Exists = true,
            LoadFailure = new InvalidOperationException("corrupt (simulated)"),
            ThrowOnSetAside = setAsideFails,
        };
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => LastSnapshot(ipc) is not null);
        Assert.Equal((problem, path), (LastSnapshot(ipc)!.ConfigProblem, LastSnapshot(ipc)!.ConfigSetAsidePath));

        ipc.Emit(new IpcCommand(IpcCommand.SaveConfig, Config: IpcConfig.Empty with { OnboardingCompleted = true }));
        await WaitUntilAsync(() => LastSnapshot(ipc) is { ConfigProblem: null, ConfigSetAsidePath: null });

        await service.StopAsync(cts.Token);
    }

    /// <summary>A reload that reads the file back puts it in use again - the report from the start no longer holds.</summary>
    [Fact]
    public async Task Start_WithAnUnreadableConfig_ReportIsGone_OnceAReloadReadsTheFile()
    {
        var hw = FanRig();
        var store = new FakeConfigStore { Exists = true, LoadFailure = new IOException("EACCES (simulated)") };
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => LastSnapshot(ipc) is { ConfigProblem: "NotLoaded" });

        store.LoadFailure = null; // the operator fixed the permissions
        ipc.Emit(new IpcCommand(IpcCommand.Reload));

        await WaitUntilAsync(() => LastSnapshot(ipc) is { ConfigProblem: null });
        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Start_WithAnUnreadableConfig_ReportsThatTheFileWasNotLoaded()
    {
        var hw = FanRig();
        var store = new FakeConfigStore { Exists = true, LoadFailure = new IOException("EACCES (simulated)") };
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => LastSnapshot(ipc) is not null);

        Assert.Equal("NotLoaded", LastSnapshot(ipc)!.ConfigProblem);
        Assert.Null(store.SetAsideConfig); // left in place
        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Start_WithAGoodConfig_ReportsNoProblem()
    {
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(FanRig(), new FakeConfigStore { Exists = true }, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => LastSnapshot(ipc) is not null);

        Assert.Null(LastSnapshot(ipc)!.ConfigProblem);
        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Start_WithSensorGroupsNull_LoadsTheConfig_WithNoGroups()
    {
        // Unlike a null in the other lists: a hand-edited null in this display-only list must not cost the curves.
        var hw = FanRig();
        var curve = new CurveConfig
        {
            Id = "c",
            Name = "Quiet",
            SourceSensorIds = new[] { "t" },
            Points = new[] { new CurvePoint(30, 20), new CurvePoint(80, 100) },
        };
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with { Curves = new[] { curve }, SensorGroups = null!, OnboardingCompleted = true },
            Exists = true,
        };
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 2);

        Assert.Null(store.SetAsideConfig);
        IpcConfig relayed = LastSnapshot(ipc)!.Config!;
        Assert.Equal("c", Assert.Single(relayed.Curves).Id);
        Assert.NotNull(relayed.SensorGroups);
        Assert.Empty(relayed.SensorGroups!);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Start_WithAConfigThatParsesButCannotBeUsed_KeepsTheOperatorsLimits()
    {
        // A null list (hand edit) breaks the processing after the file parsed: the fallback must not move a
        // stricter over-temperature limit back to the default and persist that.
        var hw = FanRig();
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with { FailSafeTempC = 72, PollIntervalMs = 2500, Fans = null! },
            Exists = true,
        };
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 2);

        Assert.NotNull(store.SetAsideConfig);
        AppConfig fresh = Assert.Single(store.Saves);
        Assert.Equal(72, fresh.FailSafeTempC);
        Assert.Equal(2500, fresh.PollIntervalMs);

        await service.StopAsync(cts.Token);
    }

    [Theory]
    [InlineData(false, FanMode.Auto)]
    [InlineData(true, FanMode.Manual)]
    public async Task Start_ReturnsEveryFanToHardwareAuto_ExceptInDryRun(bool dryRun, FanMode expected)
    {
        // A predecessor killed with a fan in manual (SIGKILL past the stop timeout) left it at that PWM; a config
        // that does not regulate the fan would never touch it again. A dry-run instance (no root, a dev build
        // next to the live daemon) must not reach for the fans at all.
        var hw = FanRig();
        hw.ModeLog["hwmon7/pwm1"] = FanMode.Manual;
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        var service = new ControlLoopService(
            hw, hw, store, ipc, NullLogger<ControlLoopService>.Instance, FastTick, dryRunOverride: dryRun);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 2); // no fan is configured: the loop itself writes nothing

        Assert.Equal(expected, hw.ModeLog["hwmon7/pwm1"]);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Start_WhenTheConfigCannotBeRead_LeavesTheFileAlone()
    {
        // An I/O error says nothing about the content: setting the file aside would replace a possibly good
        // config with the defaults.
        var hw = FanRig();
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with { FailSafeTempC = 72, OnboardingCompleted = true },
            Exists = true,
            LoadFailure = new IOException("EIO (simulated)"),
        };
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 2);

        Assert.Null(store.SetAsideConfig);
        Assert.Empty(store.Saves);
        Assert.Equal(72, store.Stored.FailSafeTempC);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Start_WhenTheUnusableConfigCannotBeSetAside_StillServes_WithoutOverwritingIt()
    {
        var hw = FanRig();
        var store = new FakeConfigStore
        {
            Exists = true,
            LoadFailure = new InvalidOperationException("corrupt (simulated)"),
            ThrowOnSetAside = true,
        };
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 2);

        Assert.Empty(store.Saves);

        await service.StopAsync(cts.Token);
    }

    /// <summary>
    /// Holds a fan in manual, sends an import the daemon does not take, and checks that the fans were left alone:
    /// no return to hardware auto, the manual override still in place, nothing persisted.
    /// </summary>
    private static async Task AssertImportLeavesTheFansAloneAsync(IpcCommand replace, FakeConfigStore? store = null)
    {
        var hw = FanRig();
        store ??= new FakeConfigStore();
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        ipc.Emit(new IpcCommand(IpcCommand.SetManualPwm, Target: "hwmon7/pwm1", Value: 128));
        await WaitUntilAsync(() => FanIsManual(ipc, "hwmon7/pwm1"));
        int restores = hw.RestoreCount;
        int saves = store.Saves.Count;

        int ticks = ipc.Broadcasts.Count;
        ipc.Emit(replace);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= ticks + 2);

        Assert.Equal(restores, hw.RestoreCount);
        Assert.True(FanIsManual(ipc, "hwmon7/pwm1"));
        Assert.Equal(saves, store.Saves.Count);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public Task OnCommand_ReplaceConfig_ThatFailsToConvert_LeavesTheFansAlone() =>
        // A null list entry makes ConfigMapper.Replace throw: the command is dropped as a whole.
        AssertImportLeavesTheFansAloneAsync(Wire(
            """{"command":"replaceConfig","config":{"curves":[],"fans":[null],"sensors":[],"profiles":[],"activeProfileId":null}}"""));

    [Fact]
    public Task OnCommand_ReplaceConfig_PastTheLimits_LeavesTheFansAlone() =>
        AssertImportLeavesTheFansAloneAsync(new IpcCommand(IpcCommand.ReplaceConfig, Config: IpcConfig.Empty with
        {
            Profiles = Enumerable.Range(0, ConfigLimits.MaxProfiles + 1)
                .Select(i => new IpcProfile($"q{i}", "", Array.Empty<IpcProfileAssignment>(), Array.Empty<IpcCurve>()))
                .ToArray(),
        }));

    [Fact]
    public Task OnCommand_ReplaceConfig_ThatFailsToSave_LeavesTheFansAlone()
    {
        // Saving can fail (I/O, the size cap); the fans used to bounce to auto before the save was even tried.
        var store = new FakeConfigStore { ThrowOnSave = true };
        return AssertImportLeavesTheFansAloneAsync(
            new IpcCommand(IpcCommand.ReplaceConfig, Config: IpcWith(new IpcFanAssignment("hwmon7/pwm1", "CPU", 0, 255, null))),
            store);
    }

    [Fact]
    public async Task SaveConfig_EmptyProfilesThatWouldMultiplyTheCurves_AreRejectedAsAWhole()
    {
        // Audit run-2 #10: one curve and a thousand empty profiles - 63 KB on the wire, 25 MB once each profile
        // got its copy of the curve list.
        var curve = new IpcCurve("k1", "K1", "t", 1.5,
            Enumerable.Range(0, 250).Select(i => new IpcCurvePoint(20 + 0.3 * i, 10 + 90.0 * i / 249)).ToArray(),
            new[] { "t" });
        var hw = FanRig();
        var store = new FakeConfigStore();
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();
        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        store.ClearSaves();

        var command = new IpcCommand(IpcCommand.SaveConfig, Config: IpcConfig.Empty with
        {
            Curves = new[] { curve },
            Profiles = Enumerable.Range(0, 1000)
                .Select(i => new IpcProfile($"q{i}", "", Array.Empty<IpcProfileAssignment>(), Array.Empty<IpcCurve>()))
                .ToArray(),
            ActiveProfileId = "q0",
        });
        int ticks = ipc.Broadcasts.Count;
        ipc.Emit(command);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= ticks + 2);

        Assert.Empty(store.Saves);
        Assert.Single(LastSnapshot(ipc)!.Config!.Profiles);

        await service.StopAsync(cts.Token);
    }

    [Fact]
    public async Task SaveConfig_TooManyFans_IsRejectedAsAWhole()
    {
        // Audit run-2 #5: every configured fan costs work in each tick, and the tick carries the watchdog.
        var hw = FanRig();
        var store = new FakeConfigStore();

        await SendAndStopAsync(hw, store, JsonSerializer.Serialize(
            new IpcCommand(IpcCommand.SaveConfig, Config: IpcWith(Enumerable.Range(0, ConfigLimits.MaxFans + 1)
                .Select(i => new IpcFanAssignment($"u{i}", "", 0, 255, null)).ToArray())),
            WireJson));

        Assert.DoesNotContain(store.Saves, s => s.Fans.Any(f => f.FanId.StartsWith('u')));
    }

    [Fact]
    public async Task OnCommand_SetFanTachometer_ForAFanTheBackendDoesNotKnow_IsRejected()
    {
        // Each accepted id became a persisted fan entry: 20,000 such lines meant 20,000 fans in every tick.
        var hw = FanRig();
        var store = new FakeConfigStore();

        await SendAndStopAsync(hw, store,
            """{"command":"setFanTachometer","target":"q1","rpmSource":"hwmon7/fan1"}""",
            """{"command":"setFanTachometer","target":"hwmon7/pwm1","rpmSource":"no/such/sensor"}""");

        Assert.DoesNotContain(store.Saves, s => s.Fans.Any(f => f.FanId == "q1" || f.RpmSource == "no/such/sensor"));
    }

    [Fact]
    public async Task Start_WithAConfigPastTheLimits_SetsItAside_AndKeepsTheOperatorsLimits()
    {
        // A file an older version let a client inflate: not used, but its over-temperature limit carries over.
        var hw = FanRig();
        var store = new FakeConfigStore
        {
            Stored = AppConfig.Empty with
            {
                FailSafeTempC = 72,
                Profiles = Enumerable.Range(0, 1000).Select(i => new Profile { Id = $"q{i}" }).ToArray(),
            },
            Exists = true,
        };
        var ipc = new FakeIpcServer();
        ControlLoopService service = NewService(hw, store, ipc);
        using var cts = new CancellationTokenSource();

        await StartAndWaitReadyAsync(service, ipc, cts.Token);
        await WaitUntilAsync(() => ipc.Broadcasts.Count >= 2);

        Assert.NotNull(store.SetAsideConfig);
        AppConfig fresh = Assert.Single(store.Saves);
        Assert.Single(fresh.Profiles);
        Assert.Equal(72, fresh.FailSafeTempC);

        await service.StopAsync(cts.Token);
    }
}
