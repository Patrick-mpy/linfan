// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.App.Services;
using Xunit;

namespace LinFan.App.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v0.1.0", 0, 1, 0, null)]                 // führendes v
    [InlineData("0.1.0-dev", 0, 1, 0, "dev")]             // Prerelease
    [InlineData("0.1.0-dev+abc123", 0, 1, 0, "dev")]      // Build-Metadaten verworfen
    [InlineData("2", 2, 0, 0, null)]                      // fehlende Stellen = 0
    [InlineData("1.4", 1, 4, 0, null)]
    public void TryParse_ValidForms(string text, int maj, int min, int pat, string? pre)
    {
        Assert.True(SemVer.TryParse(text, out SemVer v));
        Assert.Equal(new SemVer(maj, min, pat, pre), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]     // zu viele Stellen
    [InlineData("1.x.0")]       // nicht-numerisch
    public void TryParse_InvalidForms(string? text) =>
        Assert.False(SemVer.TryParse(text, out _));

    [Fact]
    public void Compare_CoreOrdering()
    {
        Assert.True(V("0.2.0").CompareTo(V("0.1.9")) > 0);
        Assert.True(V("1.0.0").CompareTo(V("0.9.9")) > 0);
        Assert.True(V("0.1.1").CompareTo(V("0.1.0")) > 0);
        Assert.Equal(0, V("1.2.3").CompareTo(V("1.2.3")));
    }

    [Fact]
    public void Compare_ReleaseOutranksPrerelease()
    {
        // 0.1.0 (Release) ist neuer als 0.1.0-dev (Prerelease) - ein Dev-Build wird über die Release informiert.
        Assert.True(V("0.1.0").CompareTo(V("0.1.0-dev")) > 0);
        Assert.True(V("0.1.0-dev").CompareTo(V("0.1.0")) < 0);
        // Höherer Core schlägt Prerelease-Status trotzdem.
        Assert.True(V("0.2.0-rc1").CompareTo(V("0.1.0")) > 0);
    }

    private static SemVer V(string s)
    {
        Assert.True(SemVer.TryParse(s, out SemVer v));
        return v;
    }
}
