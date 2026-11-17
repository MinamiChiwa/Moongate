using System.Text.Json;

namespace MabiTool.Infrastructure;

/// <summary>读取本机维护清单与日志：限制长度与深度，拒绝注释、尾随逗号与重复键；不合规时抛出 <see cref="JsonException"/>。</summary>
internal static class StrictJson
{
    public static JsonDocument Parse(ReadOnlySpan<byte> bytes, int maximumBytes = 1_048_576)
    {
        if (bytes.Length == 0 || bytes.Length > maximumBytes) throw new JsonException("JSON 长度超出范围");
        var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
        {
            MaxDepth = 24, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false
        });
        try { RejectDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("JSON 含重复键：" + property.Name);
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
