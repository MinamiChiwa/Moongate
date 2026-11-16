using System.IO.Compression;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MabiTool.Infrastructure.Updates;

/// <summary>发行构建阶段生成 gzip 内容寻址资产和完整文件清单，供后续签名发布使用。</summary>
public sealed class FileTreeArtifactBuilder(UpdateIoBudget? budget = null)
{
    private readonly UpdateIoBudget budget = budget ?? new();

    public async Task<FileTreeArtifacts> BuildAsync(string releaseRoot, string outputRoot, CancellationToken cancellationToken = default)
    {
        using var activity = budget.EnterActivity("update.artifact-build");
        releaseRoot = Path.GetFullPath(releaseRoot);
        outputRoot = Path.GetFullPath(outputRoot);
        UpdatePaths.RequireDisjoint(releaseRoot, outputRoot);
        var work = outputRoot + ".work-" + Guid.NewGuid().ToString("N");
        UpdatePaths.RequireDisjoint(releaseRoot, work);
        UpdatePaths.RejectLinks(outputRoot + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(outputRoot)!);
        await using var ownership = new FileStream(outputRoot + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!Directory.Exists(releaseRoot)) throw new DirectoryNotFoundException(releaseRoot);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot)) throw new FileTreeException("update.stageExists");
        var files = UpdatePaths.EnumerateFiles(releaseRoot, cancellationToken);
        Directory.CreateDirectory(Path.Combine(work, "blobs"));
        var artifacts = new List<UpdateFileArtifact>();
        try
        {
            foreach (var (relative, path) in files.OrderBy(item => item.Relative, StringComparer.Ordinal))
            {
                UpdatePaths.RejectLinks(path);
                var temporary = Path.Combine(work, "content.tmp");
                (long Length, string Hash) raw;
                bool executable;
                await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    UpdateIoBudget.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = File.Create(temporary))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    executable = HasEntryPoint(input);
                    using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
                    raw = await budget.CopyAndHashAsync(input, gzip, 20L * 1024 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                }
                (long Length, string Hash) compressed;
                await using (var input = File.OpenRead(temporary))
                    compressed = await budget.CopyAndHashAsync(input, null, 20L * 1024 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                var destination = ResumableAssetDownloader.CachePath(Path.Combine(work, "blobs"), compressed.Hash);
                if (File.Exists(destination)) File.Delete(temporary);
                else File.Move(temporary, destination);
                artifacts.Add(new(new(relative, raw.Length, raw.Hash, executable), compressed.Length, compressed.Hash, UpdateBlobEncoding.Gzip));
            }
            var result = new FileTreeArtifacts(artifacts.ToArray(), FileTreePlanner.TreeHash(artifacts.Select(item => item.File)));
            FileTreeStager.ValidateArtifacts(result);
            var packagePath = Path.Combine(work, "full.zip");
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                foreach (var artifact in result.Files)
                {
                    await budget.PauseVerificationAsync(cancellationToken).ConfigureAwait(false);
                    var entry = archive.CreateEntry(artifact.File.RelativePath, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var blob = File.OpenRead(ResumableAssetDownloader.CachePath(Path.Combine(work, "blobs"), artifact.DownloadSha256));
                    using var input = new GZipStream(blob, CompressionMode.Decompress);
                    await using var output = entry.Open();
                    var actual = await budget.CopyAndHashAsync(input, output, artifact.File.SizeBytes, cancellationToken).ConfigureAwait(false);
                    if (actual.Length != artifact.File.SizeBytes || actual.Hash != artifact.File.Sha256)
                        throw new FileTreeException("update.assetHashMismatch");
                }
            }
            await using (var package = File.OpenRead(packagePath))
            {
                ZipPackageBounds.Validate(package, result.Files.Count);
                package.Position = 0;
                var content = await budget.CopyAndHashAsync(package, null, 20L * 1024 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                result = result with { FullPackage = new(result.TargetTreeHash, content.Length, content.Hash) };
            }
            await using (var manifest = File.Create(Path.Combine(work, "files.json")))
                await JsonSerializer.SerializeAsync(manifest, result, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            UpdatePaths.RejectLinks(outputRoot);
            await UpdatePaths.PublishOwnedDirectoryAsync(work, outputRoot, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (IOException error) when (UpdatePaths.IsDiskFull(error)) { throw new FileTreeException("update.diskSpaceInsufficient"); }
        finally { UpdatePaths.DeleteOwnedTree(work); }
    }

    private static bool HasEntryPoint(Stream input)
    {
        var position = input.Position;
        try
        {
            if (input.ReadByte() != 'M' || input.ReadByte() != 'Z') return false;
            input.Position = position;
            using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
            var headers = pe.PEHeaders;
            if (headers.PEHeader is null || (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) == 0) return false;
            if (headers.CorHeader is { } cor)
            {
                if ((cor.Flags & CorFlags.NativeEntryPoint) != 0)
                    return (headers.CoffHeader.Characteristics & Characteristics.Dll) == 0 &&
                        cor.EntryPointTokenOrRelativeVirtualAddress != 0 && pe.GetSectionData(cor.EntryPointTokenOrRelativeVirtualAddress).Length > 0;
                if (!pe.HasMetadata) return false;
                var entry = MetadataTokens.Handle(cor.EntryPointTokenOrRelativeVirtualAddress);
                var metadata = pe.GetMetadataReader();
                if (entry.Kind != HandleKind.MethodDefinition || MetadataTokens.GetRowNumber((MethodDefinitionHandle)entry) <= 0 ||
                    MetadataTokens.GetRowNumber((MethodDefinitionHandle)entry) > metadata.MethodDefinitions.Count) return false;
                var rva = metadata.GetMethodDefinition((MethodDefinitionHandle)entry).RelativeVirtualAddress;
                return rva != 0 && pe.GetSectionData(rva).Length > 0;
            }
            return (headers.CoffHeader.Characteristics & Characteristics.Dll) == 0 && headers.PEHeader.AddressOfEntryPoint != 0 &&
                pe.GetSectionData(headers.PEHeader.AddressOfEntryPoint).Length > 0;
        }
        catch (BadImageFormatException) { return false; }
        catch (ArgumentException) { return false; }
        finally { input.Position = position; }
    }
}
