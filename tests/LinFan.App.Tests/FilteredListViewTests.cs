// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using LinFan.App.Services;
using Xunit;

namespace LinFan.App.Tests;

public class FilteredListViewTests
{
    [Theory]
    [InlineData("cpu", "CPU Fan", "hwmon0", true)]   // case-insensitiver Teilstring
    [InlineData("FAN", "cpu fan", null, true)]       // Groß/Klein egal, null-Feld ignoriert
    [InlineData("gpu", "CPU Fan", "hwmon0", false)]  // kein Treffer
    [InlineData("  cpu  ", "CPU Fan", null, true)]   // Suchtext wird getrimmt
    [InlineData("", "irgendwas", null, true)]        // leerer Suchtext matcht jedes nicht-null Feld
    public void Matches_SubstringCaseInsensitive(string text, string? a, string? b, bool expected) =>
        Assert.Equal(expected, FilteredListView.Matches(text, a, b));

    [Fact]
    public void Matches_AllFieldsNull_IsFalse() =>
        Assert.False(FilteredListView.Matches("x", null, null));

    [Fact]
    public void Sync_DifferentSource_ReplacesContent()
    {
        var target = new ObservableCollection<int> { 1, 2 };
        FilteredListView.Sync(target, new[] { 3, 4, 5 });
        Assert.Equal(new[] { 3, 4, 5 }, target);
    }

    [Fact]
    public void Sync_EqualSource_DoesNotRaiseChange()
    {
        // „nur bei Abweichung": eine identische Quelle darf die gebundene Liste nicht auffrischen.
        var target = new ObservableCollection<int> { 1, 2, 3 };
        int changes = 0;
        target.CollectionChanged += (_, _) => changes++;

        FilteredListView.Sync(target, new[] { 1, 2, 3 });

        Assert.Equal(0, changes);
        Assert.Equal(new[] { 1, 2, 3 }, target);
    }
}
