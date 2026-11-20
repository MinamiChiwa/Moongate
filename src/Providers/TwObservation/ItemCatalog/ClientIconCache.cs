using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.ItemCatalog;

/// <summary>
/// 图标解码结果的磁盘缓存：<c>&lt;根&gt;/v{RulesVersion}/&lt;ClientId 前 32 位&gt;/</c> 下一份 catalog.json 与每个可用键一个 PNG。
/// 客户端资源索引变化（ClientId 不同）或规则版本变化即视为没有缓存；清单、文件长度、SHA-256 或 PNG 头不符时整份丢弃。
/// 保留当前与最近使用的另一份客户端结果，总量超过 <see cref="MaximumBytes"/> 时只保留当前一份；其他规则版本与残留临时目录一并删除。
/// 缓存只是加速：读写失败时按没有缓存处理，不影响图标读取。
/// </summary>
public static partial class ClientIconCache
{
    /// <summary>解码规则或图标键变化时递增，旧缓存随之失效。</summary>
    public const int RulesVersion = 1;
    public const long MaximumBytes = 8L * 1024 * 1024;
    public const int MaximumClients = 2;
    private const int MaximumManifestBytes = 64 * 1024;
    private const string ManifestName = "catalog.json";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    private sealed record Manifest(int RulesVersion, string ClientId, string[] Reasons, ManifestIcon[] Icons);
    private sealed record ManifestIcon(string Key, string? Reason, string? Tint, ClientIconSource Source, string SourcePath,
        int? Width, int? Height, string? Sha256, string? NameKey, string? DisplayName);

    public static ClientIconSet? Read(string root, string clientId)
    {
        if (!ClientIdPattern().IsMatch(clientId)) return null;
        var directory = ClientDirectory(root, clientId);
        try
        {
            if (!Directory.Exists(directory) || IsLink(directory)) return null;
            var manifestPath = Path.Combine(directory, ManifestName);
            var info = new FileInfo(manifestPath);
            if (!info.Exists || info.Length > MaximumManifestBytes) return Discard(directory);
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(manifestPath), Options);
            if (manifest is null || manifest.RulesVersion != RulesVersion || manifest.ClientId != clientId || manifest.Reasons is null ||
                manifest.Icons is null || !manifest.Icons.Select(icon => icon?.Key).SequenceEqual(ClientAttributeIcons.Definitions.Select(definition => definition.Key)))
                return Discard(directory);
            var icons = new List<ClientIconEntry>(manifest.Icons.Length);
            foreach (var icon in manifest.Icons)
            {
                if (icon.SourcePath is null || !Enum.IsDefined(icon.Source)) return Discard(directory);
                if (icon.Sha256 is null)
                {
                    if (icon.Reason is null) return Discard(directory);
                    icons.Add(new(icon.Key, icon.Reason, icon.Tint, icon.Source, icon.SourcePath, null, null, null, null, icon.NameKey, icon.DisplayName));
                    continue;
                }
                var path = Path.Combine(directory, icon.Key + ".png");
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > ClientAttributeIcons.MaximumPngBytes || icon.Width is not > 0 || icon.Height is not > 0) return Discard(directory);
                var png = File.ReadAllBytes(path);
                if (!png.AsSpan().StartsWith(PngSignature) || Convert.ToHexStringLower(SHA256.HashData(png)) != icon.Sha256) return Discard(directory);
                icons.Add(new(icon.Key, null, icon.Tint, icon.Source, icon.SourcePath, icon.Width, icon.Height, png, icon.Sha256, icon.NameKey, icon.DisplayName));
            }
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
            return new ClientIconSet(clientId, icons, manifest.Reasons);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return Discard(directory);
        }
    }

    public static void Write(string root, ClientIconSet set)
    {
        if (!ClientIdPattern().IsMatch(set.ClientId)) return;
        var versionDirectory = Path.Combine(root, "v" + RulesVersion);
        var temporary = Path.Combine(versionDirectory, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(versionDirectory);
            if (IsLink(root) || IsLink(versionDirectory)) return;
            Directory.CreateDirectory(temporary);
            foreach (var icon in set.Icons)
                if (icon.Png is { } png) File.WriteAllBytes(Path.Combine(temporary, icon.Key + ".png"), png);
            var manifest = new Manifest(RulesVersion, set.ClientId, [.. set.Reasons], [.. set.Icons.Select(icon =>
                new ManifestIcon(icon.Key, icon.Reason, icon.Tint, icon.Source, icon.SourcePath, icon.Width, icon.Height, icon.Sha256, icon.NameKey, icon.DisplayName))]);
            File.WriteAllBytes(Path.Combine(temporary, ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest, Options));
            var target = ClientDirectory(root, set.ClientId);
            if (Directory.Exists(target) && !IsLink(target)) Directory.Delete(target, true);
            Directory.Move(temporary, target);
            Prune(root, set.ClientId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
        }
    }

    /// <summary>只保留当前规则版本下的当前客户端与最近使用的另一份；总量超限时只保留当前一份。不跟随联接或符号链接。</summary>
    public static void Prune(string root, string keepClientId)
    {
        try
        {
            if (!Directory.Exists(root) || IsLink(root)) return;
            var current = "v" + RulesVersion;
            foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
                if (!entry.Name.Equals(current, StringComparison.OrdinalIgnoreCase)) TryDelete(entry.FullName);
            var versionDirectory = new DirectoryInfo(Path.Combine(root, current));
            if (!versionDirectory.Exists || IsLink(versionDirectory.FullName)) return;
            var keep = ClientIdPattern().IsMatch(keepClientId) ? keepClientId[..32] : null;
            var clients = new List<DirectoryInfo>();
            foreach (var entry in versionDirectory.EnumerateFileSystemInfos())
                if (entry is DirectoryInfo directory && !entry.Name.StartsWith('.') && !IsLink(entry.FullName)) clients.Add(directory);
                else TryDelete(entry.FullName);
            var ordered = clients.OrderByDescending(directory => directory.Name == keep).ThenByDescending(directory => directory.LastWriteTimeUtc).ToArray();
            foreach (var stale in ordered.Skip(MaximumClients)) TryDelete(stale.FullName);
            var kept = ordered.Take(MaximumClients).ToArray();
            if (kept.Sum(directory => directory.EnumerateFiles().Sum(file => file.Length)) > MaximumBytes)
                foreach (var directory in kept.Where(directory => directory.Name != keep)) TryDelete(directory.FullName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string ClientDirectory(string root, string clientId) => Path.Combine(root, "v" + RulesVersion, clientId[..32]);

    private static ClientIconSet? Discard(string directory)
    {
        TryDelete(directory);
        return null;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void TryDelete(string path)
    {
        try
        {
            if (!Path.Exists(path)) return;
            if (IsLink(path)) return;
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [System.Text.RegularExpressions.GeneratedRegex("\\A[0-9a-f]{64}\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ClientIdPattern();
}
