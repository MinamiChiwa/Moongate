using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>通过十进制字符串传输完整 UInt64，避免 JavaScript 整数舍入。</summary>
[JsonConverter(typeof(UInt64StringConverter))]
public readonly record struct UInt64String(ulong Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public static UInt64String Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 20 ||
            (text.Length > 1 && text[0] == '0') || text.Any(c => c is < '0' or > '9') ||
            !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new FormatException("值必须为 0–18446744073709551615 的规范十进制字符串");
        return new(value);
    }
}

public sealed class UInt64StringConverter : JsonConverter<UInt64String>
{
    public override UInt64String Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("UInt64 必须通过字符串传输");
        try { return UInt64String.Parse(reader.GetString()!); }
        catch (FormatException) { throw new JsonException("UInt64 字符串无效或超出范围"); }
    }

    public override void Write(Utf8JsonWriter writer, UInt64String value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
