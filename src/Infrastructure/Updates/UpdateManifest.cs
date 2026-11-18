using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.PortableUpdate;

namespace MabiTool.Infrastructure.Updates;

/// <summary>
/// 清单里的一个程序文件：相对包根的路径（<c>/</c> 分隔）、字节数与小写 SHA-256。schema 2 另记它在完整包 zip 里的数据区：
/// 起始偏移 <c>DataOffset</c>、压缩后长度 <c>CompressedSize</c>、压缩方式 <c>Compression</c>（<c>stored</c>／<c>deflate</c>）与 CRC-32。
/// </summary>
public sealed record UpdateManifestFile(string Path, long Size, string Sha256, long? DataOffset = null, long? CompressedSize = null,
    string? Compression = null, uint? Crc32 = null);
public sealed record UpdateManifestPackage(string Name, long Size, string Sha256);
/// <summary>从 <see cref="FromVersion"/> 出发的增量包：所含内容按 SHA-256 命名，<see cref="Blobs"/> 列出全部内容哈希。</summary>
public sealed record UpdateManifestDelta(string FromVersion, string Name, long Size, string Sha256, IReadOnlyList<string> Blobs);

/// <summary>
/// 发行的文件清单 <c>&lt;完整包名&gt;.manifest.json</c>（由 <c>scripts/build/package-update-assets.py</c> 生成）：
/// 新版全部程序文件、完整包与各增量包。路径首段只能是 <c>Moongate.exe</c>、<c>components</c>、<c>licenses</c>。
/// schema 1 带旧式增量包（供 0.1.2／0.1.3 使用），schema 2 为每个文件记录完整包内的数据区，客户端按需分段下载。
/// </summary>
public sealed record UpdateManifest(int SchemaVersion, string Product, string DisplayVersion, UpdateManifestPackage Package,
    IReadOnlyList<UpdateManifestFile> Files, IReadOnlyList<UpdateManifestDelta> Deltas)
{
    private const long MaximumFileBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumFiles = 100_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>
    /// 解析并核对清单：版本与完整包名须与发行一致，完整包哈希须等于 <c>.sha256</c>；路径、大小与哈希逐项校验。
    /// 不符时抛出 <c>update.manifestInvalid</c>。
    /// </summary>
    public static UpdateManifest Parse(ReadOnlySpan<byte> bytes, string displayVersion, string assetName, string packageSha256)
    {
        UpdateManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json); }
        catch (JsonException) { throw Invalid(); }
        if (manifest is not { SchemaVersion: 1 or 2, Product: "Moongate", Package: { } package, Files: { } files, Deltas: { } deltas } ||
            manifest.DisplayVersion != displayVersion || package.Name != assetName || package.Sha256 != packageSha256 ||
            package.Size is < 1 or > PortableUpdatePackage.MaximumArchiveBytes || files.Count is 0 or > MaximumFiles || deltas.Count > 16)
            throw Invalid();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || file.Size is < 0 or > MaximumFileBytes || !ReleaseIdentity.IsHash(file.Sha256) || !IsProgramPath(file.Path) ||
                !paths.Add(file.Path)) throw Invalid();
            var located = file.DataOffset is not null || file.CompressedSize is not null || file.Compression is not null || file.Crc32 is not null;
            if (manifest.SchemaVersion == 1 ? located :
                file.DataOffset is not { } offset || file.CompressedSize is not { } compressed || file.Crc32 is null ||
                offset < 0 || compressed < 0 || offset + compressed > package.Size || file.Compression is not ("stored" or "deflate") ||
                file.Compression == "stored" && compressed != file.Size)
                throw Invalid();
        }
        // 某个文件的路径不能是另一个文件的目录。
        foreach (var path in paths)
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (paths.Contains(path[..slash])) throw Invalid();
        if (!paths.Contains(ReleaseIdentity.EntryName) || !paths.Contains(ReleaseIdentity.RelativePath)) throw Invalid();
        var hashes = files.Select(file => file.Sha256).ToHashSet(StringComparer.Ordinal);
        foreach (var delta in deltas)
        {
            if (delta is null || !IsDisplayVersion(delta.FromVersion) || delta.Name != assetName + ".update-from-" + delta.FromVersion + ".zip" ||
                delta.Size is < 1 or > PortableUpdatePackage.MaximumArchiveBytes || !ReleaseIdentity.IsHash(delta.Sha256) || delta.Blobs is null ||
                delta.Blobs.Count > hashes.Count || delta.Blobs.Any(blob => !hashes.Contains(blob)) ||
                delta.Blobs.Distinct(StringComparer.Ordinal).Count() != delta.Blobs.Count) throw Invalid();
        }
        if (deltas.Select(delta => delta.FromVersion).Distinct(StringComparer.Ordinal).Count() != deltas.Count) throw Invalid();
        return manifest;
    }

    /// <summary>新版全部文件的字节数之和，用于估算暂存所需空间。</summary>
    public long TotalBytes => Files.Sum(file => file.Size);

    /// <summary>schema 2：每个文件都记有完整包内的数据区，可以按需分段下载。</summary>
    public bool Locates => SchemaVersion == 2;

    private static bool IsProgramPath(string? path)
    {
        if (path is null) return false;
        try { if (UpdatePaths.Normalize(path) != path) return false; }
        catch (UpdatePackageException) { return false; }
        return path == ReleaseIdentity.EntryName ||
            path.StartsWith("components/", StringComparison.Ordinal) || path.StartsWith("licenses/", StringComparison.Ordinal);
    }

    private static bool IsDisplayVersion(string? value) => value is { Length: > 0 and <= 64 } &&
        System.Text.RegularExpressions.Regex.IsMatch(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview)?\z");

    private static UpdatePackageException Invalid() => new("update.manifestInvalid");
}

//// <summary>完整包里一段连续的字节区间，含若干缺失文件的数据区。</summary>
public sealed record UpdateRangeSegment(long Start, long Length)
{
    public long End => Start + Length;
}

/// <summary>按需分段的选择：本机缺的文件、合并后的区间，以及复用、下载与删除的文件数。</summary>
public sealed record UpdateRangeChoice(IReadOnlyList<UpdateManifestFile> Missing, IReadOnlyList<UpdateRangeSegment> Segments,
    int ReusedFiles, int DownloadedFiles, int DeletedFiles)
{
    public long DownloadBytes => Segments.Sum(segment => segment.Length);
}

/// <summary>
/// 增量更新（按需分段）：检查时比对本机程序文件与新版清单，得到本机缺的文件，按它们在完整包里的数据区合并成少数几个区间；
/// 下载后用本机文件与区间里解压出的内容在暂存目录构建新包根，按清单逐文件核对后再核对发行身份。任一环节不满足时由调用方改用完整包。
/// </summary>
public static class DeltaUpdate
{
    /// <summary>相邻两段的间隔小于这个值时合并为一个区间，减少请求次数。</summary>
    public const long MergeGap = 64 * 1024;
    /// <summary>需要下载的字节超过完整包的这个比例时直接下载完整包。</summary>
    public const double FullPackageShare = 0.8;

    /// <summary>
    /// 清单记有数据区（schema 2）时给出本机缺的文件与合并后的区间；清单没有数据区或需要下载的字节超过完整包的 80% 时返回空。
    /// </summary>
    public static async Task<UpdateRangeChoice?> ChooseAsync(UpdateManifest manifest, string packageRoot, UpdateIoBudget budget,
        CancellationToken token = default)
    {
        if (!manifest.Locates) return null;
        var local = await HashLocalAsync(packageRoot, budget, token).ConfigureAwait(false);
        var available = local.Values.ToHashSet(StringComparer.Ordinal);
        var missing = manifest.Files.Where(file => !available.Contains(file.Sha256)).ToArray();
        var segments = Segments(missing.DistinctBy(file => file.Sha256));
        if (segments.Sum(segment => segment.Length) > manifest.Package.Size * FullPackageShare) return null;
        var targets = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(missing, segments, manifest.Files.Count - missing.Length, missing.Length, local.Keys.Count(path => !targets.Contains(path)));
    }

    /// <summary>按数据区偏移排序，间隔小于 <see cref="MergeGap"/> 的相邻段合并。</summary>
    public static IReadOnlyList<UpdateRangeSegment> Segments(IEnumerable<UpdateManifestFile> files)
    {
        var result = new List<UpdateRangeSegment>();
        foreach (var file in files.Where(file => file.CompressedSize > 0).OrderBy(file => file.DataOffset))
        {
            var start = file.DataOffset!.Value;
            var end = start + file.CompressedSize!.Value;
            if (result.Count > 0 && start - result[^1].End < MergeGap)
                result[^1] = result[^1] with { Length = Math.Max(result[^1].End, end) - result[^1].Start };
            else result.Add(new(start, end - start));
        }
        return result;
    }

    /// <summary>
    /// 在 <c>update/staging/&lt;事务&gt;.work/Moongate</c> 构建新包根：本机有相同内容的复制并核对，其余由 <paramref name="content"/>
    /// 给出解压后的内容并核对（给不出时 <c>update.deltaIncomplete</c>，哈希不符或解压失败时 <c>update.assetHashMismatch</c>）；
    /// 完成后重新枚举，文件集合与每个文件的哈希须与清单完全一致，再核对发行身份，通过后改名为 <c>update/staging/&lt;事务&gt;</c>。
    /// </summary>
    public static async Task<string> StageAsync(UpdateManifest manifest, Func<UpdateManifestFile, Stream?> content, string packageRoot,
        string toolRoot, string transactionId, UpdateIoBudget budget, CancellationToken token = default)
    {
        if (!UpdateLayout.IsTransactionId(transactionId)) throw new UpdatePackageException("update.planInvalid");
        var staging = UpdateLayout.Staging(toolRoot, transactionId);
        var work = staging + ".work";
        UpdatePaths.RejectLinks(staging);
        UpdatePaths.DeleteOwnedTree(work);
        UpdatePaths.DeleteOwnedTree(staging);
        var target = Path.Combine(work, "Moongate");
        Directory.CreateDirectory(target);
        try
        {
            var local = await HashLocalAsync(packageRoot, budget, token).ConfigureAwait(false);
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (relative, hash) in local) sources.TryAdd(hash, LocalPath(packageRoot, relative));
            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                var output = UpdatePaths.Local(target, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                if (sources.TryGetValue(file.Sha256, out var source) && await CopyVerifiedAsync(source, output, file, budget, token).ConfigureAwait(false))
                    continue;
                await using var input = content(file) ?? throw new UpdatePackageException("update.deltaIncomplete", file.Path);
                if (!await WriteVerifiedAsync(input, output, file, budget, token).ConfigureAwait(false))
                    throw new UpdatePackageException("update.assetHashMismatch", file.Path);
            }
            await VerifyTreeAsync(target, manifest, budget, token).ConfigureAwait(false);
            ReleaseIdentity? identity;
            string? mismatch;
            try { identity = ReleaseIdentity.Read(target); mismatch = identity?.Verify(target); }
            catch (InvalidDataException error) { throw new UpdatePackageException("update.packageInvalid", error.Message); }
            if (identity is null) throw new UpdatePackageException("update.packageInvalid", "缺少发行身份");
            if (identity.DisplayVersion != manifest.DisplayVersion) throw new UpdatePackageException("update.releaseIdentityMismatch");
            if (mismatch is not null) throw new UpdatePackageException("update.packageInvalid", "与发行身份不符：" + mismatch);
            Directory.Move(work, staging);
            return UpdateLayout.StagedPackage(toolRoot, transactionId);
        }
        catch (InvalidDataException error) { throw new UpdatePackageException("update.assetHashMismatch", error.Message); }
        catch (IOException error) when (UpdatePaths.IsDiskFull(error)) { throw new UpdatePackageException("update.diskSpaceInsufficient"); }
        finally { if (Directory.Exists(work)) UpdatePaths.DeleteOwnedTree(work); }
    }

    /// <summary>从已下载的区间文件取出一个文件解压后的内容；文件的数据区须整段落在某个区间内，否则返回空。</summary>
    public static Stream? OpenFromSegments(UpdateManifestFile file, IReadOnlyList<(UpdateRangeSegment Segment, string Path)> segments)
    {
        if (file.DataOffset is not { } offset || file.CompressedSize is not { } length) return null;
        if (length == 0) return file.Compression == "deflate" ? new DeflateStream(new MemoryStream(), CompressionMode.Decompress) : new MemoryStream();
        foreach (var (segment, path) in segments)
        {
            if (offset < segment.Start || offset + length > segment.End) continue;
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, UpdateIoBudget.ChunkSize, FileOptions.SequentialScan);
            if (stream.Length != segment.Length) { stream.Dispose(); return null; }
            var slice = new SliceStream(stream, offset - segment.Start, length);
            return file.Compression == "deflate" ? new DeflateStream(slice, CompressionMode.Decompress) : slice;
        }
        return null;
    }

    /// <summary>只读的字节区间视图，释放时一并释放底层流。</summary>
    private sealed class SliceStream : Stream
    {
        private readonly Stream inner;
        private long remaining;

        public SliceStream(Stream inner, long start, long length)
        {
            this.inner = inner;
            remaining = length;
            inner.Position = start;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (remaining <= 0) return 0;
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            remaining -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (remaining <= 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], token).ConfigureAwait(false);
            remaining -= read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>暂存目录的文件集合与清单完全一致，每个文件的字节数与 SHA-256 等于清单记录；否则 <c>update.targetTreeMismatch</c>。</summary>
    internal static async Task VerifyTreeAsync(string root, UpdateManifest manifest, UpdateIoBudget budget, CancellationToken token)
    {
        var actual = await HashTreeAsync(root, budget, token).ConfigureAwait(false);
        if (actual.Count != manifest.Files.Count || manifest.Files.Any(file =>
                !actual.TryGetValue(file.Path, out var found) || found.Hash != file.Sha256 || found.Size != file.Size))
            throw new UpdatePackageException("update.targetTreeMismatch");
    }

    /// <summary>本机包根的程序文件（<c>Moongate.exe</c>、<c>components/</c>、<c>licenses/</c>）：相对路径 → SHA-256。</summary>
    private static async Task<Dictionary<string, string>> HashLocalAsync(string packageRoot, UpdateIoBudget budget, CancellationToken token) =>
        (await HashTreeAsync(packageRoot, budget, token, programOnly: true).ConfigureAwait(false))
            .ToDictionary(item => item.Key, item => item.Value.Hash, StringComparer.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, (long Size, string Hash)>> HashTreeAsync(string root, UpdateIoBudget budget,
        CancellationToken token, bool programOnly = false)
    {
        var files = new List<(string Relative, string Full)>();
        var entry = Path.Combine(root, ReleaseIdentity.EntryName);
        if (File.Exists(entry)) files.Add((ReleaseIdentity.EntryName, entry));
        foreach (var directory in new[] { "components", "licenses" })
            foreach (var (relative, full) in UpdatePaths.EnumerateFiles(Path.Combine(root, directory), token))
                files.Add((directory + "/" + relative, full));
        if (!programOnly)
        {
            // 暂存目录里不能有三项之外的条目。
            var extra = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)
                .Where(name => name is not (ReleaseIdentity.EntryName or "components" or "licenses"));
            if (extra.Any()) throw new UpdatePackageException("update.targetTreeMismatch");
        }
        var result = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, full) in files)
        {
            UpdatePaths.RejectLinks(full);
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, UpdateIoBudget.ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var content = await budget.CopyAndHashAsync(stream, null, long.MaxValue, token).ConfigureAwait(false);
            if (!result.TryAdd(relative, content)) throw new UpdatePackageException("update.targetTreeMismatch", relative);
        }
        return result;
    }

    private static string LocalPath(string packageRoot, string relative) => UpdatePaths.Local(packageRoot, relative);

    // 本机文件在检查后被改动或被占用时返回 false，由调用方改从下载的内容取得。
    private static async Task<bool> CopyVerifiedAsync(string source, string output, UpdateManifestFile file, UpdateIoBudget budget, CancellationToken token)
    {
        try
        {
            UpdatePaths.RejectLinks(source);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, UpdateIoBudget.ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != file.Size) return false;
            return await WriteVerifiedAsync(input, output, file, budget, token).ConfigureAwait(false);
        }
        catch (IOException error) when (!UpdatePaths.IsDiskFull(error)) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<bool> WriteVerifiedAsync(Stream input, string output, UpdateManifestFile file, UpdateIoBudget budget, CancellationToken token)
    {
        (long Length, string Hash) content;
        try
        {
            await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, UpdateIoBudget.ChunkSize, FileOptions.Asynchronous);
            content = await budget.CopyAndHashAsync(input, stream, file.Size, token).ConfigureAwait(false);
        }
        catch (UpdatePackageException error) when (error.Code == "update.assetLengthMismatch") { content = (-1, ""); }
        if (content.Length == file.Size && content.Hash == file.Sha256) return true;
        File.Delete(output);
        return false;
    }
}
