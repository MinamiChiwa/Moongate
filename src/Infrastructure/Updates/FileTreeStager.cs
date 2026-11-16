using System.IO.Compression;

namespace MabiTool.Infrastructure.Updates;

/// <summary>读取可信清单指定的内容，构建独立目标树；全部验证后通过同卷改名发布暂存目录。</summary>
public sealed partial class FileTreeStager(ResumableAssetDownloader downloader, UpdateIoBudget budget)
{
    public async Task<FileTreeStageResult> StageAsync(string currentRoot, string stageRoot, string cacheRoot,
        FileTreeArtifacts artifacts, Func<UpdateFileArtifact, IReadOnlyList<AssetDownloadSource>> sources,
        CancellationToken cancellationToken = default, Func<AssetDownloadResult, CancellationToken, Task>? verified = null)
    {
        using var activity = budget.EnterActivity("update.staging");
        artifacts = SnapshotArtifacts(artifacts);
        var manifest = ValidateArtifacts(artifacts);
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
        var reused = 0;
        var downloaded = 0;
        try
        {
            // 在打开任何旧文件前完成整个旧树的路径检查。
            var current = UpdatePaths.EnumerateFiles(currentRoot, cancellationToken)
                .ToDictionary(item => item.Relative, item => item.Full, StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in artifacts.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = manifest[UpdatePaths.Normalize(artifact.File.RelativePath)];
                var outputPath = UpdatePaths.Local(work, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                var copied = false;
                if (current.TryGetValue(file.RelativePath, out var sourcePath))
                {
                    try
                    {
                        UpdatePaths.RejectLinks(sourcePath);
                        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            UpdateIoBudget.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (input.Length == file.SizeBytes)
                        {
                            await using var output = File.Create(outputPath);
                            var content = await budget.CopyAndHashAsync(input, output, file.SizeBytes, cancellationToken).ConfigureAwait(false);
                            copied = content.Length == file.SizeBytes && content.Hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch (IOException error) when (!UpdatePaths.IsDiskFull(error)) { /* 旧文件改变或被占用时改用目标完整文件。 */ }
                }
                if (copied) { reused++; continue; }
                if (File.Exists(outputPath)) File.Delete(outputPath);
                var blobPath = ResumableAssetDownloader.CachePath(cacheRoot, artifact.DownloadSha256);
                var acquired = await downloader.DownloadAsync(new(sources(artifact), blobPath, artifact.DownloadLength, artifact.DownloadSha256), cancellationToken)
                    .ConfigureAwait(false);
                if (verified is not null) await verified(acquired, cancellationToken).ConfigureAwait(false);
                UpdatePaths.RejectLinks(blobPath);
                await using (var blob = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    UpdateIoBudget.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    // 缓存命中后仍在同一只读句柄下校验，再解压，防止校验与使用之间替换内容。
                    var compressed = await budget.CopyAndHashAsync(blob, null, artifact.DownloadLength, cancellationToken).ConfigureAwait(false);
                    if (compressed.Length != artifact.DownloadLength || !compressed.Hash.Equals(artifact.DownloadSha256, StringComparison.OrdinalIgnoreCase))
                        throw new FileTreeException("update.assetHashMismatch");
                    blob.Position = 0;
                    using var gzip = artifact.Encoding == UpdateBlobEncoding.Gzip ? new GZipStream(blob, CompressionMode.Decompress, leaveOpen: true) : null;
                    await using var output = File.Create(outputPath);
                    var content = await budget.CopyAndHashAsync(gzip is null ? blob : gzip, output, file.SizeBytes, cancellationToken).ConfigureAwait(false);
                    if (content.Length != file.SizeBytes || !content.Hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new FileTreeException("update.assetHashMismatch");
                }
                downloaded++;
            }
            await FileTreePlanner.VerifyAsync(work, manifest.Values.ToArray(), cancellationToken, budget).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            UpdatePaths.RejectLinks(stageRoot);
            await UpdatePaths.PublishOwnedDirectoryAsync(work, stageRoot, cancellationToken).ConfigureAwait(false);
            return new(stageRoot, artifacts.TargetTreeHash, reused, downloaded);
        }
        catch (InvalidDataException) { throw new FileTreeException("update.assetInvalid"); }
        catch (IOException error) when (UpdatePaths.IsDiskFull(error)) { throw new FileTreeException("update.diskSpaceInsufficient"); }
        finally { UpdatePaths.DeleteOwnedTree(work); }
    }

    internal static Dictionary<string, ManifestFile> ValidateArtifacts(FileTreeArtifacts artifacts)
    {
        if (artifacts.Files.Count > 100_000) throw new FileTreeException("update.manifestTooLarge");
        var manifest = FileTreePlanner.ValidateManifest(artifacts.Files.Select(item => item.File).ToArray());
        if (!FileTreePlanner.TreeHash(manifest.Values).Equals(artifacts.TargetTreeHash, StringComparison.OrdinalIgnoreCase))
            throw new FileTreeException("update.targetTreeMismatch");
        foreach (var item in artifacts.Files)
            if (item.DownloadLength is < 0 or > 20L * 1024 * 1024 * 1024 || item.DownloadSha256.Length != 64 ||
                !item.DownloadSha256.All(Uri.IsHexDigit) || !Enum.IsDefined(item.Encoding) ||
                item.Encoding == UpdateBlobEncoding.Identity && (item.DownloadLength != item.File.SizeBytes ||
                    !item.DownloadSha256.Equals(item.File.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new FileTreeException("update.assetMetadataInvalid");
        return manifest;
    }

    private static FileTreeArtifacts SnapshotArtifacts(FileTreeArtifacts artifacts)
    {
        if (artifacts.Files.Count > 100_000) throw new FileTreeException("update.manifestTooLarge");
        return artifacts with { Files = artifacts.Files.ToArray() };
    }
}
