// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.App.Localization;

namespace LinFan.App.Controllers;

/// <summary>
/// A sensor group in the wizard's devices step: the group its sensors already have, or the one the classifier
/// suggests for them. The rows are the controller's <see cref="SensorOption"/> instances, so ticking a box here is
/// the same visibility the rest of the wizard reads.
/// </summary>
public sealed class OnboardingSensorGroup : ObservableObject
{
    public string Name { get; }
    public IReadOnlyList<SensorOption> Sensors { get; }

    /// <summary>"3 sensors", or "2 of 3 visible" once one is deselected.</summary>
    public string CountText
    {
        get
        {
            int visible = Sensors.Count(s => s.Visible);
            if (visible < Sensors.Count)
                return Localizer.Instance.Format("OnboardingCtrl.SensorGroupVisible", visible, Sensors.Count);
            return Sensors.Count == 1
                ? Localizer.Instance["OnboardingCtrl.SensorGroupCountOne"]
                : Localizer.Instance.Format("OnboardingCtrl.SensorGroupCountMany", Sensors.Count);
        }
    }

    public OnboardingSensorGroup(string name, IReadOnlyList<SensorOption> sensors)
    {
        Name = name;
        Sensors = sensors;
    }

    /// <summary>Re-announces <see cref="CountText"/> after a member's visibility changed.</summary>
    public void RefreshCount() => OnPropertyChanged(nameof(CountText));
}
