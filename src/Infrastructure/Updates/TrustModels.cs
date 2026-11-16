using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Infrastructure.Updates;

public sealed record EnvelopeSignature(string KeyId, string Algorithm, string SignatureBase64);

public sealed record SignedPayloadEnvelope(
    int SchemaVersion,
    string PayloadBase64,
    IReadOnlyList<EnvelopeSignature> Signatures)
{
    public static SignedPayloadEnvelope Parse(ReadOnlySpan<byte> bytes, int maximumBytes = 1_048_576)
    {
        using var document = StrictJson.Parse(bytes, maximumBytes);
        var root = document.RootElement;
        StrictJson.RequireObject(root, "update.envelopeInvalid");
        StrictJson.RejectUnknown(root, "schemaVersion", "payloadBase64", "signatures");
        var schemaVersion = StrictJson.Int32(root, "schemaVersion");
        if (schemaVersion != 1) throw new TrustValidationException("update.envelopeVersionUnsupported");
        var payload = StrictJson.String(root, "payloadBase64", maximumBytes);
        if (!root.TryGetProperty("signatures", out var signatureArray) || signatureArray.ValueKind != JsonValueKind.Array ||
            signatureArray.GetArrayLength() is < 1 or > 16)
            throw new TrustValidationException("update.envelopeInvalid");
        var signatures = new List<EnvelopeSignature>();
        foreach (var element in signatureArray.EnumerateArray())
        {
            StrictJson.RequireObject(element, "update.envelopeInvalid");
            StrictJson.RejectUnknown(element, "keyId", "algorithm", "signatureBase64");
            signatures.Add(new(StrictJson.String(element, "keyId", 128), StrictJson.String(element, "algorithm", 64),
                StrictJson.String(element, "signatureBase64", 1024)));
        }
        return new(schemaVersion, payload, signatures);
    }
}

public sealed record TrustKey(string KeyId, string Algorithm, string PublicKeyBase64, bool Revoked = false, IReadOnlyList<string>? Roles = null);

public sealed record TrustRoot(
    int RootVersion,
    int Threshold,
    IReadOnlyList<TrustKey> Keys,
    int? IndexThreshold = null,
    int? ReleaseThreshold = null);

public sealed record ChannelTrustState(
    long IndexSequence,
    string PayloadSha256,
    DateTimeOffset AcceptedAt,
    long? TargetReleaseSequence = null,
    string? TargetManifestSha256 = null,
    long? TargetManifestLength = null);

public sealed record ReleaseTrustState(long ReleaseSequence, string ManifestSha256);

public sealed record TrustStateDocument(
    TrustRoot Root,
    IReadOnlyDictionary<string, ChannelTrustState> Channels,
    DateTimeOffset LastObservedUtc,
    IReadOnlyDictionary<string, ReleaseTrustState>? Releases = null,
    string? ProductId = null);

public sealed record VerifiedChannelIndex(
    byte[] Payload,
    string ProductId,
    string Channel,
    long IndexSequence,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string TargetManifestSha256,
    string PayloadSha256,
    int RootVersion,
    long TargetReleaseSequence,
    long TargetManifestLength)
{
    // 安装前重验原始签名索引，公开记录的 with 修改不能替代签名证据。
    internal SignedPayloadEnvelope? OriginalEnvelope { get; init; }
}

internal static class StrictJson
{
    public static JsonDocument Parse(ReadOnlySpan<byte> bytes, int maximumBytes = 1_048_576)
    {
        if (bytes.Length == 0 || bytes.Length > maximumBytes)
            throw new TrustValidationException("update.metadataTooLarge");
        try
        {
            var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 24,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            try { RejectDuplicates(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException) { throw new TrustValidationException("update.metadataInvalid"); }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new TrustValidationException("update.duplicateMetadataField");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    public static void RequireObject(JsonElement element, string code)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new TrustValidationException(code);
    }

    public static void RejectUnknown(JsonElement element, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name)) throw new TrustValidationException("update.duplicateMetadataField");
            else if (!set.Contains(property.Name)) throw new TrustValidationException("update.unknownMetadataField");
    }

    public static string String(JsonElement objectElement, string name, int maximum = 512)
    {
        if (!objectElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new TrustValidationException("update.metadataFieldInvalid");
        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > maximum)
            throw new TrustValidationException("update.metadataFieldInvalid");
        return result;
    }

    public static long Int64(JsonElement objectElement, string name)
    {
        if (!objectElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result) || result < 0)
            throw new TrustValidationException("update.metadataFieldInvalid");
        return result;
    }

    public static int Int32(JsonElement element, string name)
    {
        var value = Int64(element, name);
        if (value > int.MaxValue) throw new TrustValidationException("update.metadataFieldInvalid");
        return (int)value;
    }

    public static bool Boolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new TrustValidationException("update.metadataFieldInvalid");
        return value.GetBoolean();
    }
}

public sealed class TrustValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
