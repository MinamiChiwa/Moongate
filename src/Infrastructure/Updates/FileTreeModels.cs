namespace MabiTool.Infrastructure.Updates;

public sealed record ManifestFile(string RelativePath, long SizeBytes, string Sha256, bool Executable = false);
public enum FileChangeKind { Reuse, Download, Delete }
public sealed record FileChange(FileChangeKind Kind, string RelativePath, long SizeBytes, string Sha256);
public sealed record FileTreePlan(IReadOnlyList<FileChange> Changes, string TargetTreeHash);
public enum UpdateBlobEncoding { Identity, Gzip }
public sealed record UpdateFileArtifact(ManifestFile File, long DownloadLength, string DownloadSha256, UpdateBlobEncoding Encoding);
public sealed record FileTreePackage(string TargetTreeHash, long DownloadLength, string DownloadSha256);
public sealed record FileTreeArtifacts(IReadOnlyList<UpdateFileArtifact> Files, string TargetTreeHash, FileTreePackage? FullPackage = null);
public sealed record FileTreeStageResult(string StageRoot, string TargetTreeHash, int ReusedFiles, int DownloadedFiles);
public sealed record FileTreeRebuildCapabilities(bool BinaryPatch, IReadOnlyList<string> PatchAlgorithms);
