// SPDX-License-Identifier: GPL-3.0-or-later

using LinFan.Hardware.Mac.Smc;

namespace LinFan.Hardware.Mac.Tests;

/// <summary>
/// Deckt den <see cref="SmcCodec"/> für jeden von LinFan genutzten SMC-Datentyp ab - die
/// (auf echter Hardware verifizierte) Interpretation der Rohbytes. Decode ist wurf-frei (unbekannt/zu
/// kurz ⇒ NaN); Encode liefert für nicht-schreibbare Typen <c>null</c>.
/// </summary>
public sealed class SmcCodecTests
{
    [Fact]
    public void Decode_Flt_IsLittleEndian_MatchesRealFanBytes()
    {
        // Auf Apple-Silicon-Hardware gemessen: F0Ac-Bytes 22-32-A9-44 ⇒ ~1353,6 RPM.
        var v = new SmcValue("flt ", new byte[] { 0x22, 0x32, 0xA9, 0x44 });
        Assert.Equal(1353.567, SmcCodec.Decode(v), 2);
    }

    [Theory]
    [InlineData(45.5, 0x2D, 0x80)]   // 45,5 * 256 = 11648 = 0x2D80
    [InlineData(-10.0, 0xF6, 0x00)]  // signed: -10 * 256 = -2560 = 0xF600
    public void Decode_Sp78_SignedFixedPoint(double expected, int b0, int b1)
    {
        var v = new SmcValue("sp78", new byte[] { (byte)b0, (byte)b1 });
        Assert.Equal(expected, SmcCodec.Decode(v), 3);
    }

    [Fact]
    public void Decode_Fpe2_UnsignedFixedPoint()
    {
        // 1350 * 4 = 5400 = 0x1518
        var v = new SmcValue("fpe2", new byte[] { 0x15, 0x18 });
        Assert.Equal(1350.0, SmcCodec.Decode(v), 3);
    }

    [Theory]
    [InlineData("ui8 ", new byte[] { 200 }, 200.0)]
    [InlineData("ui16", new byte[] { 0x01, 0x2C }, 300.0)]
    [InlineData("ui32", new byte[] { 0x00, 0x00, 0x09, 0x5C }, 2396.0)]
    public void Decode_UnsignedIntegers_BigEndian(string type, byte[] data, double expected)
    {
        Assert.Equal(expected, SmcCodec.Decode(new SmcValue(type, data)), 3);
    }

    [Fact]
    public void Decode_UnknownType_YieldsNaN()
    {
        Assert.True(double.IsNaN(SmcCodec.Decode(new SmcValue("zzzz", new byte[] { 1, 2, 3, 4 }))));
    }

    [Fact]
    public void Decode_TooShort_YieldsNaN_NotThrow()
    {
        Assert.True(double.IsNaN(SmcCodec.Decode(new SmcValue("flt ", new byte[] { 0, 0 }))));
    }

    [Fact]
    public void Encode_Flt_RoundTrips()
    {
        byte[]? bytes = SmcCodec.Encode("flt ", 1350.0);
        Assert.NotNull(bytes);
        Assert.Equal(1350.0, SmcCodec.Decode(new SmcValue("flt ", bytes!)), 1);
    }

    [Fact]
    public void Encode_Fpe2_MatchesKnownBytes()
    {
        byte[]? bytes = SmcCodec.Encode("fpe2", 1350.0);
        Assert.Equal(new byte[] { 0x15, 0x18 }, bytes);
    }

    [Fact]
    public void Encode_Sp78_MatchesKnownBytes()
    {
        byte[]? bytes = SmcCodec.Encode("sp78", 45.5);
        Assert.Equal(new byte[] { 0x2D, 0x80 }, bytes);
    }

    [Fact]
    public void Encode_Ui8_ClampsToByteRange()
    {
        Assert.Equal(new byte[] { 255 }, SmcCodec.Encode("ui8 ", 300.0));
        Assert.Equal(new byte[] { 0 }, SmcCodec.Encode("ui8 ", -5.0));
    }

    [Fact]
    public void Encode_UnsupportedType_ReturnsNull()
    {
        Assert.Null(SmcCodec.Encode("zzzz", 1.0));
    }
}
