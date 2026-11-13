// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LinFan.Core.Services;

namespace LinFan.App.Controllers;

// Sensor groups of the devices step: shown grouped there, persisted by Finish.
public partial class OnboardingController
{
    /// <summary>
    /// The temperature sensors of the devices step, grouped. A sensor that already has a group (the wizard runs
    /// again on an existing config) stays in it; every other one goes to the group <see cref="SensorClassifier"/>
    /// suggests. The rows are the <see cref="TemperatureSensors"/> instances; Finish persists the suggestions.
    /// </summary>
    public ObservableCollection<OnboardingSensorGroup> TemperatureSensorGroups { get; } = new();

    /// <summary>True if at least one sensor had no group and shows under a suggested one - only then the hint
    /// that groups were suggested is true.</summary>
    [ObservableProperty] private bool _hasSuggestedGroups;

    /// <summary>Rebuilt on populate and on a language change - the suggested names follow the UI language.</summary>
    private void RebuildSensorGroups(IReadOnlyList<string> configuredGroups)
    {
        SensorGroupPlan plan = PlanSensorGroups(configuredGroups);
        TemperatureSensorGroups.Clear();
        foreach (string name in plan.Names)
        {
            List<SensorOption> members = TemperatureSensors
                .Where(s => string.Equals(plan.GroupById[s.Id], name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            // The configured list may name groups no sensor of this machine is in - nothing to show for them.
            if (members.Count > 0)
                TemperatureSensorGroups.Add(new OnboardingSensorGroup(name, members));
        }
        HasSuggestedGroups = plan.HasSuggestions;
    }

    private void RefreshSensorGroupCount(SensorOption sensor) =>
        TemperatureSensorGroups.FirstOrDefault(g => g.Sensors.Contains(sensor))?.RefreshCount();

    /// <summary>The group of every temperature sensor and the group list Finish persists.</summary>
    private SensorGroupPlan PlanSensorGroups(IReadOnlyList<string> configuredGroups) =>
        SensorGroupOrder.Plan(configuredGroups, TemperatureSensors.Select(s => (s.Id, (string?)s.Group)));
}
