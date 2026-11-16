using System.Text.Json;
using MabiTool.Infrastructure.Updates;

namespace MabiTool.ReleaseBuilder;

public sealed class ChannelPublisher
{
    public const string IndexName = "channel-index.json";

    public async Task<VerifiedChannelIndex> PublishAsync(string releaseDirectory, string channelDirectory,
        TrustRoot trustedRoot, IReadOnlyList<SigningKey> signers, long indexSequence,
        DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var verified = await new ReleaseBuilderService().VerifyAsync(releaseDirectory, trustedRoot, cancellationToken);
        var channelRoot = Path.GetFullPath(channelDirectory);
        ReleaseFiles.RequireSafePath(channelRoot);
        ReleaseFiles.RequireDisjoint(releaseDirectory, channelRoot);
        foreach (var signer in signers.Where(signer => signer.SourcePath is not null))
            try { ReleaseFiles.RequireDisjoint(releaseDirectory, signer.SourcePath!); ReleaseFiles.RequireDisjoint(channelRoot, signer.SourcePath!); }
            catch (ReleaseBuildException) { throw new ReleaseBuildException("release.privateKeyInComponent"); }
        ReleaseFiles.RequireSafePath(channelRoot + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(channelRoot)!);
        await using var ownership = new FileStream(channelRoot + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Directory.CreateDirectory(channelRoot);
        var indexPath = Path.Combine(channelRoot, IndexName);
        var registry = Path.Combine(channelRoot, "releases", verified.Receipt.ReleaseId);
        ReleaseFiles.RequireSafePath(registry);
        var registryPath = Path.Combine(registry, ReleaseBuilderService.ManifestName);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(releaseDirectory, ReleaseBuilderService.ManifestName), cancellationToken);
        if (File.Exists(registryPath) && ReleaseBuilderService.Hash(await File.ReadAllBytesAsync(registryPath, cancellationToken)) != verified.Receipt.Manifest.Sha256)
            throw new ReleaseBuildException("release.identityReused");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, productId = verified.Receipt.ProductId, channel = verified.Receipt.Channel,
            indexSequence, issuedAt, expiresAt, targetManifestSha256 = verified.Receipt.Manifest.Sha256,
            targetManifestLength = verified.Receipt.Manifest.Length, targetReleaseSequence = verified.Receipt.ReleaseSequence
        }, ReleaseJson.Options);
        var envelope = ReleaseBuilderService.Sign(payload, signers);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ReleaseJson.Options);
        var workName = ".channel-validation-" + Guid.NewGuid().ToString("N");
        var work = Path.Combine(channelRoot, workName);
        Directory.CreateDirectory(work);
        try
        {
            SignedPayloadEnvelope? previous = null;
            DateTimeOffset initialTime = now ?? DateTimeOffset.UtcNow;
            if (File.Exists(indexPath))
            {
                previous = SignedPayloadEnvelope.Parse(await File.ReadAllBytesAsync(indexPath, cancellationToken));
                using var previousPayload = JsonDocument.Parse(Convert.FromBase64String(previous.PayloadBase64));
                initialTime = previousPayload.RootElement.GetProperty("issuedAt").GetDateTimeOffset();
            }
            var store = new UpdateTrustStore(Path.Combine(work, "trust.json"), trustedRoot, initialTime);
            if (previous is not null)
            {
                var previousIndex = store.VerifyChannelIndex(previous, verified.Receipt.ProductId, verified.Receipt.Channel, initialTime);
                var previousManifest = FindPreviousManifest(channelRoot, previousIndex);
                store.VerifyReleaseManifest(previousManifest, previousIndex, verified.Manifest.Manifest.Platform, initialTime);
            }
            var index = store.VerifyChannelIndex(envelope, verified.Receipt.ProductId, verified.Receipt.Channel, now ?? DateTimeOffset.UtcNow);
            if (previous is not null)
            {
                using var old = JsonDocument.Parse(Convert.FromBase64String(previous.PayloadBase64));
                if (indexSequence <= old.RootElement.GetProperty("indexSequence").GetInt64())
                    throw new ReleaseBuildException("release.indexSequenceNotAdvanced");
            }
            store.VerifyReleaseManifest(manifestBytes, index, verified.Manifest.Manifest.Platform, now ?? DateTimeOffset.UtcNow);
            if (!File.Exists(registryPath))
            {
                Directory.CreateDirectory(registry);
                await ReleaseFiles.WriteDurableAsync(registryPath, manifestBytes, false, cancellationToken);
            }
            // 索引是唯一当前指针；最后以同卷原子替换发布，验证失败保留原索引。
            await ReleaseFiles.WriteDurableAsync(indexPath, bytes, true, cancellationToken);
            return index;
        }
        finally { ReleaseFiles.DeleteOwnedWork(work, channelRoot, workName); }
    }

    private static byte[] FindPreviousManifest(string channelRoot, VerifiedChannelIndex previous)
    {
        var registry = Path.Combine(channelRoot, "releases");
        ReleaseFiles.RequireSafePath(registry);
        if (!Directory.Exists(registry)) throw new ReleaseBuildException("release.registryMissing");
        var directories = Directory.GetDirectories(registry);
        if (directories.Length > 4096) throw new ReleaseBuildException("release.registryTooLarge");
        foreach (var directory in directories)
        {
            ReleaseFiles.RequireSafePath(directory);
            var manifest = Path.Combine(directory, ReleaseBuilderService.ManifestName);
            ReleaseFiles.RequireSafePath(manifest);
            if (!File.Exists(manifest) || new FileInfo(manifest).Length != previous.TargetManifestLength) continue;
            var bytes = File.ReadAllBytes(manifest);
            if (ReleaseBuilderService.Hash(bytes) != previous.TargetManifestSha256) continue;
            using var payload = JsonDocument.Parse(Convert.FromBase64String(SignedPayloadEnvelope.Parse(bytes, 12 * 1024 * 1024).PayloadBase64));
            if (payload.RootElement.GetProperty("releaseId").GetString() != Path.GetFileName(directory))
                throw new ReleaseBuildException("release.registryInvalid");
            return bytes;
        }
        throw new ReleaseBuildException("release.registryMissing");
    }
}
