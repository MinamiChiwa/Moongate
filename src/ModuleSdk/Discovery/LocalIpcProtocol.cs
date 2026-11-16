using System.Buffers.Binary;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public static class LocalIpcProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 16 * 1024;

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("本机 IPC 帧大小无效");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return JsonSerializer.Deserialize<T>(body, ContractJson.Options) ?? throw new JsonException("本机 IPC 内容为空");
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options);
        if (body.Length > MaximumFrameBytes) throw new InvalidDataException("本机 IPC 帧过大");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
