using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public static class ImportSnapshotCodec
{
    public static string Pack<T>(T value, int maximumBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        if (bytes.Length > maximumBytes) throw new OperationException("resource.capacityExceeded", "迁移快照超过字节预算");
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, true)) compressor.Write(bytes);
        return "br1:" + Convert.ToHexString(SHA256.HashData(bytes)) + ":" + Convert.ToBase64String(output.ToArray());
    }
    public static T Read<T>(string packed, int maximumBytes)
    {
        try
        {
            if (packed.Length < 69 || packed.Length > maximumBytes * 2L || !packed.StartsWith("br1:", StringComparison.Ordinal) || packed[68] != ':')
                throw new InvalidDataException();
            using var source = new MemoryStream(Convert.FromBase64String(packed[69..]));
            using var stream = new BrotliStream(source, CompressionMode.Decompress);
            using var result = new MemoryStream();
            var block = new byte[65536]; int read;
            while ((read = stream.Read(block)) > 0)
            {
                if (result.Length + read > maximumBytes) throw new InvalidDataException();
                result.Write(block, 0, read);
            }
            var bytes = result.ToArray();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(packed[4..68]))) throw new InvalidDataException();
            return JsonSerializer.Deserialize<T>(bytes, ContractJson.Options) ?? throw new InvalidDataException();
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or JsonException)
        { throw new StorageException("storage.assetDamaged", "迁移快照已损坏"); }
    }
}
