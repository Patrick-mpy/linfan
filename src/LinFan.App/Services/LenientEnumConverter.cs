// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinFan.App.Services;

/// <summary>
/// Reads an enum of the backup's interface preferences by name (or number) and turns a value this build does not know -
/// a newer LinFan's theme or accent - into the enum's first value (System / Standard). Without it the whole backup,
/// fans and curves included, would fail to read over a display setting. Writes the name.
/// </summary>
public sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse(reader.GetString(), ignoreCase: true, out T named) && Enum.IsDefined(named))
            return named;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number)
            && Enum.IsDefined(typeof(T), number))
            return (T)Enum.ToObject(typeof(T), number);
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number))
            throw new JsonException($"Expected a {typeof(T).Name} name.");
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
