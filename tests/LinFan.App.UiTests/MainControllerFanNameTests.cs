// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Headless.XUnit;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.App.UiTests;

/// <summary>
/// A fan without an own name is „Lüfter N" everywhere - also in the overview, which takes its names from the daemon's
/// snapshot (the hardware label for such a fan) until a save writes the name down.
/// </summary>
public class MainControllerFanNameTests
{
    [AvaloniaFact]
    public void TheOverview_CallsAFanWithoutAnOwnName_AsTheSettingsDo()
    {
        MonitorSnapshot sample = UiTestHelpers.SampleSnapshot();
        MonitorSnapshot unnamed = sample with
        {
            Fans = sample.Fans.Select(f => f with { Name = "nct6798 pwm1" }).ToList(),
            Config = sample.Config with { Fans = sample.Config.Fans.Select(f => f with { Name = "" }).ToList() },
        };
        var fake = new FakeLiveMonitor(unnamed);
        var ctrl = UiTestHelpers.NewController(fake, pollInterval: TimeSpan.FromMilliseconds(10));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.IsReady && ctrl.Fans.Count > 0);

            Assert.Equal("Lüfter 1", Assert.Single(ctrl.Fans).Name);
            Assert.Equal("Lüfter 1", Assert.Single(ctrl.Editor.Fans).DisplayName);
        }
        finally { ctrl.Dispose(); }
    }
}
