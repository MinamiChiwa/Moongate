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
                    1, FileOptions.Asynchronous | FileOptions.SequentialScan));
            var hashes = new string[streams.Count];
            for (var index = 0; index < streams.Count; index++)
                hashes[index] = await ClientFileHash.ComputeAsync(streams[index], ct).ConfigureAwait(false);
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

    /// <summary>
    /// 主程序路径经联接、符号链接或挂载点解析后的最终路径（迁移到其他盘、再用联接挂回原处的安装）。解析结果不含重解析点，
    /// 之后的文件戳与哈希仍按 <see cref="ReadFileStamps"/> 拒绝链接，解析与核对之间路径被换成链接时核对失败。
    /// 卷没有盘符时返回 <c>\\?\Volume{…}\</c> 形式。文件不存在或无法打开时为空。
    /// </summary>
    public static string? ResolveFinalPath(string imagePath)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(imagePath)) return null;
        try
        {
            using var handle = File.OpenHandle(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var kind in new[] { 0u, 1u })
            {
                var buffer = new char[32768];
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, kind);
                if (length == 0 || length >= buffer.Length) continue;
                var path = new string(buffer, 0, (int)length);
                // 盘符形式去掉 \\?\ 前缀（UNC 为 \\?\UNC\服务器\共享）；卷 GUID 形式保留前缀才是可用路径。
                if (kind == 0 && path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
                if (kind == 0 && path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
                return path;
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, [System.Runtime.InteropServices.Out] char[] path, uint length, uint flags);

    private static void RejectLinks(string path)
    {
        for (FileSystemInfo? current = new FileInfo(path); current is not null; current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("客户端路径包含链接");
    }
}

/// <summary>
/// 客户端文件的流式 SHA-256：固定 1 MiB 缓冲、可取消，不整读文件。同一进程内文件身份（卷序列号、文件 ID、大小、修改与创建时间、USN）
/// 未变时复用已算出的哈希。身份取自调用方已打开的只读共享句柄：句柄存续期间文件不能被写入、替换或删除，
/// 因此命中时的哈希就是该句柄所指内容的哈希；调用方前后文件戳一致的核对照旧执行。宿主与 Worker 各自计算，不跨进程共享。
/// </summary>
public static class ClientFileHash
{
    private const int BufferBytes = 1024 * 1024, MaximumEntries = 16;
    private sealed record Identity(uint Volume, ulong FileId, long Length, long LastWrite, long Creation, long Usn);
    private static readonly object Gate = new();
    private static readonly Dictionary<Identity, string> Known = [];
    private static readonly Queue<Identity> Order = new();

    /// <summary>从文件开头计算 <paramref name="stream"/> 的哈希（小写十六进制）。</summary>
    public static string Compute(FileStream stream, CancellationToken ct)
    {
        var before = Read(stream);
        if (Find(before) is { } known) return known;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            stream.Position = 0;
            for (int count; (count = stream.Read(buffer, 0, BufferBytes)) > 0;) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        return Remember(before, Read(stream), Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary><see cref="Compute"/> 的异步版本。</summary>
    public static async Task<string> ComputeAsync(FileStream stream, CancellationToken ct)
    {
        var before = Read(stream);
        if (Find(before) is { } known) return known;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            stream.Position = 0;
            for (int count; (count = await stream.ReadAsync(buffer.AsMemory(0, BufferBytes), ct).ConfigureAwait(false)) > 0;) hash.AppendData(buffer, 0, count);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        return Remember(before, Read(stream), Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static string? Find(Identity? identity)
    {
        if (identity is null) return null;
        lock (Gate) return Known.GetValueOrDefault(identity);
    }

    private static string Remember(Identity? before, Identity? after, string value)
    {
        if (before is null || before != after) return value;
        lock (Gate)
        {
            if (Known.TryAdd(before, value)) Order.Enqueue(before);
            while (Order.Count > MaximumEntries) Known.Remove(Order.Dequeue());
        }
        return value;
    }

    /// <summary>
    /// 文件身份。除卷、文件 ID、大小与时间外还取文件当前的 USN（NTFS 变更日志序号）：任何写入都会推进 USN，
    /// 改写内容后再把修改时间改回原值也不会命中旧哈希。读不到 USN（卷未启用变更日志或不是 NTFS）时不复用。
    /// </summary>
    private static Identity? Read(FileStream stream)
    {
        if (!OperatingSystem.IsWindows() || !GetFileInformationByHandle(stream.SafeFileHandle, out var info)) return null;
        var record = new byte[1024];
        if (!DeviceIoControl(stream.SafeFileHandle, ReadFileUsnData, IntPtr.Zero, 0, record, record.Length, out var returned, IntPtr.Zero) ||
            returned < 32 || BitConverter.ToUInt16(record, 4) != 2) return null;
        var usn = BitConverter.ToInt64(record, 24);
        return usn <= 0 ? null : new(info.VolumeSerialNumber, (ulong)info.FileIndexHigh << 32 | info.FileIndexLow, (long)info.FileSizeHigh << 32 | info.FileSizeLow,
            info.LastWriteTime, info.CreationTime, usn);
    }
    private const uint ReadFileUsnData = 0x000900eb;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, IntPtr input, int inputSize,
        [System.Runtime.InteropServices.Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>BY_HANDLE_FILE_INFORMATION；FILETIME 为两个 DWORD，按 4 字节对齐。</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct FileInformation
    {
        public uint Attributes; public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation information);
}
