using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Infrastructure.Updates;
using MabiTool.Providers.GitHubReleases;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class UpdateTrustStoreTests : IDisposable
{
    private readonly UpdateTestWorkspace workspace = new();
    private string directory => workspace.Root;
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly DateTimeOffset now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Accepts_signed_index_persists_sequence_and_rejects_tampering()
    {
        var store = CreateStore();
        var envelope = Index("stable", 3, "a", key);

        var accepted = store.VerifyChannelIndex(envelope, "mabitool", "stable", now.AddMinutes(1));

        Assert.Equal(3, accepted.IndexSequence);
        Assert.Equal("stable", accepted.Channel);
        Assert.Throws<TrustValidationException>(() => store.VerifyChannelIndex(
            envelope with { PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("tampered")) },
            "mabitool", "stable", now.AddMinutes(2)));
        var reloaded = new UpdateTrustStore(Path.Combine(directory, "trust.json"), Root(), now);
        Assert.Equal(3, reloaded.Channels["stable"].IndexSequence);
    }

    [Fact]
    public void Rejects_expired_clock_rollback_and_lower_or_conflicting_sequence()
    {
        var store = CreateStore();
        store.VerifyChannelIndex(Index("stable", 4, "a", key), "mabitool", "stable", now.AddMinutes(1));

        AssertCode("update.clockUntrusted", () => store.VerifyChannelIndex(Index("stable", 5, "b", key), "mabitool", "stable", now.AddMinutes(-5)));
        AssertCode("update.sequenceRollback", () => store.VerifyChannelIndex(Index("stable", 3, "c", key), "mabitool", "stable", now.AddMinutes(2)));
        AssertCode("update.sameSequenceDifferentMetadata", () => store.VerifyChannelIndex(Index("stable", 4, "d", key), "mabitool", "stable", now.AddMinutes(2)));
        AssertCode("update.metadataExpired", () => store.VerifyChannelIndex(Index("stable", 5, "e", key, expires: now.AddMinutes(1)), "mabitool", "stable", now.AddMinutes(2)));
    }

    [Fact]
    public void Channels_have_independent_sequences_and_revoked_key_cannot_authorize()
    {
        var store = CreateStore();
        store.VerifyChannelIndex(Index("preview", 1, "a", key), "mabitool", "preview", now.AddMinutes(1));
        Assert.Equal(1, store.VerifyChannelIndex(Index("stable", 1, "b", key), "mabitool", "stable", now.AddMinutes(2)).IndexSequence);

        var revokedRoot = Root(revoked: true);
        var revokedStore = new UpdateTrustStore(Path.Combine(directory, "revoked.json"), revokedRoot, now);
        AssertCode("update.signatureInvalid", () => revokedStore.VerifyChannelIndex(Index("stable", 1, "c", key), "mabitool", "stable", now.AddMinutes(1)));
    }

    [Fact]
    public void Root_rotation_requires_old_and_new_threshold_and_persists()
    {
        var store = CreateStore();
        var rotated = RootPayload(2, secondKey);
        var rotation = Envelope(rotated, [Sign("old", key, rotated), Sign("new", secondKey, rotated)]);

        store.ApplyRootRotation(rotation, now.AddMinutes(1));

        Assert.Equal(2, store.Root.RootVersion);
        var reloaded = new UpdateTrustStore(Path.Combine(directory, "trust.json"), Root(), now);
        Assert.Equal(2, reloaded.Root.RootVersion);
        AssertCode("update.rootVersionInvalid", () => reloaded.ApplyRootRotation(rotation, now.AddMinutes(2)));
    }

    [Fact]
    public void Envelope_parser_rejects_unknown_duplicate_and_oversized_structure()
    {
        Assert.Throws<TrustValidationException>(() => SignedPayloadEnvelope.Parse(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"payloadBase64\":\"YQ==\",\"signatures\":[],\"extra\":true}")));
        Assert.Throws<TrustValidationException>(() => SignedPayloadEnvelope.Parse(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"payloadBase64\":\"YQ==\",\"signatures\":[{\"keyId\":\"a\",\"algorithm\":\"ECDSA-P256-SHA256\",\"signatureBase64\":\"YQ==\"}]}")));
    }

    [Fact]
    public async Task Signed_index_from_release_provider_is_verified_before_acceptance()
    {
        var payload = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"productId":"mabitool","channel":"stable","indexSequence":1,"issuedAt":"2026-11-08T00:00:00Z","expiresAt":"2026-11-08T01:00:00Z","targetManifestSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","targetManifestLength":100,"targetReleaseSequence":1}""");
        var envelope = Envelope(payload, [Sign("old", key, payload)]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = envelope.SchemaVersion,
            payloadBase64 = envelope.PayloadBase64,
            signatures = envelope.Signatures.Select(signature => new
            {
                keyId = signature.KeyId,
                algorithm = signature.Algorithm,
                signatureBase64 = signature.SignatureBase64
            })
        });
        var handler = new SingleResponseHandler(bytes);
        using var provider = new ReleaseIndexSourceProvider(handler, retryCount: 0);
        var retrieved = await provider.FetchChannelIndexAsync(new("Mobi-Lan/MobiFolio", "stable", null, null));
        var parsed = SignedPayloadEnvelope.Parse(retrieved.Content);
        var store = CreateStore();

        var accepted = store.VerifyChannelIndex(parsed, "mabitool", "stable", now.AddMinutes(1));

        Assert.Equal("stable", accepted.Channel);
    }

    private UpdateTrustStore CreateStore() => new(Path.Combine(directory, "trust.json"), Root(), now);

    private TrustRoot Root(bool revoked = false) => new(1, 1, [
        new("old", "ECDSA-P256-SHA256", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), revoked)]);

    private SignedPayloadEnvelope Index(string channel, long sequence, string marker, ECDsa signer, DateTimeOffset? expires = null)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            productId = "mabitool",
            channel,
            indexSequence = sequence,
            issuedAt = now.ToString("O"),
            expiresAt = (expires ?? now.AddHours(1)).ToString("O"),
            targetManifestLength = 100,
            targetReleaseSequence = sequence,
            targetManifestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker))).ToLowerInvariant()
        }));
        return Envelope(payload, [Sign("old", signer, payload)]);
    }

    private byte[] RootPayload(int version, ECDsa newKey) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        rootVersion = version,
        threshold = 1,
        keys = new[]
        {
            new { keyId = "new", algorithm = "ECDSA-P256-SHA256", publicKeyBase64 = Convert.ToBase64String(newKey.ExportSubjectPublicKeyInfo()), revoked = false }
        }
    }));

    private static SignedPayloadEnvelope Envelope(byte[] payload, IReadOnlyList<EnvelopeSignature> signatures) =>
        new(1, Convert.ToBase64String(payload), signatures);

    private static EnvelopeSignature Sign(string id, ECDsa signer, byte[] payload) =>
        new(id, "ECDSA-P256-SHA256", Convert.ToBase64String(signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));

    private static void AssertCode(string expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<TrustValidationException>(action).Code);

    private sealed class SingleResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    public void Dispose()
    {
        key.Dispose();
        secondKey.Dispose();
        workspace.Dispose();
    }
}
