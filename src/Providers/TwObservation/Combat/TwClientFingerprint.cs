using System.Security.Cryptography;
using System.Text;

namespace MabiTool.Providers.TwObservation.Combat;

public sealed record TwClientFileStamp(string RelativePath, long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);

/// <summary>客户端三个文件的 SHA-256（小写十六进制）。</summary>
public sealed record TwClientHashes(string AssemblySha256, string MetadataSha256, string UnitySha256);

/// <summary>已登记的台服构建：三文件哈希经静态核对与 tw-20261110 协议布局一致。</summary>
public sealed record TwRegisteredBuild(string Name, string AssemblySha256, string MetadataSha256, string UnitySha256);

/// <summary>
/// 台服客户端构建识别。已登记构建（协议布局与 tw-20261110 一致）共用布局标识 <see cref="GameBuild"/>；
/// 未登记但可完整读取的台服客户端以 <see cref="AdaptiveBuild"/>（GameAssembly 与 metadata 哈希）标识，
/// 各项能力由 <see cref="TwBuildVerification"/> 在运行时核对后分别启用。调用方在显式绑定时计算哈希，刷新时只核对文件戳。
/// </summary>
public static class TwClientFingerprint
{
    public const string ImageName = "MabinogiMobile.exe";
    public const string UnitySha256 = "e969642866c2195f16a0bd0e52ff8ffe87cad5b213fda9a62ca1564970e2fa26";
    public const string AssemblyFile = "GameAssembly.dll", MetadataFile = "MabinogiMobile_Data/il2cpp_data/Metadata/global-metadata.dat",
        UnityFile = "UnityPlayer.dll";
    private const string AssemblyPath = AssemblyFile, MetadataPath = MetadataFile, UnityPath = UnityFile;
    private static readonly string[] Files = [AssemblyPath, MetadataPath, UnityPath];
    /// <summary>已登记构建；2026-11-18 构建的协议枚举、收包签名与字段布局经静态核对与 2026-11-10 相同。</summary>
    public static IReadOnlyList<TwRegisteredBuild> RegisteredBuilds { get; } =
    [
        new("tw-20261110", Tw20261110CombatProtocol.AssemblySha256, Tw20261110CombatProtocol.MetadataSha256, UnitySha256),
        new("tw-20261118", "680b919f19a0fb2d2be39f06d63542b9728244f3a0fcd7a5e728c4dc1cf59e85",
            "6ad3cc2961699e329885f47675e8e1e34b462ab8f4dbadb56e0f5acac4331852", UnitySha256)
    ];
    /// <summary>tw-20261110 协议布局标识；所有已登记构建的绑定、战斗来源与库存来源共用此值。</summary>
    public static string GameBuild { get; } = "tw-20261110:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(
        string.Join(":", Tw20261110CombatProtocol.AssemblySha256, Tw20261110CombatProtocol.MetadataSha256, UnitySha256))));

    private static bool Hex64(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    /// <summary>未登记构建的标识：<c>sha256:</c> 加 GameAssembly 与 metadata 哈希串联后的 SHA-256；与库存布局来源的构建标识一致。</summary>
    public static string AdaptiveBuild(string assemblySha256, string metadataSha256)
    {
        if (!Hex64(assemblySha256) || !Hex64(metadataSha256)) throw new ArgumentException("客户端构建指纹必须为 SHA-256");
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(
            assemblySha256.ToLowerInvariant() + ":" + metadataSha256.ToLowerInvariant())));
    }

    /// <summary>构建标识是否为未登记构建（<see cref="AdaptiveBuild"/> 的格式）。</summary>
    public static bool IsAdaptive(string? build) => build is { Length: 71 } && build.StartsWith("sha256:", StringComparison.Ordinal) &&
        build.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    public static TwRegisteredBuild? Registered(string assemblySha256, string metadataSha256, string? unitySha256 = null) =>
        RegisteredBuilds.FirstOrDefault(build => build.AssemblySha256.Equals(assemblySha256, StringComparison.OrdinalIgnoreCase) &&
            build.MetadataSha256.Equals(metadataSha256, StringComparison.OrdinalIgnoreCase) &&
            (unitySha256 is null || build.UnitySha256.Equals(unitySha256, StringComparison.OrdinalIgnoreCase)));

    /// <summary>三文件哈希对应的构建标识：已登记构建为 <see cref="GameBuild"/>，其余为 <see cref="AdaptiveBuild"/>。</summary>
    public static string BuildId(TwClientHashes hashes) => Registered(hashes.AssemblySha256, hashes.MetadataSha256, hashes.UnitySha256) is not null
        ? GameBuild : AdaptiveBuild(hashes.AssemblySha256, hashes.MetadataSha256);

    public static string GameBuildForInventoryLayout(string assemblySha256, string metadataSha256) =>
        Registered(assemblySha256, metadataSha256) is not null ? GameBuild : AdaptiveBuild(assemblySha256, metadataSha256);

    /// <summary>三文件哈希与某个已登记构建一致。</summary>
    public static async Task<bool> MatchesAsync(string imagePath, CancellationToken ct) =>
        await HashAsync(imagePath, ct).ConfigureAwait(false) is { } hashes &&
        Registered(hashes.AssemblySha256, hashes.MetadataSha256, hashes.UnitySha256) is not null;

    public static async Task<bool> MatchesFilesAsync(string imagePath, IReadOnlyDictionary<string, string> expectedHashes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedHashes);
        ct.ThrowIfCancellationRequested();
        if (expectedHashes.Count != Files.Length || Files.Any(key => !expectedHashes.ContainsKey(key)) ||
            expectedHashes.Values.Any(value => !Hex64(value))) return false;
        return await HashAsync(imagePath, ct).ConfigureAwait(false) is { } actual &&
            actual.AssemblySha256.Equals(expectedHashes[AssemblyPath], StringComparison.OrdinalIgnoreCase) &&
            actual.MetadataSha256.Equals(expectedHashes[MetadataPath], StringComparison.OrdinalIgnoreCase) &&
            actual.UnitySha256.Equals(expectedHashes[UnityPath], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>计算三文件哈希；哈希期间持有只读共享句柄，前后文件戳一致才返回，文件缺失、被写入或含链接时为空。</summary>
    public static async Task<TwClientHashes?> HashAsync(string imagePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var streams = new List<FileStream>();
        try
        {
            var before = ReadFileStamps(imagePath);
            if (before is null) return null;
            var directory = Path.GetDirectoryName(Path.GetFullPath(imagePath))!;
            // 同时持有只读共享句柄，Windows 在整个哈希窗口拒绝写入、替换及删除。
            foreach (var relative in Files)
                streams.Add(new FileStream(Path.Combine(directory, relative), FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
            var hashes = new string[streams.Count];
            for (var index = 0; index < streams.Count; index++)
                hashes[index] = Convert.ToHexStringLower(await SHA256.HashDataAsync(streams[index], ct).ConfigureAwait(false));
            var after = ReadFileStamps(imagePath);
            return after is not null && before.SequenceEqual(after) ? new(hashes[0], hashes[1], hashes[2]) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return null; }
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
            foreach (var relative in new[] { ImageName }.Concat(Files))
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
