using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MabiTool.Infrastructure.Updates;

public sealed class FileTreeException(string code, string? path = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? PathValue { get; } = path;
}

public static class FileTreePlanner
{
    public static FileTreePlan Plan(string currentRoot, IReadOnlyList<ManifestFile> target) =>
        PlanAsync(currentRoot, target).GetAwaiter().GetResult();

    public static async Task<FileTreePlan> PlanAsync(string currentRoot, IReadOnlyList<ManifestFile> target,
        UpdateIoBudget? budget = null, CancellationToken cancellationToken = default)
    {
        budget ??= new();
        using var activity = budget.EnterActivity("update.verification");
        var targetMap = ValidateManifest(target);
        var current = await ReadCurrentAsync(currentRoot, budget, cancellationToken).ConfigureAwait(false);
        var changes = targetMap.Values.Select(item => new FileChange(
            current.TryGetValue(item.RelativePath, out var existing) && SameContent(existing, item) ? FileChangeKind.Reuse : FileChangeKind.Download,
            item.RelativePath, item.SizeBytes, item.Sha256)).ToList();
        changes.AddRange(current.Values.Where(item => !targetMap.ContainsKey(item.RelativePath))
            .Select(item => new FileChange(FileChangeKind.Delete, item.RelativePath, 0, string.Empty)));
        return new(changes.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray(), TreeHash(targetMap.Values));
    }

    public static async Task VerifyAsync(string root, IReadOnlyList<ManifestFile> target, CancellationToken cancellationToken = default,
        UpdateIoBudget? budget = null)
    {
        budget ??= new();
        using var activity = budget.EnterActivity("update.verification");
        var expected = ValidateManifest(target);
        var actual = await ReadCurrentAsync(root, budget, cancellationToken).ConfigureAwait(false);
        if (actual.Count != expected.Count || expected.Values.Any(item => !actual.TryGetValue(item.RelativePath, out var found) ||
            !SameContent(found, item))) throw new FileTreeException("update.targetTreeMismatch");
    }

    internal static bool SameContent(ManifestFile a, ManifestFile b) => a.SizeBytes == b.SizeBytes &&
        string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase);

    internal static Dictionary<string, ManifestFile> ValidateManifest(IReadOnlyList<ManifestFile> files)
    {
        if (files.Count > 100_000) throw new FileTreeException("update.manifestTooLarge");
        var result = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var path = UpdatePaths.Normalize(file.RelativePath);
            if (file.SizeBytes is < 0 or > 20L * 1024 * 1024 * 1024 || file.Sha256.Length != 64 ||
                !file.Sha256.All(Uri.IsHexDigit) || !result.TryAdd(path, file with { RelativePath = path, Sha256 = file.Sha256.ToLowerInvariant() }))
                throw new FileTreeException("update.manifestPathInvalid", path);
        }
        foreach (var path in result.Keys)
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (result.ContainsKey(path[..slash])) throw new FileTreeException("update.manifestPathInvalid", path);
        return result;
    }

    internal static async Task<Dictionary<string, ManifestFile>> ReadCurrentAsync(string root, UpdateIoBudget budget, CancellationToken token)
    {
        var files = UpdatePaths.EnumerateFiles(root, token);
        var contents = new ManifestFile[files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = token
        }, async (index, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (relative, path) = files[index];
            UpdatePaths.RejectLinks(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                UpdateIoBudget.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var content = await budget.CopyAndHashAsync(stream, null, 20L * 1024 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            contents[index] = new(relative, content.Length, content.Hash);
        }).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var result = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var content in contents)
        {
            if (!result.TryAdd(content.RelativePath, content))
                throw new FileTreeException("update.targetTreeMismatch", content.RelativePath);
        }
        return result;
    }

    internal static string TreeHash(IEnumerable<ManifestFile> files)
    {
        var canonical = string.Join("\n", files.OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .Select(item => $"{item.RelativePath}\0{item.SizeBytes.ToString(CultureInfo.InvariantCulture)}\0{item.Sha256.ToLowerInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
