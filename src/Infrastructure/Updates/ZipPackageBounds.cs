using System.Buffers.Binary;

namespace MabiTool.Infrastructure.Updates;

/// <summary>在 ZipArchive 分配目录对象前限制目录字节数和条目数，支持单卷 ZIP64。</summary>
internal static class ZipPackageBounds
{
    internal static void Validate(Stream stream, int maximumEntries)
    {
        var tail = new byte[(int)Math.Min(stream.Length, 65_557)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        var offset = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { offset = i; break; }
        if (offset < 0 || U16(tail, offset + 4) != 0 || U16(tail, offset + 6) != 0) Reject();
        ulong entries = U16(tail, offset + 10);
        ulong directoryLength = U32(tail, offset + 12);
        ulong directoryOffset = U32(tail, offset + 16);
        var directoryEnd = (ulong)(stream.Length - tail.Length + offset);
        if (entries == ushort.MaxValue || directoryLength == uint.MaxValue || directoryOffset == uint.MaxValue)
        {
            if (directoryEnd < 20) Reject();
            stream.Position = (long)directoryEnd - 20;
            var locator = new byte[20];
            stream.ReadExactly(locator);
            var zip64Offset = U64(locator, 8);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1 || zip64Offset > directoryEnd - 20 ||
                directoryEnd - 20 - zip64Offset < 56) Reject();
            stream.Position = (long)zip64Offset;
            var record = new byte[56];
            stream.ReadExactly(record);
            if (U32(record, 0) != 0x06064b50 || U64(record, 4) < 44 || U64(record, 4) != directoryEnd - 20 - zip64Offset - 12 ||
                U32(record, 16) != 0 || U32(record, 20) != 0 ||
                U64(record, 24) != U64(record, 32)) Reject();
            entries = U64(record, 32);
            directoryLength = U64(record, 40);
            directoryOffset = U64(record, 48);
            directoryEnd = zip64Offset;
        }
        else if (U16(tail, offset + 8) != entries) Reject();
        if (entries == 0 || entries > (ulong)maximumEntries || entries > 100_000 || directoryLength > 64 * 1024 * 1024 ||
            directoryOffset > directoryEnd || directoryLength != directoryEnd - directoryOffset) Reject();
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static void Reject() => throw new UpdatePackageException("update.packageInvalid");
}
