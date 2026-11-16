using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MabiTool.RoomServer.Protocol;

internal sealed class RoomProtocolException(string code, JsonObject? details = null) : Exception(code)
{
    internal string Code { get; } = code;
    internal JsonObject? Details { get; } = details;
}

internal static class RoomProtocol
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    internal static JsonElement Field(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    internal static bool Has(this JsonElement value, string name) => value.Field(name).ValueKind != JsonValueKind.Undefined;
    internal static string? Text(this JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string? Text(this JsonElement value, string name) => value.Field(name).Text();
    internal static bool? Boolean(this JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    internal static bool? Boolean(this JsonElement value, string name) => value.Field(name).Boolean();
    internal static double? Number(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    internal static double? Number(this JsonElement value, string name) => value.Field(name).Number();
    internal static int? Integer(this JsonElement value, string name) =>
        value.Field(name).ValueKind == JsonValueKind.Number && value.Field(name).TryGetInt32(out var number) ? number : null;
    internal static bool MissingOrNull(this JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
    internal static bool SafeInteger(double? number) => number is { } n && Math.Abs(n) <= 9_007_199_254_740_991 && Math.Truncate(n) == n;
    internal static string Display(this JsonElement value, int limit = 200)
    {
        var text = value.MissingOrNull() ? "" : value.Text() ?? value.ToString();
        return text[..Math.Min(text.Length, limit)];
    }
    internal static string Identity(JsonElement value)
    {
        var text = value.Text();
        if (text is null || text.Length is < 1 or > 20 || text[0] == '0' || !text.All(char.IsAsciiDigit) ||
            !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)
            throw new RoomProtocolException("BAD_OBSERVATION");
        return text;
    }
    internal static bool Hex(string? text, int length) => text is not null && text.Length == length && text.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static JsonNode? Node(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value, Json);
    internal static string[] Strings(JsonElement value, string code)
    {
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new RoomProtocolException(code);
        return value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
}
