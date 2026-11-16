using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>累计伤害使用整数十进制文本；单次 UInt64 伤害相加后仍保持原精度。</summary>
[JsonConverter(typeof(DamageAmountStringConverter))]
public readonly record struct DamageAmountString
{
    public BigInteger Value { get; }
    public DamageAmountString(BigInteger value)
    {
        if (value.Sign < 0 || value.ToString(CultureInfo.InvariantCulture).Length > 128)
            throw new ArgumentOutOfRangeException(nameof(value), "累计伤害须为最多 128 位的非负整数");
        Value = value;
    }
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    public static implicit operator DamageAmountString(UInt64String value) => new(value.Value);
    public static DamageAmountString Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 128 || text.Length > 1 && text[0] == '0' ||
            text.Any(c => c is < '0' or > '9')) throw new FormatException("累计伤害十进制字符串无效");
        return new(BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture));
    }
}

public sealed class DamageAmountStringConverter : JsonConverter<DamageAmountString>
{
    public override DamageAmountString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("累计伤害须通过字符串传输");
        try { return DamageAmountString.Parse(reader.GetString()!); }
        catch (FormatException) { throw new JsonException("累计伤害十进制字符串无效"); }
    }
    public override void Write(Utf8JsonWriter writer, DamageAmountString value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
