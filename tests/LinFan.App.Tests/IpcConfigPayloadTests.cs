// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;
using LinFan.Core.Models;
using LinFan.Ipc.Messages;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// What the GUI actually puts on the wire for a config. Calibration and tachometer assignment are
/// daemon-owned: a plain save (merge) must not carry them, a full replace (import/restore) must — otherwise
/// restoring a backup silently drops what the backup holds. The daemon side of this was covered, this
/// mapping was not, which is exactly where the field went missing.
/// </summary>
public sealed class IpcConfigPayloadTests
{
    private static AppConfig ConfigWithFan() => new()
    {
        Fans = new[]
        {
            new FanConfig
            {
                FanId = "/lpc/nct6797d/0/control/2",
                Name = "Back",
                RpmSource = "/lpc/nct6797d/0/fan/2",
                Calibration = new FanCalibration { StartPwm = 64, MinRpm = 300, MaxRpm = 1500 },
            },
        },
    };

    [Fact]
    public void Replace_CarriesRpmSourceAndCalibration()
    {
        IpcFanAssignment fan = Assert.Single(
            IpcLiveMonitor.ToIpcConfig(ConfigWithFan(), withDaemonOwned: true).Fans);

        Assert.Equal("/lpc/nct6797d/0/fan/2", fan.RpmSource);
        Assert.NotNull(fan.Calibration);
        Assert.Equal(64, fan.Calibration!.StartPwm);
    }

    [Fact]
    public void Save_OmitsRpmSourceAndCalibration()
    {
        IpcFanAssignment fan = Assert.Single(IpcLiveMonitor.ToIpcConfig(ConfigWithFan()).Fans);

        // The daemon keeps its own values on a merge — sending them would let a stale GUI snapshot win.
        Assert.Null(fan.RpmSource);
        Assert.Null(fan.Calibration);
    }
}
