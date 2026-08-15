// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinFan.Ipc;

/// <summary>Gemeinsame Serialisierungs-Optionen für den NDJSON-Transport.</summary>
internal static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Sensorwerte können NaN sein (z. B. nicht lesbarer Kanal) - als "NaN" serialisieren.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // Vertrags-Enums (Status/Phase/FailReason) als ihren Namen serialisieren, nicht als Ordinalzahl -
        // robust gegen Umsortieren der Member und selbsterklärend auf der Leitung.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Serialisiert <paramref name="value"/> als eine NDJSON-Zeile (UTF-8-JSON + <c>'\n'</c>) - die
    /// gemeinsame Framing-Quelle für <c>IpcServer</c> und <c>IpcClient</c> (bis hierher an beiden Enden
    /// dupliziert). <see cref="JsonSerializer.SerializeToUtf8Bytes{TValue}(TValue, JsonSerializerOptions)"/>
    /// spart die Zwischen-String-Allokation des früheren <c>Serialize + "\n" + GetBytes</c>.
    /// </summary>
    public static byte[] SerializeLine<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        byte[] framed = new byte[json.Length + 1];
        Array.Copy(json, framed, json.Length);
        framed[^1] = (byte)'\n';
        return framed;
    }
}
