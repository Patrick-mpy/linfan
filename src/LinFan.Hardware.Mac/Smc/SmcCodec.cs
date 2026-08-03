// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace LinFan.Hardware.Mac.Smc;

/// <summary>
/// Interpretiert SMC-Rohwerte (<see cref="SmcValue"/>) als <see cref="double"/> und kodiert Zielwerte
/// zurück in den nativen Datentyp eines Keys. Rein und ohne I/O — die ausführbare Referenz für die
/// (empirisch auf echter Hardware verifizierten) SMC-Datentypen; Unit-Tests decken jeden Typ ab.
/// <para>
/// Endianness: <c>flt</c> ist <b>little-endian</b> IEEE-754 (auf Apple-Silicon-Hardware bestätigt: die
/// Lüfterdrehzahl-Bytes <c>22-32-A9-44</c> ⇒ 1353,6). Die Festkomma- und Integer-Typen sind
/// <b>big-endian</b> (Netzwerk-Reihenfolge), wie beim SMC üblich.
/// </para>
/// </summary>
internal static class SmcCodec
{
    /// <summary>
    /// Dekodiert einen Rohwert. Unbekannte oder zu kurze Typen liefern <see cref="double.NaN"/>
    /// („kein Wert") statt zu werfen — der Lese-Pfad ist vertraglich wurf-frei.
    /// </summary>
    public static double Decode(SmcValue value)
    {
        byte[] d = value.Data;
        return Normalize(value.Type) switch
        {
            "ui8" => d.Length >= 1 ? d[0] : double.NaN,
            "ui16" => d.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(d) : double.NaN,
            "ui32" => d.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(d) : double.NaN,
            "si8" => d.Length >= 1 ? (sbyte)d[0] : double.NaN,
            "si16" => d.Length >= 2 ? BinaryPrimitives.ReadInt16BigEndian(d) : double.NaN,
            "flt" => d.Length >= 4 ? BitConverter.ToSingle(LittleEndian(d, 4)) : double.NaN,
            "sp78" => d.Length >= 2 ? BinaryPrimitives.ReadInt16BigEndian(d) / 256.0 : double.NaN,
            "sp87" => d.Length >= 2 ? BinaryPrimitives.ReadInt16BigEndian(d) / 128.0 : double.NaN,
            "sp96" => d.Length >= 2 ? BinaryPrimitives.ReadInt16BigEndian(d) / 64.0 : double.NaN,
            "spb4" => d.Length >= 2 ? BinaryPrimitives.ReadInt16BigEndian(d) / 16.0 : double.NaN,
            "fp88" => d.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(d) / 256.0 : double.NaN,
            "fpe2" => d.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(d) / 4.0 : double.NaN,
            "fp1f" => d.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(d) / 32768.0 : double.NaN,
            _ => double.NaN,
        };
    }

    /// <summary>
    /// Kodiert einen Zielwert in die rohen Bytes des angegebenen SMC-Datentyps (für den Steuer-Pfad, z. B.
    /// Ziel-Drehzahl <c>F0Tg</c> oder Modus <c>F0Md</c>). Liefert <c>null</c> für Typen, die LinFan nicht
    /// zu schreiben braucht — der Aufrufer überspringt den Write dann sicher (Fail-Safe), statt zu raten.
    /// </summary>
    public static byte[]? Encode(string type, double value) => Normalize(type) switch
    {
        "ui8" => new[] { (byte)Math.Clamp(Math.Round(value), 0, 255) },
        "ui16" => BigEndian16((ushort)Math.Clamp(Math.Round(value), 0, ushort.MaxValue)),
        "ui32" => BigEndian32((uint)Math.Clamp(Math.Round(value), 0, uint.MaxValue)),
        "flt" => LittleEndian(BitConverter.GetBytes((float)value), 4),
        "sp78" => BigEndian16((ushort)(short)Math.Clamp(Math.Round(value * 256.0), short.MinValue, short.MaxValue)),
        "fp88" => BigEndian16((ushort)Math.Clamp(Math.Round(value * 256.0), 0, ushort.MaxValue)),
        "fpe2" => BigEndian16((ushort)Math.Clamp(Math.Round(value * 4.0), 0, ushort.MaxValue)),
        _ => null,
    };

    /// <summary>Normalisiert den 4-Zeichen-Typcode (trimmt das SMC-typische nachlaufende Leerzeichen, z. B. <c>"flt "</c> → <c>"flt"</c>).</summary>
    private static string Normalize(string type) => type.TrimEnd();

    /// <summary>Liefert die ersten <paramref name="n"/> Bytes in little-endian-Reihenfolge (für <c>flt</c> auf big-endian-Hosts — pragmatisch, LinFan läuft nur little-endian).</summary>
    private static byte[] LittleEndian(byte[] src, int n)
    {
        var b = new byte[n];
        Array.Copy(src, b, Math.Min(n, src.Length));
        if (!BitConverter.IsLittleEndian) Array.Reverse(b);
        return b;
    }

    private static byte[] BigEndian16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        return b;
    }

    private static byte[] BigEndian32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }
}
