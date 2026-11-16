using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Infrastructure.Updates;

namespace MabiTool.ReleaseBuilder;

public sealed class ReleaseBuilderService
{
    public const string ManifestName = "release-manifest.v1.json";
    public const string ReceiptName = "release-build.v1.json";

    public async Task<ReleaseBuildReceipt> BuildAsync(ReleaseBuildInput input, string inputDirectory, string outputDirectory,
        IReadOnlyList<SigningKey> signers, TrustRoot trustedRoot, CancellationToken cancellationToken = default)
    {
        if (input.SchemaVersion != 1 || input.Components.Count is < 1 or > 64 || input.Data.Count > 128)
            throw new ReleaseBuildException("release.inputInvalid");
        var output = Path.GetFullPath(outputDirectory);
        ReleaseFiles.RequireSafePath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new ReleaseBuildException("release.alreadyExists");
        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var workName = Path.GetFileName(output) + ".build-" + Guid.NewGuid().ToString("N");
        var work = Path.Combine(parent, workName);
        var components = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Directory.CreateDirectory(Path.Combine(work, "assets"));
        try
        {
            var sbomPath = Resolve(inputDirectory, input.SbomPath);
            var noticesPath = Resolve(inputDirectory, input.NoticesPath);
            if (new FileInfo(sbomPath).Length > 16 * 1024 * 1024) throw new ReleaseBuildException("release.sbomInvalid");
            foreach (var signer in signers.Where(signer => signer.SourcePath is not null))
                try { ReleaseFiles.RequireDisjoint(sbomPath, signer.SourcePath!); ReleaseFiles.RequireDisjoint(noticesPath, signer.SourcePath!); }
                catch (ReleaseBuildException) { throw new ReleaseBuildException("release.privateKeyInComponent"); }
            using (var sbom = JsonDocument.Parse(await File.ReadAllBytesAsync(sbomPath, cancellationToken)))
                if (!sbom.RootElement.TryGetProperty("spdxVersion", out _) &&
                    (!sbom.RootElement.TryGetProperty("bomFormat", out var format) || format.GetString() != "CycloneDX"))
                    throw new ReleaseBuildException("release.sbomInvalid");
            if (new FileInfo(noticesPath).Length == 0) throw new ReleaseBuildException("release.noticesMissing");
            ReleaseFiles.RequireDisjoint(sbomPath, output);
            ReleaseFiles.RequireDisjoint(noticesPath, output);
            var sbomAsset = await ReleaseFiles.AddAssetAsync(sbomPath, Path.Combine(work, "assets"), cancellationToken);
            var noticesAsset = await ReleaseFiles.AddAssetAsync(noticesPath, Path.Combine(work, "assets"), cancellationToken);
            foreach (var component in input.Components)
            {
                if (!Identifier(component.ComponentId) || !seen.Add(component.ComponentId))
                    throw new ReleaseBuildException("release.componentInvalid");
                var source = Resolve(inputDirectory, component.Directory);
                ReleaseFiles.RequireDisjoint(source, output);
                ReleaseFiles.RequireDisjoint(source, work);
                foreach (var signer in signers.Where(signer => signer.SourcePath is not null))
                    try { ReleaseFiles.RequireDisjoint(source, signer.SourcePath!); }
                    catch (ReleaseBuildException) { throw new ReleaseBuildException("release.privateKeyInComponent"); }
                var built = Path.Combine(work, "built", component.ComponentId);
                var artifacts = await new FileTreeArtifactBuilder().BuildAsync(source, built, cancellationToken);
                foreach (var artifact in artifacts.Files)
                    await ReleaseFiles.AddAssetAsync(Path.Combine(built, "blobs", artifact.DownloadSha256 + ".blob"),
                        Path.Combine(work, "assets"), cancellationToken);
                var package = await ReleaseFiles.AddAssetAsync(Path.Combine(built, "full.zip"), Path.Combine(work, "assets"), cancellationToken);
                components.Add(new
                {
                    component.ComponentId, component.Version, component.Optional, component.ModuleApiRange,
                    component.HostVersionRange, component.WorkerProtocolRange, component.DataSchemaRange,
                    component.Dependencies, installedSize = artifacts.Files.Sum(file => file.File.SizeBytes),
                    artifacts.TargetTreeHash,
                    files = artifacts.Files.Select(file => new
                    {
                        path = file.File.RelativePath, size = file.File.SizeBytes, sha256 = file.File.Sha256,
                        componentId = component.ComponentId, file.File.Executable,
                        encoding = file.Encoding == UpdateBlobEncoding.Gzip ? "gzip" : "identity",
                        asset = new AssetDescriptor(file.DownloadSha256 + ".blob", file.DownloadLength, file.DownloadSha256)
                    }).ToArray(), fullPackage = package
                });
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, input.ProductId, input.Channel, input.ReleaseId, input.Version,
                input.ReleaseSequence, input.PublishedAt, input.MinUpdaterVersion,
                platforms = new[] { new { os = "windows", input.Architecture, input.MinimumOsBuild, input.RuntimeMode, components } },
                input.Data, input.ReleaseNotes, sbom = sbomAsset, notices = noticesAsset
            }, ReleaseJson.Options);
            var envelope = Sign(payload, signers);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ReleaseJson.Options);
            await ReleaseFiles.WriteDurableAsync(Path.Combine(work, ManifestName), manifestBytes, false, cancellationToken);
            // 借用相同客户端解析器验证签名角色、元数据、兼容关系和完整目标树。
            var verified = VerifyManifest(manifestBytes, trustedRoot, input.ProductId, input.Channel, input.PublishedAt, parent);
            var assets = CollectAssets(verified).OrderBy(asset => asset.AssetName, StringComparer.Ordinal).ToArray();
            var descriptor = await ReleaseFiles.DescribeAsync(Path.Combine(work, ManifestName), cancellationToken);
            var receipt = new ReleaseBuildReceipt(1, input.ProductId, input.Channel, input.ReleaseId, input.ReleaseSequence, descriptor, assets);
            await ReleaseFiles.WriteDurableAsync(Path.Combine(work, ReceiptName), JsonSerializer.SerializeToUtf8Bytes(receipt, ReleaseJson.Options), false, cancellationToken);
            ReleaseFiles.DeleteOwnedWork(Path.Combine(work, "built"), work, "built");
            await VerifyAsync(work, trustedRoot, cancellationToken);
            ReleaseFiles.RequireSafePath(output);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(work, output);
            return receipt;
        }
        finally { ReleaseFiles.DeleteOwnedWork(work, parent, workName); }
    }

    public async Task<ReleaseVerification> VerifyAsync(string releaseDirectory, TrustRoot trustedRoot,
        CancellationToken cancellationToken = default)
    {
        var release = Path.GetFullPath(releaseDirectory);
        ReleaseFiles.RequireSafePath(release);
        var receipt = ReleaseJson.Read<ReleaseBuildReceipt>(Path.Combine(release, ReceiptName));
        if (receipt.SchemaVersion != 1 || receipt.Assets.Count > 100_128 || !Identifier(receipt.ProductId) || !Identifier(receipt.Channel))
            throw new ReleaseBuildException("release.receiptInvalid");
        var manifestPath = Path.Combine(release, ManifestName);
        if (await ReleaseFiles.DescribeAsync(manifestPath, cancellationToken) != receipt.Manifest)
            throw new ReleaseBuildException("release.manifestMismatch");
        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var payload = JsonDocument.Parse(Convert.FromBase64String(SignedPayloadEnvelope.Parse(bytes, 12 * 1024 * 1024).PayloadBase64));
        using (payload)
        {
            var published = payload.RootElement.GetProperty("publishedAt").GetDateTimeOffset();
            var manifest = VerifyManifest(bytes, trustedRoot, receipt.ProductId, receipt.Channel, published, Path.GetDirectoryName(release)!);
            if (manifest.Manifest.ReleaseId != receipt.ReleaseId || manifest.Manifest.ReleaseSequence != receipt.ReleaseSequence)
                throw new ReleaseBuildException("release.receiptInvalid");
            var actual = CollectAssets(manifest).OrderBy(asset => asset.AssetName, StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(receipt.Assets)) throw new ReleaseBuildException("release.receiptInvalid");
            foreach (var descriptor in actual)
                if (await ReleaseFiles.DescribeAsync(Path.Combine(release, "assets", descriptor.AssetName), cancellationToken) != descriptor)
                    throw new ReleaseBuildException("release.assetMismatch");
            foreach (var component in manifest.Components.Values)
                await VerifyComponentAsync(release, component.Artifacts, cancellationToken);
            return new(receipt, manifest);
        }
    }

    internal static SignedPayloadEnvelope Sign(byte[] payload, IReadOnlyList<SigningKey> signers)
    {
        if (signers.Count is < 1 or > 16 || signers.Select(signer => signer.KeyId).Distinct(StringComparer.Ordinal).Count() != signers.Count)
            throw new ReleaseBuildException("release.signersInvalid");
        return new(1, Convert.ToBase64String(payload), signers.Select(signer => new EnvelopeSignature(signer.KeyId,
            "ECDSA-P256-SHA256", Convert.ToBase64String(signer.Key.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence)))).ToArray());
    }

    private static VerifiedReleaseManifest VerifyManifest(byte[] envelope, TrustRoot root, string product, string channel, DateTimeOffset published, string scratchParent)
    {
        // 临时索引只用于同一个客户端解析器检查构建物，最终渠道另由独立索引角色签名。
        using var indexSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rootSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = root.Keys.Where(key => key.Roles is null || key.Roles.Contains("release-manifest", StringComparer.Ordinal))
            .Select(key => key with { Roles = new[] { "release-manifest" } }).ToList();
        keys.Add(new("builder-validation-index", "ECDSA-P256-SHA256", Convert.ToBase64String(indexSigner.ExportSubjectPublicKeyInfo()), false, new[] { "channel-index" }));
        keys.Add(new("builder-validation-root", "ECDSA-P256-SHA256", Convert.ToBase64String(rootSigner.ExportSubjectPublicKeyInfo()), false, new[] { "root" }));
        var validationRoot = new TrustRoot(root.RootVersion, 1, keys, 1, root.ReleaseThreshold ?? root.Threshold);
        using var document = JsonDocument.Parse(Convert.FromBase64String(SignedPayloadEnvelope.Parse(envelope, 12 * 1024 * 1024).PayloadBase64));
        var sequence = document.RootElement.GetProperty("releaseSequence").GetInt64();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, productId = product, channel, indexSequence = 1,
            issuedAt = published, expiresAt = published.AddDays(1), targetManifestSha256 = Hash(envelope),
            targetManifestLength = envelope.Length, targetReleaseSequence = sequence
        }, ReleaseJson.Options);
        ReleaseFiles.RequireSafePath(scratchParent);
        Directory.CreateDirectory(scratchParent);
        var name = Guid.NewGuid().ToString("N");
        var scratch = Path.Combine(scratchParent, name);
        Directory.CreateDirectory(scratch);
        try
        {
            _ = new UpdateTrustStore(Path.Combine(scratch, "original-root.json"), root, published);
            var store = new UpdateTrustStore(Path.Combine(scratch, "trust.json"), validationRoot, published);
            var index = store.VerifyChannelIndex(Sign(payload, [new("builder-validation-index", indexSigner)]), product, channel, published);
            var architecture = document.RootElement.GetProperty("platforms")[0].GetProperty("architecture").GetString();
            return store.VerifyReleaseManifest(envelope, index, "win-" + architecture, published);
        }
        finally { ReleaseFiles.DeleteOwnedWork(scratch, scratchParent, name); }
    }

    internal static IEnumerable<AssetDescriptor> CollectAssets(VerifiedReleaseManifest manifest)
    {
        var descriptors = new List<AssetDescriptor> { new(manifest.Sbom.AssetName, manifest.Sbom.Length, manifest.Sbom.Sha256),
            new(manifest.Notices.AssetName, manifest.Notices.Length, manifest.Notices.Sha256) };
        foreach (var component in manifest.Components.Values)
        {
            descriptors.AddRange(component.Artifacts.Files.Select(file => new AssetDescriptor(file.DownloadSha256 + ".blob", file.DownloadLength, file.DownloadSha256)));
            var full = component.Artifacts.FullPackage!;
            descriptors.Add(new(full.DownloadSha256 + ".blob", full.DownloadLength, full.DownloadSha256));
        }
        var grouped = descriptors.GroupBy(asset => asset.AssetName, StringComparer.Ordinal);
        foreach (var group in grouped)
        {
            if (group.Distinct().Count() != 1) throw new ReleaseBuildException("release.assetConflict");
            yield return group.First();
        }
    }

    private static async Task VerifyComponentAsync(string release, FileTreeArtifacts artifacts, CancellationToken cancellationToken)
    {
        foreach (var artifact in artifacts.Files)
        {
            await using var blob = File.OpenRead(Path.Combine(release, "assets", artifact.DownloadSha256 + ".blob"));
            using var gzip = artifact.Encoding == UpdateBlobEncoding.Gzip ? new GZipStream(blob, CompressionMode.Decompress) : null;
            var input = (Stream?)gzip ?? blob;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long count = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                count = checked(count + read);
                if (count > artifact.File.SizeBytes) throw new ReleaseBuildException("release.contentMismatch");
                hash.AppendData(buffer.AsSpan(0, read));
            }
            if (count != artifact.File.SizeBytes || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != artifact.File.Sha256)
                throw new ReleaseBuildException("release.contentMismatch");
        }
        var parent = Path.GetDirectoryName(release)!;
        var name = Path.GetFileName(release) + ".verify-" + Guid.NewGuid().ToString("N");
        var work = Path.Combine(parent, name);
        Directory.CreateDirectory(work);
        try
        {
            using var downloader = new ResumableAssetDownloader(new LocalAssetHandler(Path.Combine(release, "assets")));
            var staged = await new FileTreeStager(downloader, new()).StagePackageAsync(Path.Combine(work, "absent"),
                Path.Combine(work, "stage"), Path.Combine(work, "cache"), artifacts,
                package => [new("local-release", new Uri("https://release.invalid/" + package.DownloadSha256 + ".blob"))], cancellationToken);
            await FileTreePlanner.VerifyAsync(staged.StageRoot, artifacts.Files.Select(file => file.File).ToArray(), cancellationToken);
        }
        finally { ReleaseFiles.DeleteOwnedWork(work, parent, name); }
    }

    private static string Resolve(string baseDirectory, string path)
    {
        var result = Path.GetFullPath(path, Path.GetFullPath(baseDirectory));
        ReleaseFiles.RequireSafePath(result);
        return result;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool Identifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

internal sealed class LocalAssetHandler(string root) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var name = request.RequestUri!.AbsolutePath.TrimStart('/');
        if (name.Length != 69 || !name.EndsWith(".blob", StringComparison.Ordinal) || !name[..64].All(Uri.IsHexDigit))
            throw new ReleaseBuildException("release.assetInvalid");
        var path = Path.Combine(root, name);
        ReleaseFiles.RequireSafePath(path);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) };
        return Task.FromResult(response);
    }
}
