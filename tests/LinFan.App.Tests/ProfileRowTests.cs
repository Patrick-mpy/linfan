// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Controllers;
using LinFan.Core.Models;
using Xunit;

namespace LinFan.App.Tests;

/// <summary>
/// Sichert das Trim-/Fallback-Verhalten des Profilnamens ab: ein geleerter Name darf nicht still als
/// leerer String persistiert werden, sondern fällt auf den ursprünglich geladenen Namen zurück.
/// </summary>
public sealed class ProfileRowTests
{
    private static ProfileRow Make(string name) =>
        new("profile-1", name, Array.Empty<CurveConfig>(), Array.Empty<ProfileAssignment>());

    [Fact]
    public void ToProfile_TrimsName()
    {
        var row = Make("Standard");
        row.Name = "  Silent  ";

        Assert.Equal("Silent", row.ToProfile().Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ToProfile_EmptyName_FallsBackToOriginal(string emptied)
    {
        var row = Make("Standard");
        row.Name = emptied;

        Assert.Equal("Standard", row.ToProfile().Name);
    }

    [Fact]
    public void ToProfile_WithExplicitCurves_AlsoFallsBackOnEmptyName()
    {
        var row = Make("Standard");
        row.Name = "  ";

        Profile p = row.ToProfile(Array.Empty<CurveConfig>(), Array.Empty<ProfileAssignment>());

        Assert.Equal("Standard", p.Name);
    }
}
