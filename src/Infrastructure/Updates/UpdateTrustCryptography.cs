using System.Security.Cryptography;
using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

public sealed partial class UpdateTrustStore
{
    private const string Algorithm = "ECDSA-P256-SHA256";
    private static readonly string[] KnownRoles = ["root", "channel-index", "release-manifest"];

    private static byte[] DecodeBase64(string value, int maximumBytes = 1_048_576)
    {
        if (value is null || value.Length > 4L * ((maximumBytes + 2L) / 3))
            throw new TrustValidationException("update.metadataTooLarge");
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length > maximumBytes) throw new TrustValidationException("update.metadataTooLarge");
            return bytes;
        }
        catch (FormatException) { throw new TrustValidationException("update.signatureEncodingInvalid"); }
    }

    private static ECDsa ImportKey(TrustKey key)
    {
        var bytes = DecodeBase64(key.PublicKeyBase64, 1024);
        var algorithm = ECDsa.Create();
        try
        {
            algorithm.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            if (consumed != bytes.Length || algorithm.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new TrustValidationException("update.rootInvalid");
            return algorithm;
        }
        catch (CryptographicException) { algorithm.Dispose(); throw new TrustValidationException("update.rootInvalid"); }
        catch { algorithm.Dispose(); throw; }
    }

    private static bool HasRole(TrustKey key, string role) => key.Roles is null
        ? role is "root" or "channel-index" : key.Roles.Contains(role, StringComparer.Ordinal);

    private static void ValidateRoot(TrustRoot root)
    {
        if (root is null || root.RootVersion < 1 || root.Keys is null || root.Keys.Count is < 1 or > 16 ||
            root.Threshold < 1 || root.Threshold > root.Keys.Count ||
            root.IndexThreshold is < 1 || root.IndexThreshold > root.Keys.Count ||
            root.ReleaseThreshold is < 1 || root.ReleaseThreshold > root.Keys.Count)
            throw new TrustValidationException("update.rootInvalid");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var material = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in root.Keys)
        {
            if (key is null || !SafeId(key.KeyId) || !ids.Add(key.KeyId) || key.Algorithm != Algorithm ||
                key.Roles is { } roles && (roles.Count is < 1 or > 3 || roles.Distinct(StringComparer.Ordinal).Count() != roles.Count ||
                    roles.Any(role => !KnownRoles.Contains(role, StringComparer.Ordinal))))
                throw new TrustValidationException("update.rootInvalid");
            using var imported = ImportKey(key);
            if (!material.Add(Convert.ToBase64String(imported.ExportSubjectPublicKeyInfo())))
                throw new TrustValidationException("update.rootInvalid");
        }
        foreach (var role in KnownRoles)
        {
            var threshold = Threshold(root, role);
            var count = root.Keys.Count(key => HasRole(key, role));
            // 旧根可以没有发行角色，显式配置的角色阈值必须可满足。
            if ((role != "release-manifest" || root.ReleaseThreshold.HasValue || count > 0) && count < threshold)
                throw new TrustValidationException("update.rootInvalid");
        }
    }

    private static int Threshold(TrustRoot root, string role) => role switch
    {
        "channel-index" => root.IndexThreshold ?? root.Threshold,
        "release-manifest" => root.ReleaseThreshold ?? root.Threshold,
        _ => root.Threshold
    };

    private static void VerifyEnvelope(SignedPayloadEnvelope envelope, byte[] payload, TrustRoot root, string role)
    {
        if (envelope.SchemaVersion != 1) throw new TrustValidationException("update.envelopeVersionUnsupported");
        if (envelope.Signatures is null || envelope.Signatures.Count is < 1 or > 16)
            throw new TrustValidationException("update.envelopeInvalid");
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signature in envelope.Signatures)
        {
            if (signature is null) throw new TrustValidationException("update.envelopeInvalid");
            var key = root.Keys.FirstOrDefault(item => item.KeyId == signature.KeyId);
            if (key is null || key.Revoked || signature.Algorithm != Algorithm || !HasRole(key, role) || accepted.Contains(key.KeyId)) continue;
            using var verifier = ImportKey(key);
            var bytes = DecodeBase64(signature.SignatureBase64, 1024);
            try
            {
                if (verifier.VerifyData(payload, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) accepted.Add(key.KeyId);
            }
            catch (CryptographicException) { }
        }
        if (accepted.Count < Threshold(root, role)) throw new TrustValidationException("update.signatureInvalid");
    }

    private static TrustRoot ParseRoot(byte[] payload)
    {
        using var document = StrictJson.Parse(payload);
        var value = document.RootElement;
        StrictJson.RequireObject(value, "update.rootInvalid");
        StrictJson.RejectUnknown(value, "rootVersion", "threshold", "keys", "indexThreshold", "releaseThreshold");
        if (!value.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() is < 1 or > 16)
            throw new TrustValidationException("update.rootInvalid");
        var parsed = new List<TrustKey>();
        foreach (var key in keys.EnumerateArray())
        {
            StrictJson.RequireObject(key, "update.rootInvalid");
            StrictJson.RejectUnknown(key, "keyId", "algorithm", "publicKeyBase64", "revoked", "roles");
            List<string>? roles = null;
            if (key.TryGetProperty("roles", out var array) && array.ValueKind != JsonValueKind.Null)
            {
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 3)
                    throw new TrustValidationException("update.rootInvalid");
                roles = [];
                foreach (var role in array.EnumerateArray())
                {
                    if (role.ValueKind != JsonValueKind.String) throw new TrustValidationException("update.rootInvalid");
                    roles.Add(role.GetString()!);
                }
            }
            parsed.Add(new(StrictJson.String(key, "keyId", 128), StrictJson.String(key, "algorithm", 64),
                StrictJson.String(key, "publicKeyBase64", 2048), StrictJson.Boolean(key, "revoked"), roles));
        }
        var root = new TrustRoot(StrictJson.Int32(value, "rootVersion"), StrictJson.Int32(value, "threshold"), parsed,
            value.TryGetProperty("indexThreshold", out var indexThreshold) && indexThreshold.ValueKind != JsonValueKind.Null ? StrictJson.Int32(value, "indexThreshold") : null,
            value.TryGetProperty("releaseThreshold", out var releaseThreshold) && releaseThreshold.ValueKind != JsonValueKind.Null ? StrictJson.Int32(value, "releaseThreshold") : null);
        ValidateRoot(root);
        return root;
    }

    private static bool SafeId(string? value) => value is { Length: > 0 and <= 128 } && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
}
