using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>响应中的可扩展枚举保留未知字符串，由调用方决定其展示与能力。</summary>
[JsonConverter(typeof(OpenEnumConverterFactory))]
public readonly record struct OpenEnum<T> where T : struct, Enum
{
    public string RawValue { get; }
    public T? KnownValue => RawValue is not null && Values.TryGetValue(RawValue, out var known) ? known : null;
    public bool IsKnown => KnownValue.HasValue;
    private static readonly IReadOnlyDictionary<string, T> Values = Enum.GetValues<T>()
        .ToDictionary(value => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString()), StringComparer.Ordinal);

    public OpenEnum(string rawValue)
    {
        if (string.IsNullOrEmpty(rawValue) || rawValue.Length > 128 || rawValue.Any(c => c < ' '))
            throw new ArgumentException("枚举值必须为 1–128 字符且不含控制字符", nameof(rawValue));
        RawValue = rawValue;
    }

    public static implicit operator OpenEnum<T>(T value) => Enum.IsDefined(value)
        ? new(JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString()))
        : throw new ArgumentOutOfRangeException(nameof(value));
    public override string ToString() => RawValue;
}

public sealed class OpenEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(OpenEnum<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert.GenericTypeArguments))!;

    private sealed class Converter<T> : JsonConverter<OpenEnum<T>> where T : struct, Enum
    {
        public override OpenEnum<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("枚举必须通过字符串传输");
            try { return new(reader.GetString()!); }
            catch (ArgumentException error) { throw new JsonException("枚举字符串无效", error); }
        }

        public override void Write(Utf8JsonWriter writer, OpenEnum<T> value, JsonSerializerOptions options)
        {
            if (value.RawValue is null) throw new JsonException("枚举未初始化");
            writer.WriteStringValue(value.RawValue);
        }
    }
}
