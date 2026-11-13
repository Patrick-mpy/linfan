// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Core.Services;
using Xunit;

namespace LinFan.Core.Tests;

public class UntrustedTextTests
{
    [Theory]
    [InlineData("line\nforged")]
    [InlineData("cr\rhere")]
    [InlineData("nul\0<0>emerg")]
    [InlineData("\u001b]0;title\u0007")]   // OSC window title
    [InlineData("\u001b[8mhidden")]        // SGR conceal
    [InlineData("del\u007f")]
    [InlineData("c1\u009bcsi")]
    [InlineData("bidi\u202eevil")]        // right-to-left override
    [InlineData("sep\u2028line")]
    public void IsClean_RejectsControlFormatAndSeparatorCharacters(string text) =>
        Assert.False(UntrustedText.IsClean(text));

    [Fact]
    public void IsClean_RejectsALoneSurrogate() => Assert.False(UntrustedText.IsClean("lone" + (char)0xD800 + "half"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CPU-Lüfter vorne")]
    [InlineData("Ventilateur à 50 % · 温度")]
    [InlineData("Fan 🌀")]                  // surrogate pair
    [InlineData("👨\u200d💻 desk")]          // zero-width joiner inside an emoji sequence
    public void IsClean_AcceptsOrdinaryText(string? text) =>
        Assert.True(UntrustedText.IsClean(text));

    [Fact]
    public void Strip_RemovesOnlyUnsafeCharacters()
    {
        Assert.Equal("Fan 🌀 frontOSC", UntrustedText.Strip("Fan 🌀\n front\u001bOSC\u0007"));
    }

    [Fact]
    public void Strip_CleanText_ReturnsSameInstance()
    {
        string text = "Front intake";
        Assert.Same(text, UntrustedText.Strip(text));
    }

    [Fact]
    public void Escape_MakesUnsafeCharactersVisible_OnOneLine()
    {
        string escaped = UntrustedText.Escape("a\nb\0<0>c\u001b[8m 🌀");

        Assert.Equal("a\\u000Ab\\u0000<0>c\\u001B[8m 🌀", escaped);
        Assert.True(UntrustedText.IsClean(escaped));
    }

    [Fact]
    public void Escape_Null_IsEmpty() => Assert.Equal("", UntrustedText.Escape(null));

    /// <summary>Format characters beyond the BMP count too - tag characters smuggle invisible text.</summary>
    [Fact]
    public void TagCharacters_AreUnsafe_AndEscapedAsOneCodePoint()
    {
        string hidden = "Fan" + char.ConvertFromUtf32(0xE0041) + char.ConvertFromUtf32(0xE0042);

        Assert.False(UntrustedText.IsClean(hidden));
        Assert.Equal("Fan", UntrustedText.Strip(hidden));
        Assert.Equal("Fan\\U000E0041\\U000E0042", UntrustedText.Escape(hidden));
    }
}
