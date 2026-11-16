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
            using var document = JsonDocument.Parse(body, new() { MaxDepth = 24 });
            Unique(document.RootElement);
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
        if (body.Length > MaximumFrameBytes) throw new WorkerException("worker.frameTooLarge");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
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
        Unique(frame.Payload);
    }

    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                Unique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) Unique(value);
    }

    private static WorkerException Invalid() => new("worker.frameInvalid");
}
