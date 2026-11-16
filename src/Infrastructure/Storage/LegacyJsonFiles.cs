using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed record LegacyJsonSource(string SourceId, string SourceLabel, string SourceHash,
    IReadOnlyList<ImportFileSnapshot> Files, IReadOnlyDictionary<string, JsonElement> Documents);

public static class LegacyJsonFiles
{
    public const int MaximumRequestBytes = 4 * 1024 * 1024;
    public const int MaximumSourceBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static LegacyJsonSource Read(SettingsImportRequest request, IReadOnlySet<string> allowed, CancellationToken ct)
    {
        if (!Regex.IsMatch(request.SourceId ?? "", @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,63}\z") ||
            request.SourceLabel is not { Length: >= 1 and <= 200 } || request.SourceLabel.Any(char.IsControl) ||
            request.Files is null || request.Files.Count < 1 || request.Files.Count > allowed.Count || request.Files.Any(x => x is null))
            throw Invalid("", "来源标识、名称或文件数量无效");
        var files = new List<ImportFileSnapshot>();
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        long sourceBytes = 0, encodedBytes = 0;
        foreach (var file in request.Files.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (file.Name is null || !allowed.Contains(file.Name) || documents.ContainsKey(file.Name))
                throw Invalid(file.Name ?? "", "须使用不同的已登记 JSON 文件名");
            if (file.ContentBase64 is null || (encodedBytes += file.ContentBase64.Length) > MaximumRequestBytes)
                throw new OperationException("request.bodyTooLarge", "设置导入请求超过 4 MiB");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(file.ContentBase64); }
            catch (FormatException) { throw Invalid(file.Name, "文件内容不是有效 base64"); }
            if ((sourceBytes += bytes.Length) > MaximumSourceBytes)
                throw new OperationException("request.bodyTooLarge", "设置导入原文件超过 2 MiB");
            files.Add(new(file.Name, Convert.ToHexString(SHA256.HashData(bytes)), new((ulong)bytes.Length)));
            try
            {
                var content = bytes.AsSpan();
                if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
                using var document = JsonDocument.Parse(Utf8.GetString(content), new() { MaxDepth = 32 });
                ValidateJson(document.RootElement, file.Name, ct);
                documents.Add(file.Name, document.RootElement.Clone());
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException or InvalidOperationException)
            { throw Invalid(file.Name, "须为有效 Unicode 的 UTF-8 JSON，深度不超过 32"); }
        }
        return new(request.SourceId!, request.SourceLabel,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(files, ContractJson.Options))), files, documents);
    }

    private static void ValidateJson(JsonElement value, string file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid(file, "JSON 包含重复属性");
                _ = Utf8.GetByteCount(property.Name);
                ValidateJson(property.Value, file, ct);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var row in value.EnumerateArray()) ValidateJson(row, file, ct);
        else if (value.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(value.GetString()!);
    }

    public static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var result) ? result : default;
    public static string Text(JsonElement value, string file, int maximum, bool optional = false)
    {
        if (optional && value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum ||
            text.Any(char.IsControl)) throw Invalid(file, "文本格式或长度无效");
        return text;
    }
    public static OperationException Invalid(string file, string message) => new("import.invalidInput", message,
        new Dictionary<string, JsonElement> { ["file"] = JsonSerializer.SerializeToElement(file) });
}
