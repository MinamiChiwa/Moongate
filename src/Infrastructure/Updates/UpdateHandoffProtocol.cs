using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Infrastructure.Updates;

internal sealed record UpdateHandoffEnvelope(int ProtocolVersion, string SessionId, string TransactionId,
    int Sequence, string Kind, JsonElement Payload);

internal static class UpdateHandoffProtocol
{
    internal const int Version = 1;
    internal const int MaximumFrameBytes = 1_048_576;
    internal const int MaximumMessages = 64;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 24, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static bool Id(string? value) => value is { Length: 32 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool Kind(string? value) => value is "intent" or "accepted" or "release" or "released" or
        "preflight" or "preflightResult" or "preflightCompleted" or
        "offer" or "healthCheck" or "healthResult" or "commit" or "committed" or
        "rollbackHealth" or "rollbackHealthResult" or "rollbackHealthCompleted" or
        "updaterReady" or "updaterAccepted" or "updaterResume" or "updaterResumed" or "abort";
    internal static UpdatePlanException Invalid() => new("update.handoffInvalid");

    internal static async Task<UpdateHandoffEnvelope> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaximumFrameBytes) throw Invalid();
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        try
        {
            using var document = StrictJson.Parse(body, MaximumFrameBytes);
            var envelope = document.RootElement.Deserialize<UpdateHandoffEnvelope>(Json) ?? throw Invalid();
            Validate(envelope);
            return envelope;
        }
        catch (Exception error) when (error is JsonException or TrustValidationException) { throw Invalid(); }
    }

    internal static async Task WriteAsync(Stream stream, UpdateHandoffEnvelope envelope, CancellationToken token)
    {
        Validate(envelope);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        if (body.Length > MaximumFrameBytes) throw Invalid();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static void Validate(UpdateHandoffEnvelope envelope)
    {
        if (envelope is null || envelope.ProtocolVersion != Version || !Id(envelope.SessionId) || !Id(envelope.TransactionId) ||
            envelope.Sequence is < 1 or > MaximumMessages || !Kind(envelope.Kind) || envelope.Payload.ValueKind != JsonValueKind.Object)
            throw Invalid();
    }
}
