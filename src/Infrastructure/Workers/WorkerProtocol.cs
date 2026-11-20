using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MabiTool.Infrastructure.Workers;

public sealed class WorkerException(string code, Exception? innerException = null) : Exception(code, innerException)
{
    public string Code { get; } = code;
}

public sealed record WorkerFrame(int ProtocolVersion, string MessageId, string? RequestId, string Kind,
    string? RunId, string? BindingRevision, string? FencingToken, JsonElement Payload);

public sealed record WorkerAssetReference(string AssetId, string RunId, long SizeBytes,
    string Sha256, long ExpiresAtUnixMs);

public static class WorkerProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 1024 * 1024;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { MaxDepth = 24 };
    private static readonly HashSet<string> Fields = ["protocolVersion", "messageId", "requestId", "kind", "runId", "bindingRevision", "fencingToken", "payload"];
    private static readonly HashSet<string> Controls = ["hello", "ready", "ping", "pong", "shutdown", "bye"];
    private static readonly HashSet<string> Kinds = [.. Controls, "request", "accepted", "event", "result", "error", "cancel", "cancelled", "portRequest", "portResult", "portError", "command", "commandResult"];

    public static bool IsId(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
    public static bool IsUInt64(string? value) => value is not null && Regex.IsMatch(value, @"\A(0|[1-9][0-9]{0,19})\z", RegexOptions.CultureInvariant) &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    public static async Task<WorkerFrame> ReadAsync(Stream stream, CancellationToken token = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaximumFrameBytes) throw new WorkerException("worker.frameTooLarge");
        var body = new byte[(int)length];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        try
        {
            // 重复键在原始字节上一次检查（含信封与 payload），之后不再逐对象检查 payload。
            if (JsonDuplicateKeys.Contains(body, 24)) throw Invalid();
            using var document = JsonDocument.Parse(body, new() { MaxDepth = 24 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !Fields.SetEquals(document.RootElement.EnumerateObject().Select(p => p.Name))) throw Invalid();
            var result = document.RootElement.Deserialize<WorkerFrame>(Json) ?? throw Invalid();
            Validate(result);
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { throw Invalid(); }
    }

    public static async Task WriteAsync(Stream stream, WorkerFrame frame, CancellationToken token = default)
    {
        Validate(frame);
        var body = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        // 帧序列化保留 payload 中的每个属性（含重复的），在写出的字节上检查重复键，与逐对象检查 payload 的结果相同。
        try { if (JsonDuplicateKeys.Contains(body, JsonDuplicateKeys.UnboundedDepth)) throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
        if (body.Length > MaximumFrameBytes) throw new WorkerException("worker.frameTooLarge");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
    /// <summary>
    /// 写出 payload 为已编码 UTF-8 JSON 对象的一帧，与把该编码解析成 JsonElement 后经 <see cref="WriteAsync(Stream, WorkerFrame, CancellationToken)"/>
    /// 写出的字节相同（payload 为契约序列化选项编出的规范编码时）：信封按 <paramref name="envelope"/> 的字段编码（其 Payload 不使用），
    /// payload 校验为对象、JSON 有效、无重复键且帧深度不超过 24 层后原样拼入，不再解析与重新编码。
    /// </summary>
    public static async Task WriteAsync(Stream stream, WorkerFrame envelope, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        envelope = envelope with { Payload = EmptyObject };
        Validate(envelope);
        var head = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        // 信封编码以 "payload":{}} 结尾：去掉末尾的 {}}，接上 payload 与右括号。
        if (!head.AsSpan().EndsWith("{}}"u8)) throw new InvalidOperationException("帧信封编码格式不符");
        bool duplicate;
        try { duplicate = JsonDuplicateKeys.Contains(payload.Span, 23, requireObject: true); }
        catch (JsonException) { throw Invalid(); }
        if (duplicate) throw Invalid();
        var prefix = head.Length - 3;
        var length = prefix + payload.Length + 1;
        if (length > MaximumFrameBytes) throw new WorkerException("worker.frameTooLarge");
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4 + length);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)length);
            head.AsSpan(0, prefix).CopyTo(buffer.AsSpan(4));
            payload.Span.CopyTo(buffer.AsSpan(4 + prefix));
            buffer[3 + length] = (byte)'}';
            await stream.WriteAsync(buffer.AsMemory(0, 4 + length), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void Validate(WorkerFrame frame)
    {
        if (frame.ProtocolVersion != Version || !IsUInt64(frame.MessageId) || frame.MessageId == "0" ||
            !Kinds.Contains(frame.Kind) || frame.Payload.ValueKind != JsonValueKind.Object) throw Invalid();
        if (Controls.Contains(frame.Kind))
        {
            if (frame.RequestId is not null || frame.RunId is not null || frame.BindingRevision is not null || frame.FencingToken is not null) throw Invalid();
        }
        else if (!IsId(frame.RequestId) || !IsId(frame.RunId) || !IsUInt64(frame.BindingRevision) || !IsUInt64(frame.FencingToken)) throw Invalid();
    }

    private static WorkerException Invalid() => new("worker.frameInvalid");
}
