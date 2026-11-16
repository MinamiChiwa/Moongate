using System.IO.Compression;

namespace MabiTool.Infrastructure.Updates;

public sealed partial class FileTreeStager
{
    public static FileTreeRebuildCapabilities Capabilities { get; } = new(false, Array.Empty<string>());

    public Task<FileTreeStageResult> StageSelectedAsync(UpdateArtifactKind selected, string currentRoot, string stageRoot, string cacheRoot,
        FileTreeArtifacts artifacts, Func<UpdateFileArtifact, IReadOnlyList<AssetDownloadSource>> sources,
        Func<FileTreePackage, IReadOnlyList<AssetDownloadSource>> packageSources, CancellationToken cancellationToken = default,
        Func<AssetDownloadResult, CancellationToken, Task>? verified = null) => selected switch
        {
            UpdateArtifactKind.FullPackage => StagePackageAsync(currentRoot, stageRoot, cacheRoot, artifacts, packageSources, cancellationToken, verified),
            // 首版不执行二进制补丁，即使收到旧计划的补丁选择也按完整文件重建。
            UpdateArtifactKind.FileIncremental or UpdateArtifactKind.BinaryPatch =>
                StageWithFallbackAsync(currentRoot, stageRoot, cacheRoot, artifacts, sources, packageSources, cancellationToken, verified),
            _ => throw new FileTreeException("update.artifactUnsupported")
        };

    public async Task<FileTreeStageResult> StageWithFallbackAsync(string currentRoot, string stageRoot, string cacheRoot,
        FileTreeArtifacts artifacts, Func<UpdateFileArtifact, IReadOnlyList<AssetDownloadSource>> sources,
        Func<FileTreePackage, IReadOnlyList<AssetDownloadSource>> packageSources, CancellationToken cancellationToken = default,
        Func<AssetDownloadResult, CancellationToken, Task>? verified = null)
    {
        using var activity = budget.EnterActivity("update.staging");
        artifacts = SnapshotArtifacts(artifacts);
        try { return await StageAsync(currentRoot, stageRoot, cacheRoot, artifacts, sources, cancellationToken, verified).ConfigureAwait(false); }
        catch (AssetDownloadException error) when (artifacts.FullPackage is not null && error.Code is
            "update.allSourcesUnavailable" or "update.assetDownloadFailed" or "update.assetHashMismatch" or "update.assetLengthMismatch")
        {
            return await StagePackageAsync(currentRoot, stageRoot, cacheRoot, artifacts, packageSources, cancellationToken, verified).ConfigureAwait(false);
        }
        catch (FileTreeException error) when (artifacts.FullPackage is not null && error.Code is
            "update.assetInvalid" or "update.assetHashMismatch" or "update.assetLengthMismatch")
        {
            return await StagePackageAsync(currentRoot, stageRoot, cacheRoot, artifacts, packageSources, cancellationToken, verified).ConfigureAwait(false);
        }
    }

    public async Task<FileTreeStageResult> StagePackageAsync(string currentRoot, string stageRoot, string cacheRoot,
        FileTreeArtifacts artifacts, Func<FileTreePackage, IReadOnlyList<AssetDownloadSource>> sources,
        CancellationToken cancellationToken = default, Func<AssetDownloadResult, CancellationToken, Task>? verified = null)
    {
        using var activity = budget.EnterActivity("update.staging");
        artifacts = SnapshotArtifacts(artifacts);
        var manifest = ValidateArtifacts(artifacts);
        var package = artifacts.FullPackage ?? throw new FileTreeException("update.fullPackageMissing");
        if (!package.TargetTreeHash.Equals(artifacts.TargetTreeHash, StringComparison.OrdinalIgnoreCase) ||
            package.DownloadLength is < 0 or > 20L * 1024 * 1024 * 1024 || package.DownloadSha256.Length != 64 ||
            !package.DownloadSha256.All(Uri.IsHexDigit)) throw new FileTreeException("update.assetMetadataInvalid");
        currentRoot = Path.GetFullPath(currentRoot);
        stageRoot = Path.GetFullPath(stageRoot);
        cacheRoot = Path.GetFullPath(cacheRoot);
        UpdatePaths.RequireDisjoint(currentRoot, stageRoot, cacheRoot);
        var work = stageRoot + ".work-" + Guid.NewGuid().ToString("N");
        UpdatePaths.RequireDisjoint(currentRoot, work, cacheRoot);
        UpdatePaths.RejectLinks(stageRoot + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(stageRoot)!);
        await using var ownership = new FileStream(stageRoot + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(stageRoot) || File.Exists(stageRoot)) throw new FileTreeException("update.stageExists");
        Directory.CreateDirectory(work);
        try
        {
            var blobPath = ResumableAssetDownloader.CachePath(cacheRoot, package.DownloadSha256);
            var acquired = await downloader.DownloadAsync(new(sources(package), blobPath, package.DownloadLength, package.DownloadSha256), cancellationToken).ConfigureAwait(false);
            if (verified is not null) await verified(acquired, cancellationToken).ConfigureAwait(false);
            UpdatePaths.RejectLinks(blobPath);
            await using (var blob = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                UpdateIoBudget.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var compressed = await budget.CopyAndHashAsync(blob, null, package.DownloadLength, cancellationToken).ConfigureAwait(false);
                if (compressed.Length != package.DownloadLength || !compressed.Hash.Equals(package.DownloadSha256, StringComparison.OrdinalIgnoreCase))
                    throw new FileTreeException("update.assetHashMismatch");
                ZipPackageBounds.Validate(blob, manifest.Count);
                blob.Position = 0;
                using var archive = new ZipArchive(blob, ZipArchiveMode.Read, leaveOpen: true);
                if (archive.Entries.Count != manifest.Count) throw new FileTreeException("update.targetTreeMismatch");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // 在展开任何条目前检查整份目录，未知条目、链接和重复路径直接拒绝。
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = UpdatePaths.Normalize(entry.FullName);
                    var unixKind = (entry.ExternalAttributes >> 16) & 0xf000;
                    if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || unixKind is not (0 or 0x8000) ||
                        !seen.Add(relative) || !manifest.TryGetValue(relative, out var file) || entry.Length != file.SizeBytes)
                        throw new FileTreeException("update.packageEntryInvalid", relative);
                }
                foreach (var entry in archive.Entries)
                {
                    var file = manifest[UpdatePaths.Normalize(entry.FullName)];
                    var outputPath = UpdatePaths.Local(work, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    await using var input = entry.Open();
                    await using var output = File.Create(outputPath);
                    var actual = await budget.CopyAndHashAsync(input, output, file.SizeBytes, cancellationToken).ConfigureAwait(false);
                    if (actual.Length != file.SizeBytes || !actual.Hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new FileTreeException("update.assetHashMismatch");
                }
            }
            await FileTreePlanner.VerifyAsync(work, manifest.Values.ToArray(), cancellationToken, budget).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            UpdatePaths.RejectLinks(stageRoot);
            await UpdatePaths.PublishOwnedDirectoryAsync(work, stageRoot, cancellationToken).ConfigureAwait(false);
            return new(stageRoot, artifacts.TargetTreeHash, 0, manifest.Count);
        }
        catch (InvalidDataException) { throw new FileTreeException("update.packageInvalid"); }
        catch (IOException error) when (UpdatePaths.IsDiskFull(error)) { throw new FileTreeException("update.diskSpaceInsufficient"); }
        finally { UpdatePaths.DeleteOwnedTree(work); }
    }
}
