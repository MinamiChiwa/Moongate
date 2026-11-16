namespace MabiTool.Infrastructure.Updates;

public sealed record BinaryPatchArtifact(string BaseManifestSha256, string TargetManifestSha256, string Algorithm, long SizeBytes, string Sha256);
public sealed record FullPackageArtifact(string TargetManifestSha256, long SizeBytes, string Sha256);
public enum UpdateArtifactKind { FileIncremental, BinaryPatch, FullPackage }
public sealed record UpdateArtifactChoice(UpdateArtifactKind Kind, long DownloadBytes, string Reason, BinaryPatchArtifact? Patch = null, FullPackageArtifact? FullPackage = null);

public static class UpdateArtifactSelector
{
    public static UpdateArtifactChoice Select(string? baseManifestSha256, string targetManifestSha256, long fileIncrementalBytes,
        long fullPackageBytes, FullPackageArtifact fullPackage, BinaryPatchArtifact? patch = null,
        IReadOnlySet<string>? supportedAlgorithms = null, long availableBytes = long.MaxValue)
    {
        if (!IsHash(targetManifestSha256) || !IsHash(fullPackage.Sha256) ||
            !targetManifestSha256.Equals(fullPackage.TargetManifestSha256, StringComparison.OrdinalIgnoreCase) || fileIncrementalBytes < 0 ||
            fullPackageBytes < 0 || fullPackage.SizeBytes < 0 || fullPackage.SizeBytes != fullPackageBytes || availableBytes < 0)
            throw new UpdatePlanException("update.manifestInvalid");
        if (patch is not null && string.Equals(patch.TargetManifestSha256, targetManifestSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(patch.BaseManifestSha256, baseManifestSha256, StringComparison.OrdinalIgnoreCase) &&
            IsHash(patch.Sha256) && patch.SizeBytes >= 0 && patch.SizeBytes * 100m < fileIncrementalBytes * 70m && patch.SizeBytes <= availableBytes &&
            supportedAlgorithms?.Contains(patch.Algorithm) == true)
            return new(UpdateArtifactKind.BinaryPatch, patch.SizeBytes, "supported exact-base patch", patch);
        if (fileIncrementalBytes <= fullPackageBytes && fileIncrementalBytes <= availableBytes)
            return new(UpdateArtifactKind.FileIncremental, fileIncrementalBytes, "file-level incremental");
        if (fullPackage.SizeBytes <= availableBytes)
            return new(UpdateArtifactKind.FullPackage, fullPackage.SizeBytes, "incremental unavailable or larger", FullPackage: fullPackage);
        throw new UpdatePlanException("update.diskSpaceInsufficient");
    }

    public static void ValidateRebuiltTarget(string expectedManifestSha256, string actualManifestSha256)
    {
        if (!string.Equals(expectedManifestSha256, actualManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdatePlanException("update.targetTreeMismatch");
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
