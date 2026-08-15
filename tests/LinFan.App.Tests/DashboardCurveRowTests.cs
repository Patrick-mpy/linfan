// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using LinFan.App.Controllers;
using LinFan.Core.Models;
using LinFan.Core.Services;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Tests für <see cref="DashboardCurveRow"/> (Aktive-Kurven-Panel): Quell-Kurzbeschreibung und der
/// An/Aus-Toggle, der die Schalt-Aktion an den Controller delegiert.
/// </summary>
public sealed class DashboardCurveRowTests
{
    private static CurveEditRow Curve(bool enabled, params string[] sourceIds)
    {
        var sensors = new ObservableCollection<SensorOption>();
        foreach (string id in sourceIds)
            sensors.Add(new SensorOption(id, id, visible: true, group: null, unit: "°C", availableGroups: new()));
        return new CurveEditRow("c1", "Quiet", sourceIds, SensorAggregation.Max, 2m, sensors)
        {
            Enabled = enabled,
        };
    }

    [Fact]
    public void Toggling_Enabled_InvokesCallback()
    {
        bool? toggled = null;
        var row = new DashboardCurveRow(Curve(enabled: true), Array.Empty<FanRow>(), e => toggled = e, () => { });

        Assert.True(row.Enabled);
        row.Enabled = false;

        Assert.False(toggled);
    }

    [Fact]
    public void EditCommand_InvokesCallback()
    {
        bool edited = false;
        var row = new DashboardCurveRow(Curve(enabled: true), Array.Empty<FanRow>(), _ => { }, () => edited = true);

        row.EditCommand.Execute(null);

        Assert.True(edited);
    }

    [Theory]
    [InlineData(new string[0], "-")]
    [InlineData(new[] { "t1" }, "t1")]
    [InlineData(new[] { "t1", "t2" }, "2 Sensoren")]
    public void SourceSummary_ReflectsSourceCount(string[] sources, string expected)
    {
        var row = new DashboardCurveRow(Curve(enabled: true, sources), Array.Empty<FanRow>(), _ => { }, () => { });
        Assert.Equal(expected, row.SourceSummary);
    }
}
