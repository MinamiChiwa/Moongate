using System.Text.Json;
using System.Text.RegularExpressions;

namespace MabiTool.Host.Extensions;

/// <summary>
/// 扩展自己的存储，限定在 extensions/&lt;标识&gt;/data/：键值存于 kv.json，文件存于 files/（扁平目录，不接受子路径）。
/// 键值与文件合计不超过 16 MiB；键最多 1,024 个、每个值不超过 256 KiB；单个文件不超过 4 MiB。写入以临时文件替换，读到的要么是旧内容要么是完整新内容。
/// </summary>
public sealed class ExtensionStorage(string directory)
{
    public const long QuotaBytes = 16L * 1024 * 1024;
    public const int MaximumKeys = 1024, MaximumValueBytes = 256 * 1024, MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly Regex Key = new(@"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z", RegexOptions.CultureInvariant);
    private static readonly Regex FileName = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);
    private readonly object gate = new();
    private string KeyValuePath => Path.Combine(directory, "kv.json");
    private string Files => Path.Combine(directory, "files");

    public JsonElement? Get(string key)
    {
        RequireKey(key);
        lock (gate) return ReadAll().TryGetValue(key, out var value) ? value : null;
    }

    public IReadOnlyList<string> List()
    {
        lock (gate) return ReadAll().Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public void Set(string key, JsonElement value)
    {
        RequireKey(key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value).Length;
        if (bytes > MaximumValueBytes) throw new ExtensionCallException("extension.storageQuota", "单个值超过 256 KiB");
        lock (gate)
        {
            var all = ReadAll();
            all[key] = value.Clone();
            if (all.Count > MaximumKeys) throw new ExtensionCallException("extension.storageQuota", "键超过 1,024 个");
            var content = JsonSerializer.SerializeToUtf8Bytes(all);
            if (content.Length + FilesBytes() > QuotaBytes) throw new ExtensionCallException("extension.storageQuota", "存储总量超过 16 MiB");
            Replace(KeyValuePath, content);
        }
    }

    public bool Delete(string key)
    {
        RequireKey(key);
        lock (gate)
        {
            var all = ReadAll();
            if (!all.Remove(key)) return false;
            Replace(KeyValuePath, JsonSerializer.SerializeToUtf8Bytes(all));
            return true;
        }
    }

    public byte[]? ReadFile(string name)
    {
        var path = FilePath(name);
        lock (gate) return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void WriteFile(string name, byte[] content)
    {
        var path = FilePath(name);
        if (content.Length > MaximumFileBytes) throw new ExtensionCallException("extension.storageQuota", "单个文件超过 4 MiB");
        lock (gate)
        {
            var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
            var kv = File.Exists(KeyValuePath) ? new FileInfo(KeyValuePath).Length : 0;
            if (kv + FilesBytes() - existing + content.Length > QuotaBytes) throw new ExtensionCallException("extension.storageQuota", "存储总量超过 16 MiB");
            Directory.CreateDirectory(Files);
            Replace(path, content);
        }
    }

    public bool DeleteFile(string name)
    {
        var path = FilePath(name);
        lock (gate)
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
    }

    private string FilePath(string name) => FileName.IsMatch(name) && name is not ("." or "..")
        ? Path.Combine(Files, name) : throw new ExtensionCallException("request.invalid", "文件名只能是不含路径的字母、数字、点、下划线或连字符");

    private static void RequireKey(string key)
    {
        if (!Key.IsMatch(key)) throw new ExtensionCallException("request.invalid", "键须为 1–128 个字母、数字或 . _ : -");
    }

    private long FilesBytes() => Directory.Exists(Files) ? new DirectoryInfo(Files).EnumerateFiles().Sum(x => x.Length) : 0;

    private Dictionary<string, JsonElement> ReadAll()
    {
        if (!File.Exists(KeyValuePath)) return new(StringComparer.Ordinal);
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(KeyValuePath)) ?? new(StringComparer.Ordinal); }
        catch (JsonException) { throw new ExtensionCallException("extension.storageDamaged", "kv.json 已损坏"); }
    }

    private void Replace(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(content);
                file.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>能力调用失败：<see cref="Code"/> 作为错误响应的 <c>error.data.reason</c> 回给扩展。</summary>
public sealed class ExtensionCallException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
