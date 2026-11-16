using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public static class FileNames
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static string Validate(string name)
    {
        if (name is null || name.Length is < 1 or > 128 || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') || name.Any(char.IsControl) ||
            name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            Regex.IsMatch(name, @"\A(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(\.|\z)", RegexOptions.IgnoreCase))
            throw new OperationException("storage.pathRejected", "文件名包含越界、保留名或不支持的字符");
        try { _ = Utf8.GetByteCount(name); }
        catch (EncoderFallbackException) { throw new OperationException("storage.pathRejected", "文件名不是有效 Unicode"); }
        return name;
    }
    public static string ArchivePath(string path)
    {
        if (path.Length is < 1 or > 1024 || path.StartsWith('/') || path.Contains('\\'))
            throw new OperationException("storage.pathRejected", "压缩包路径必须为有界相对路径");
        var parts = path.TrimEnd('/').Split('/');
        foreach (var part in parts) Validate(part);
        return string.Join('/', parts.Select(x => x.Normalize(NormalizationForm.FormC)));
    }
}

public static class FilePackageReader
{
    public const long MaximumUploadBytes = 256 * 1024 * 1024, MaximumExpandedBytes = 1024 * 1024 * 1024;
    public const int MaximumEntries = 10000;
    public static async Task<IReadOnlyList<ImportSourceFile>> ReadAsync(Stream stream, int parserBudget, CancellationToken ct)
    {
        try
        {
            var directory = await PreflightAsync(stream, ct);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
            if (archive.Entries.Count > MaximumEntries) throw Limit();
            if (archive.Entries.Count != directory.Entries.Count) throw Damaged();
            long expanded = 0;
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<(ZipArchiveEntry Entry, EntryHeader Header)>();
            for (var index = 0; index < archive.Entries.Count; index++)
            {
                var entry = archive.Entries[index];
                ct.ThrowIfCancellationRequested();
                var path = FileNames.ArchivePath(entry.FullName);
                var rawParts = entry.FullName.TrimEnd('/').Split('/');
                for (var i = 1; i <= rawParts.Length; i++)
                {
                    var rawPrefix = string.Join('/', rawParts.Take(i));
                    var canonical = rawPrefix.Normalize(NormalizationForm.FormC);
                    if (prefixes.TryGetValue(canonical, out var existing) && existing != rawPrefix)
                        throw new OperationException("storage.pathRejected", "压缩包目录存在大小写或 Unicode 规范化冲突");
                    prefixes.TryAdd(canonical, rawPrefix);
                }
                var kind = (entry.ExternalAttributes >> 16) & 0xF000;
                if (kind is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & 0x400) != 0)
                    throw new OperationException("storage.pathRejected", "压缩包不接受链接或特殊文件");
                if (!names.TryAdd(path, entry.FullName)) throw new OperationException("storage.pathRejected", "压缩包含重复、大小写或 Unicode 规范化冲突路径");
                if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - expanded) throw Limit();
                expanded += entry.Length;
                if (entry.FullName.EndsWith('/')) { if (entry.Length != 0) throw Limit(); continue; }
                files.Add((entry, directory.Entries[index]));
            }
            foreach (var path in names.Keys)
            {
                var parts = path.Split('/');
                for (var i = 1; i < parts.Length; i++)
                {
                    var parent = string.Join('/', parts.Take(i));
                    if (names.TryGetValue(parent, out var original) && !original.EndsWith('/'))
                        throw new OperationException("storage.pathRejected", "压缩包的文件与目录路径冲突");
                }
            }
            if (expanded > parserBudget) throw new OperationException("request.bodyTooLarge", "压缩包展开内容超过所属模块解析预算");
            var result = new List<ImportSourceFile>(); long actual = 0;
            foreach (var (entry, header) in files)
            {
                await ValidateLocalAsync(stream, entry, header, directory.Start, ct);
                using var encoded = new EncodedEntryStream(stream, entry.CompressedLength);
                await using Stream input = header.Method == 0 ? encoded : new DeflateStream(encoded, CompressionMode.Decompress, true);
                using var output = new MemoryStream();
                var block = new byte[65536]; int count; uint crc = uint.MaxValue;
                while ((count = await input.ReadAsync(block, ct)) != 0)
                {
                    if (count > MaximumExpandedBytes - actual || count > parserBudget - actual ||
                        output.Length + count > entry.Length) throw Limit();
                    actual += count;
                    for (var i = 0; i < count; i++) crc = CrcTable[(crc ^ block[i]) & 255] ^ (crc >> 8);
                    await output.WriteAsync(block.AsMemory(0, count), ct);
                }
                if (output.Length != entry.Length || ~crc != header.Crc) throw Damaged();
                result.Add(new(entry.FullName, Convert.ToBase64String(output.ToArray())));
            }
            return result;
        }
        catch (InvalidDataException) { throw new OperationException("storage.assetDamaged", "压缩包结构或内容损坏"); }
    }
    private sealed record EntryHeader(long Offset, ushort Method, ushort Flags, uint Crc, byte[] Name);
    private sealed record ArchiveDirectory(long Start, IReadOnlyList<EntryHeader> Entries);
    private static async Task<ArchiveDirectory> PreflightAsync(Stream stream, CancellationToken ct)
    {
        if (!stream.CanSeek || stream.Length < 22) throw new OperationException("storage.assetDamaged", "压缩包缺少完整目录");
        if (stream.Length > MaximumUploadBytes) throw Limit();
        var tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length; await stream.ReadExactlyAsync(tail, ct);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50 &&
                i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length) { end = i; break; }
        if (end < 0 || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 4)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 6)) != 0)
            throw new OperationException("storage.assetDamaged", "压缩包目录或磁盘标识无效");
        var endPosition = stream.Length - tail.Length + end;
        ulong count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        ulong directoryBytes = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        ulong directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (count == ushort.MaxValue || directoryBytes == uint.MaxValue || directoryOffset == uint.MaxValue)
        {
            if (endPosition < 20) throw new OperationException("storage.assetDamaged", "ZIP64 目录定位无效");
            var locator = new byte[20]; stream.Position = endPosition - 20; await stream.ReadExactlyAsync(locator, ct);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
                throw new OperationException("storage.assetDamaged", "ZIP64 磁盘定位无效");
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (offset > (ulong)stream.Length - 56) throw new OperationException("storage.assetDamaged", "ZIP64 目录范围无效");
            var record = new byte[56]; stream.Position = (long)offset; await stream.ReadExactlyAsync(record, ct);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50 ||
                BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(4)) is < 44 or > 4096 ||
                BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20)) != 0)
                throw new OperationException("storage.assetDamaged", "ZIP64 目录记录无效");
            count = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
            if (count != BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24))) throw new OperationException("storage.assetDamaged", "ZIP64 条目数量不一致");
            directoryBytes = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
            directoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
        }
        else if (count != BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 8)))
            throw new OperationException("storage.assetDamaged", "压缩包条目数量不一致");
        if (count > MaximumEntries || directoryBytes > 16 * 1024 * 1024) throw Limit();
        if (directoryOffset > (ulong)stream.Length || directoryBytes > (ulong)stream.Length - directoryOffset ||
            directoryOffset + directoryBytes > (ulong)endPosition)
            throw new OperationException("storage.assetDamaged", "压缩包目录越过文件范围");
        stream.Position = (long)directoryOffset; var directoryEnd = (long)(directoryOffset + directoryBytes);
        var header = new byte[46]; ulong actual = 0;
        var entries = new List<EntryHeader>();
        while (stream.Position < directoryEnd)
        {
            if (++actual > MaximumEntries) throw Limit();
            if (directoryEnd - stream.Position < header.Length) throw new OperationException("storage.assetDamaged", "压缩包目录截断");
            await stream.ReadExactlyAsync(header, ct);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
                throw new OperationException("storage.assetDamaged", "压缩包目录条目无效");
            var name = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            var comment = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            if (name is 0 or > 4096 || extra > 4096 || comment > 1024) throw Limit();
            var remainder = name + extra + comment;
            if (remainder > directoryEnd - stream.Position) throw new OperationException("storage.assetDamaged", "压缩包目录属性截断");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
            if ((flags & 0x2041) != 0 || method is not (0 or 8) || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) != 0)
                throw new OperationException("storage.assetInvalid", "压缩包仅支持未加密的 stored/deflate 单磁盘文件");
            var path = new byte[name]; await stream.ReadExactlyAsync(path, ct);
            var properties = new byte[extra]; await stream.ReadExactlyAsync(properties, ct);
            ulong localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(42));
            if (localOffset == uint.MaxValue)
            {
                var skip = (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) == uint.MaxValue ? 8 : 0)
                    + (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) == uint.MaxValue ? 8 : 0);
                var found = false;
                for (var p = 0; p + 4 <= properties.Length;)
                {
                    var tag = BinaryPrimitives.ReadUInt16LittleEndian(properties.AsSpan(p));
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(properties.AsSpan(p + 2));
                    if (length > properties.Length - p - 4) throw Damaged();
                    if (tag == 1)
                    {
                        if (length < skip + 8) throw Damaged();
                        localOffset = BinaryPrimitives.ReadUInt64LittleEndian(properties.AsSpan(p + 4 + skip)); found = true; break;
                    }
                    p += 4 + length;
                }
                if (!found) throw Damaged();
            }
            if (localOffset > directoryOffset || directoryOffset - localOffset < 30) throw Damaged();
            entries.Add(new((long)localOffset, method, flags, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)), path));
            stream.Position += comment;
        }
        if (actual != count) throw new OperationException("storage.assetDamaged", "压缩包实际条目数量不一致");
        stream.Position = 0;
        return new((long)directoryOffset, entries);
    }
    private static async Task ValidateLocalAsync(Stream stream, ZipArchiveEntry entry, EntryHeader expected, long directoryStart, CancellationToken ct)
    {
        var header = new byte[30]; stream.Position = expected.Offset; await stream.ReadExactlyAsync(header, ct);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)) != expected.Flags ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) != expected.Method) throw Damaged();
        var nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        var extraBytes = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        if (nameBytes != expected.Name.Length || extraBytes > 4096 || nameBytes + extraBytes > directoryStart - stream.Position) throw Damaged();
        var path = new byte[nameBytes]; await stream.ReadExactlyAsync(path, ct);
        if (!path.AsSpan().SequenceEqual(expected.Name)) throw Damaged();
        stream.Position += extraBytes;
        if (entry.CompressedLength < 0 || entry.CompressedLength > directoryStart - stream.Position) throw Damaged();
        // 解压按压缩字节范围读取，实际输出不得被目录中的展开长度截断。
    }
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();
    private sealed class EncodedEntryStream : Stream
    {
        private readonly Stream source;
        private readonly long length;
        private long remaining;
        public EncodedEntryStream(Stream source, long length) { this.source = source; this.length = length; remaining = length; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => length - remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = source.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], ct); remaining -= read; return read; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static OperationException Damaged() => new("storage.assetDamaged", "压缩包目录、内容长度或 CRC 不匹配");
    private static OperationException Limit() => new("storage.archiveLimitExceeded", "压缩包文件数或展开量超过预算");
}
