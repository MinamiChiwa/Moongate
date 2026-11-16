using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Infrastructure.Updates;

public sealed partial class UpdateTrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };
    private readonly object gate = new();
    private readonly string statePath;
    private TrustStateDocument state;

    public UpdateTrustStore(string statePath, TrustRoot initialRoot, DateTimeOffset? initialClock = null)
    {
        if (string.IsNullOrWhiteSpace(statePath)) throw new ArgumentException("状态路径不能为空", nameof(statePath));
        ValidateRoot(initialRoot);
        this.statePath = Path.GetFullPath(statePath);
        UpdatePaths.RejectLinks(this.statePath);
        using var ownership = AcquireWriter();
        if (File.Exists(this.statePath)) state = ReadState();
        else
        {
            state = Freeze(new(initialRoot, new Dictionary<string, ChannelTrustState>(), initialClock ?? DateTimeOffset.UtcNow));
            Persist(state);
        }
    }

    public TrustRoot Root { get { lock (gate) return state.Root; } }
    public IReadOnlyDictionary<string, ChannelTrustState> Channels { get { lock (gate) return state.Channels; } }
    public IReadOnlyDictionary<string, ReleaseTrustState> Releases { get { lock (gate) return state.Releases!; } }
    public DateTimeOffset LastObservedUtc { get { lock (gate) return state.LastObservedUtc; } }

    public VerifiedChannelIndex VerifyChannelIndex(SignedPayloadEnvelope envelope, string expectedProductId,
        string expectedChannel, DateTimeOffset now, TimeSpan? allowedClockSkew = null)
    {
        lock (gate)
        {
            using var ownership = AcquireWriter();
            state = ReadState();
            var parsed = CheckIndex(envelope, expectedProductId, expectedChannel, now, allowedClockSkew ?? TimeSpan.FromMinutes(2));
            Commit(IndexState(parsed, now));
            return parsed;
        }
    }

    public VerifiedReleaseManifest VerifyReleaseManifest(ReadOnlySpan<byte> envelopeBytes, VerifiedChannelIndex index,
        string expectedPlatform, DateTimeOffset now)
    {
        if (envelopeBytes.Length == 0 || envelopeBytes.Length > ReleaseManifestParser.MaximumEnvelopeBytes)
            throw new TrustValidationException("update.metadataTooLarge");
        lock (gate)
        {
            using var ownership = AcquireWriter();
            state = ReadState();
            // 重新验证原始索引，不能依赖调用者可改写的 DTO 或上一次根版本。
            var refreshed = CheckIndex(index.OriginalEnvelope ?? throw new TrustValidationException("update.indexUnverified"),
                index.ProductId, index.Channel, now, TimeSpan.FromMinutes(2));
            var hash = Convert.ToHexString(SHA256.HashData(envelopeBytes)).ToLowerInvariant();
            if (envelopeBytes.Length != refreshed.TargetManifestLength || hash != refreshed.TargetManifestSha256)
                throw new TrustValidationException("update.manifestBindingMismatch");
            var envelope = SignedPayloadEnvelope.Parse(envelopeBytes, ReleaseManifestParser.MaximumEnvelopeBytes);
            var payload = DecodeBase64(envelope.PayloadBase64, ReleaseManifestParser.MaximumPayloadBytes);
            VerifyEnvelope(envelope, payload, state.Root, "release-manifest");
            var verified = ReleaseManifestParser.Parse(payload, hash, refreshed, expectedPlatform, now);
            if (state.Releases!.TryGetValue(refreshed.Channel, out var previous))
                CheckReleaseSequence(verified.Manifest.ReleaseSequence, hash, previous.ReleaseSequence, previous.ManifestSha256);
            var releases = new Dictionary<string, ReleaseTrustState>(state.Releases!, StringComparer.Ordinal)
            {
                [refreshed.Channel] = new(verified.Manifest.ReleaseSequence, hash)
            };
            Commit(IndexState(refreshed, now) with { Releases = releases });
            return verified;
        }
    }

    public void ApplyRootRotation(SignedPayloadEnvelope envelope, DateTimeOffset now)
    {
        lock (gate)
        {
            using var ownership = AcquireWriter();
            state = ReadState();
            var payload = DecodeBase64(envelope.PayloadBase64);
            var candidate = ParseRoot(payload);
            if ((long)candidate.RootVersion != (long)state.Root.RootVersion + 1)
                throw new TrustValidationException("update.rootVersionInvalid");
            VerifyEnvelope(envelope, payload, state.Root, "root");
            VerifyEnvelope(envelope, payload, candidate, "root");
            if (now < state.LastObservedUtc) throw new TrustValidationException("update.clockUntrusted");
            Commit(state with { Root = candidate, LastObservedUtc = now });
        }
    }

    public void Save()
    {
        lock (gate)
        {
            using var ownership = AcquireWriter();
            // 磁盘是多实例共同裁决依据，旧实例的 Save 不覆盖新序列。
            Commit(ReadState());
        }
    }

    private VerifiedChannelIndex CheckIndex(SignedPayloadEnvelope envelope, string expectedProductId, string expectedChannel,
        DateTimeOffset now, TimeSpan skew)
    {
        if (skew < TimeSpan.Zero || skew > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(skew));
        if (now < state.LastObservedUtc - skew) throw new TrustValidationException("update.clockUntrusted");
        if (state.ProductId is not null && state.ProductId != expectedProductId) throw new TrustValidationException("update.identityMismatch");
        if (envelope.Signatures is null || envelope.Signatures.Count is < 1 or > 16)
            throw new TrustValidationException("update.envelopeInvalid");
        envelope = envelope with { Signatures = Array.AsReadOnly(envelope.Signatures.ToArray()) };
        var payload = DecodeBase64(envelope.PayloadBase64);
        VerifyEnvelope(envelope, payload, state.Root, "channel-index");
        using var document = StrictJson.Parse(payload);
        var root = document.RootElement;
        StrictJson.RequireObject(root, "update.metadataInvalid");
        StrictJson.RejectUnknown(root, "schemaVersion", "productId", "channel", "indexSequence", "issuedAt", "expiresAt",
            "targetManifestSha256", "targetManifestLength", "targetReleaseSequence");
        if (StrictJson.Int32(root, "schemaVersion") != 1) throw new TrustValidationException("update.indexVersionUnsupported");
        var product = ReleaseManifestParser.Identifier(root, "productId");
        var channel = ReleaseManifestParser.Identifier(root, "channel", 32);
        var sequence = StrictJson.Int64(root, "indexSequence");
        var issued = ReleaseManifestParser.Time(root, "issuedAt");
        var expires = ReleaseManifestParser.Time(root, "expiresAt");
        var manifestHash = ReleaseManifestParser.Hash(root, "targetManifestSha256");
        var manifestLength = StrictJson.Int64(root, "targetManifestLength");
        var releaseSequence = StrictJson.Int64(root, "targetReleaseSequence");
        if (product != expectedProductId || channel != expectedChannel) throw new TrustValidationException("update.identityMismatch");
        if (expires <= issued || expires <= now || issued > now.AddMinutes(2)) throw new TrustValidationException("update.metadataExpired");
        if (manifestLength is < 1 or > ReleaseManifestParser.MaximumEnvelopeBytes) throw new TrustValidationException("update.metadataFieldInvalid");
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (state.Channels.TryGetValue(channel, out var previous))
        {
            if (sequence < previous.IndexSequence) throw new TrustValidationException("update.sequenceRollback");
            if (sequence == previous.IndexSequence && hash != previous.PayloadSha256)
                throw new TrustValidationException("update.sameSequenceDifferentMetadata");
            if (previous.TargetReleaseSequence is { } lowerBound)
            {
                CheckReleaseSequence(releaseSequence, manifestHash, lowerBound, previous.TargetManifestSha256!);
                if (releaseSequence == lowerBound && manifestLength != previous.TargetManifestLength)
                    throw new TrustValidationException("update.sameReleaseDifferentManifest");
            }
        }
        if (state.Channels.Count >= 32 && !state.Channels.ContainsKey(channel)) throw new TrustValidationException("update.trustCapacityExceeded");
        return new(payload, product, channel, sequence, issued, expires, manifestHash, hash, state.Root.RootVersion, releaseSequence, manifestLength)
        { OriginalEnvelope = envelope };
    }

    private TrustStateDocument IndexState(VerifiedChannelIndex parsed, DateTimeOffset now)
    {
        var channels = new Dictionary<string, ChannelTrustState>(state.Channels, StringComparer.Ordinal)
        {
            [parsed.Channel] = new(parsed.IndexSequence, parsed.PayloadSha256, now, parsed.TargetReleaseSequence, parsed.TargetManifestSha256, parsed.TargetManifestLength)
        };
        return state with { Channels = channels, ProductId = parsed.ProductId, LastObservedUtc = now > state.LastObservedUtc ? now : state.LastObservedUtc };
    }

    private static void CheckReleaseSequence(long sequence, string hash, long previous, string previousHash)
    {
        if (sequence < previous) throw new TrustValidationException("update.releaseSequenceRollback");
        if (sequence == previous && hash != previousHash) throw new TrustValidationException("update.sameReleaseDifferentManifest");
    }

    private static TrustStateDocument Freeze(TrustStateDocument value) => value with
    {
        Root = value.Root with { Keys = Array.AsReadOnly(value.Root.Keys.Select(key => key with
            { Roles = key.Roles is null ? null : Array.AsReadOnly(key.Roles.ToArray()) }).ToArray()) },
        Channels = new ReadOnlyDictionary<string, ChannelTrustState>(new Dictionary<string, ChannelTrustState>(value.Channels, StringComparer.Ordinal)),
        Releases = new ReadOnlyDictionary<string, ReleaseTrustState>(new Dictionary<string, ReleaseTrustState>(value.Releases ?? new Dictionary<string, ReleaseTrustState>(), StringComparer.Ordinal))
    };
}
