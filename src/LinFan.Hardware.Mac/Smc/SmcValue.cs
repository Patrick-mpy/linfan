// SPDX-License-Identifier: GPL-3.0-or-later

namespace LinFan.Hardware.Mac.Smc;

/// <summary>
/// Rohwert eines SMC-Keys: der 4-Zeichen-Datentyp-Code (<c>flt </c>, <c>sp78</c>, <c>ui8 </c>, …) plus
/// die rohen Bytes. Die Interpretation (→ <see cref="double"/>) und die Rückkodierung übernimmt
/// <see cref="SmcCodec"/> — bewusst getrennt von der I/O-Naht (<see cref="ISmc"/>), damit das Dekodieren
/// hardwarefrei testbar bleibt.
/// </summary>
/// <param name="Type">SMC-Datentyp als 4-Zeichen-Code (mit evtl. nachlaufendem Leerzeichen).</param>
/// <param name="Data">Rohe Bytes des Werts (big-endian bei den Festkomma-/Integer-Typen; <c>flt</c> little-endian).</param>
internal readonly record struct SmcValue(string Type, byte[] Data);
