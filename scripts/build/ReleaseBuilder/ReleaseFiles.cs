using System.Security.Cryptography;

namespace MabiTool.ReleaseBuilder;

internal static class ReleaseFiles
{
    internal const long MaximumAssetBytes = 20L * 1024 * 1024 * 1024;

    internal static string FullPath(string path) => Path.GetFullPath(path);

    internal static void RequireSafePath(string path)
    {
        path = FullPath(path);
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ReleaseBuildException("release.pathRejected");
    }

    internal static void RequireDisjoint(string left, string right)
    {
        left = FullPath(left).TrimEnd(Path.DirectorySeparatorChar);
        right = FullPath(right).TrimEnd(Path.DirectorySeparatorChar);
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
            left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseBuildException("release.pathRejected");
    }

    internal static void DeleteOwnedWork(string path, string expectedParent, string expectedName)
    {
        path = FullPath(path);
        expectedParent = FullPath(expectedParent);
        if (Path.GetDirectoryName(path) != expectedParent || Path.GetFileName(path) != expectedName)
            throw new ReleaseBuildException("release.pathRejected");
        RequireSafePath(path);
        if (!Directory.Exists(path)) return;
        CheckChildren(path);
        Directory.Delete(path, recursive: true);
        static void CheckChildren(string directory)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                RequireSafePath(child);
                if (Directory.Exists(child)) CheckChildren(child);
            }
        }
    }

    internal static async Task<AssetDescriptor> DescribeAsync(string path, CancellationToken cancellationToken)
    {
        RequireSafePath(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > MaximumAssetBytes) throw new ReleaseBuildException("release.assetTooLarge");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
        return new(hash + ".blob", input.Length, hash);
    }

    internal static async Task<AssetDescriptor> AddAssetAsync(string source, string assetsRoot, CancellationToken cancellationToken)
    {
        var descriptor = await DescribeAsync(source, cancellationToken);
        var destination = Path.Combine(assetsRoot, descriptor.AssetName);
        RequireSafePath(destination);
        if (!File.Exists(destination))
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
            output.Flush(flushToDisk: true);
        }
        if (await DescribeAsync(destination, cancellationToken) != descriptor)
            throw new ReleaseBuildException("release.assetMismatch");
        return descriptor;
    }

    internal static async Task WriteDurableAsync(string path, byte[] bytes, bool replace, CancellationToken cancellationToken)
    {
        RequireSafePath(path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
