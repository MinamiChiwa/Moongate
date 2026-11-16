using System.Security.Cryptography;
using System.Text;

namespace MabiTool.Providers.TwObservation.Combat;

public sealed record TwClientFileStamp(string RelativePath, long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);

/// <summary>校验固定台服构建；调用方在显式绑定时计算哈希，刷新时只核对文件戳。</summary>
public static class TwClientFingerprint
{
    public const string ImageName = "MabinogiMobile.exe";
    public const string UnitySha256 = "e969642866c2195f16a0bd0e52ff8ffe87cad5b213fda9a62ca1564970e2fa26";
    private static readonly IReadOnlyDictionary<string, string> expected = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GameAssembly.dll"] = Tw20261110CombatProtocol.AssemblySha256,
        ["MabinogiMobile_Data/il2cpp_data/Metadata/global-metadata.dat"] = Tw20261110CombatProtocol.MetadataSha256,
        ["UnityPlayer.dll"] = UnitySha256
    };
    public static string GameBuild { get; } = "tw-20261110:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(
        string.Join(":", expected.Values))));

    public static string GameBuildForInventoryLayout(string assemblySha256, string metadataSha256)
    {
        if (assemblySha256 is not { Length: 64 } || metadataSha256 is not { Length: 64 } ||
            assemblySha256.Any(c => !char.IsAsciiHexDigit(c)) || metadataSha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("库存布局指纹必须为 SHA-256");
        if (assemblySha256.Equals(Tw20261110CombatProtocol.AssemblySha256, StringComparison.OrdinalIgnoreCase) &&
            metadataSha256.Equals(Tw20261110CombatProtocol.MetadataSha256, StringComparison.OrdinalIgnoreCase)) return GameBuild;
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(
            assemblySha256.ToLowerInvariant() + ":" + metadataSha256.ToLowerInvariant())));
    }

    public static Task<bool> MatchesAsync(string imagePath, CancellationToken ct) => MatchesFilesAsync(imagePath, expected, ct);

    public static async Task<bool> MatchesFilesAsync(string imagePath, IReadOnlyDictionary<string, string> expectedHashes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedHashes);
        ct.ThrowIfCancellationRequested();
        if (expectedHashes.Count != expected.Count || expected.Keys.Any(key => !expectedHashes.ContainsKey(key)) ||
            expectedHashes.Values.Any(value => value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))) return false;
        var streams = new List<FileStream>();
        try
        {
            var before = ReadFileStamps(imagePath);
            if (before is null) return false;
            var directory = Path.GetDirectoryName(Path.GetFullPath(imagePath))!;
            // 同时持有只读共享句柄，Windows 在整个哈希窗口拒绝写入、替换及删除。
            foreach (var relative in expected.Keys)
                streams.Add(new FileStream(Path.Combine(directory, relative), FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
            for (var index = 0; index < streams.Count; index++)
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(streams[index], ct).ConfigureAwait(false));
                if (!actual.Equals(expectedHashes[expected.Keys.ElementAt(index)], StringComparison.OrdinalIgnoreCase)) return false;
            }
            var after = ReadFileStamps(imagePath);
            return after is not null && before.SequenceEqual(after);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return false; }
        finally { foreach (var stream in streams) await stream.DisposeAsync().ConfigureAwait(false); }
    }

    public static IReadOnlyList<TwClientFileStamp>? ReadFileStamps(string imagePath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(imagePath) || !Path.GetFileName(imagePath).Equals(ImageName, StringComparison.OrdinalIgnoreCase)) return null;
            var image = Path.GetFullPath(imagePath);
            var directory = Path.GetDirectoryName(image)!;
            var result = new List<TwClientFileStamp>();
            foreach (var relative in new[] { ImageName }.Concat(expected.Keys))
            {
                var path = Path.Combine(directory, relative);
                RejectLinks(path);
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) return null;
                result.Add(new(relative, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc));
            }
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return null; }
    }

    private static void RejectLinks(string path)
    {
        for (FileSystemInfo? current = new FileInfo(path); current is not null; current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("客户端路径包含链接");
    }
}
