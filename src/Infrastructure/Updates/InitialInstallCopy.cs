namespace MabiTool.Infrastructure.Updates;

internal static class InitialInstallCopy
{
    internal static async Task CopyAsync(string source, string target, IReadOnlyList<ManifestFile> files,
        Func<InitialInstallPersistencePoint, Task>? observed, CancellationToken token)
    {
        var expected = FileTreePlanner.ValidateManifest(files);
        UpdatePaths.RejectLinks(target); Directory.CreateDirectory(target);
        RequireDirectories(target, files);
        foreach (var (relative, _) in UpdatePaths.EnumerateFiles(target, token))
            if (!expected.ContainsKey(relative) && !(relative.EndsWith(".initial-part", StringComparison.Ordinal) &&
                expected.ContainsKey(relative[..^13]))) throw new UpdatePlanException("update.initialInstallNotEmpty");
        var budget = new UpdateIoBudget();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (file, ct) =>
        {
            var from = UpdatePaths.Local(source, file.RelativePath); var to = UpdatePaths.Local(target, file.RelativePath);
            if (File.Exists(to))
            {
                await using var existing = await UpdatePaths.OpenPublishedReadAsync(to, ct).ConfigureAwait(false);
                var actual = await budget.CopyAndHashAsync(existing, null, file.SizeBytes, ct).ConfigureAwait(false);
                if (actual.Length != file.SizeBytes || actual.Hash != file.Sha256) throw new FileTreeException("update.targetTreeMismatch");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!); var partial = to + ".initial-part";
            UpdatePaths.RejectLinks(partial);
            await using var input = await UpdatePaths.OpenPublishedReadAsync(from, ct).ConfigureAwait(false);
            await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                if (output.Length > file.SizeBytes) throw new FileTreeException("update.targetTreeMismatch");
                var length = output.Length; var left = new byte[65536]; var right = new byte[65536];
                while (length > 0)
                {
                    var count = (int)Math.Min(length, left.Length);
                    await input.ReadExactlyAsync(left.AsMemory(0, count), ct).ConfigureAwait(false);
                    await output.ReadExactlyAsync(right.AsMemory(0, count), ct).ConfigureAwait(false);
                    if (!left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count))) throw new FileTreeException("update.targetTreeMismatch");
                    length -= count;
                }
                input.Position = 0; output.Position = 0; output.SetLength(0);
                var copied = await budget.CopyAndHashAsync(input, output, file.SizeBytes, ct).ConfigureAwait(false);
                if (copied.Length != file.SizeBytes || copied.Hash != file.Sha256) throw new FileTreeException("update.targetTreeMismatch");
                await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(true);
            }
            File.Move(partial, to, false);
            if (observed is not null) await observed(InitialInstallPersistencePoint.FileCopied).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    internal static void RequireDirectories(string root, IReadOnlyList<ManifestFile> files)
    {
        if (!Directory.Exists(root)) return;
        _ = UpdatePaths.EnumerateFiles(root, CancellationToken.None);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var relative = UpdatePaths.Normalize(Path.GetRelativePath(root, directory)) + "/";
            if (!files.Any(file => file.RelativePath.StartsWith(relative, StringComparison.Ordinal)))
                throw new UpdatePlanException("update.initialInstallNotEmpty");
        }
    }
}
