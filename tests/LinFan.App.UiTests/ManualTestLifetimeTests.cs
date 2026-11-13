// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Headless.XUnit;
using LinFan.App.Controllers;

namespace LinFan.App.UiTests;

/// <summary>
/// A fan test (Settings › Fans › Advanced, the airflow's position dialog) is meant to last only while it can be seen:
/// leaving the settings, switching the section or hiding the window to the tray ends it.
/// </summary>
public class ManualTestLifetimeTests
{
    [AvaloniaFact]
    public void FanTest_Ends_WhenTheSettingsAreLeft_TheSectionChanges_OrTheWindowHides()
    {
        var fake = new FakeLiveMonitor(UiTestHelpers.SampleSnapshot());
        // One read only: further ticks would let the test follow the fake daemon, which never confirms an override.
        var ctrl = UiTestHelpers.NewController(fake, pollInterval: TimeSpan.FromHours(1));
        try
        {
            UiTestHelpers.PumpUntil(() => ctrl.Editor.Fans.Count > 0);
            FanAssignRow fan = ctrl.Editor.Fans.Single(f => f.FanId == "hwmon0/pwm1");
            ctrl.OpenSettingsCommand.Execute(null);

            fan.Manual.IsActive = true;
            ctrl.NavIndex = 0; // back to the overview
            Assert.False(fan.Manual.IsActive);
            Assert.Single(fake.AutoCalls);

            ctrl.OpenSettingsCommand.Execute(null);
            ctrl.Settings.OpenSection(SettingsSection.Fans);
            fan.Manual.IsActive = true;
            ctrl.Settings.OpenSection(SettingsSection.Appearance);
            Assert.False(fan.Manual.IsActive);
            Assert.Equal(2, fake.AutoCalls.Count);

            fan.Manual.IsActive = true;
            ctrl.OnHiddenToTray();
            Assert.False(fan.Manual.IsActive);
            Assert.Equal(3, fake.AutoCalls.Count);
        }
        finally
        {
            ctrl.Dispose();
        }
    }
}
