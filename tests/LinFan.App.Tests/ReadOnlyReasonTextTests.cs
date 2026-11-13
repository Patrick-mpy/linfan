// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using LinFan.App.Controllers;
using LinFan.App.Services;
using LinFan.Core.Models;

namespace LinFan.App.Tests;

/// <summary>
/// <see cref="ReadOnlyReasonText"/> and the read-only notes built from it: a read-only fan says why where the daemon
/// knows it, and keeps the plain note where it does not.
/// </summary>
public sealed class ReadOnlyReasonTextTests
{
    [Fact]
    public void EveryReason_HasATextOfItsOwn()
    {
        string?[] texts = Enum.GetValues<FanReadOnlyReason>().Select(r => ReadOnlyReasonText.Of(r)).ToArray();

        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(texts.Length, texts.Distinct().Count());
        Assert.Null(ReadOnlyReasonText.Of(null));
    }

    [Fact]
    public void DriverLocked_NamesTheFix()
    {
        Assert.Contains("fan_control=1", ReadOnlyReasonText.Of(FanReadOnlyReason.DriverLocked));
    }

    [Fact]
    public void Note_AppendsAKnownReason_AndKeepsThePlainNoteOtherwise()
    {
        Assert.Equal("Nur Anzeige.", ReadOnlyReasonText.Note("Nur Anzeige.", null));
        Assert.Equal($"Nur Anzeige. {ReadOnlyReasonText.Of(FanReadOnlyReason.NoControlInterface)}",
            ReadOnlyReasonText.Note("Nur Anzeige.", FanReadOnlyReason.NoControlInterface));
    }

    [Fact]
    public void OverviewRow_FollowsTheReasonOfTheReading()
    {
        var row = new FanRow("thinkpad/pwm1", "Fan");
        row.Update(new FanReading("thinkpad/pwm1", "Fan", 2100, 0, CanControl: false,
            ReadOnlyReason: FanReadOnlyReason.DriverLocked));

        Assert.True(row.IsReadOnly);
        Assert.EndsWith(ReadOnlyReasonText.Of(FanReadOnlyReason.DriverLocked)!, row.ReadOnlyNote);

        row.Update(new FanReading("thinkpad/pwm1", "Fan", 2100, 0, CanControl: false));
        Assert.Equal("Nur Anzeige - dieser Lüfter lässt sich nicht steuern.", row.ReadOnlyNote);
    }

    [Fact]
    public void SettingsRow_SaysWhy()
    {
        var row = new FanAssignRow(new FanConfig { FanId = "thinkpad/pwm1" }, selected: null,
            new ObservableCollection<CurveEditRow>(), canControl: false, readOnlyReason: FanReadOnlyReason.DriverLocked);

        Assert.EndsWith(ReadOnlyReasonText.Of(FanReadOnlyReason.DriverLocked)!, row.ReadOnlyNote);
    }
}
