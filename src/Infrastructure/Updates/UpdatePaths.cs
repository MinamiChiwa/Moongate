namespace MabiTool.Infrastructure.Updates;

internal static class UpdatePaths
{
    internal static async Task PublishOwnedDirectoryAsync(string work, string destination, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            RejectLinks(work);
            RejectLinks(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new UpdatePackageException("update.stageExists");
            try { Directory.Move(work, destination); return; }
            catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 4 &&
                (error.HResult & 0xffff) is 5 or 32 or 33 && !Directory.Exists(destination) && !File.Exists(destination))
            {
                await delay(TimeSpan.FromMilliseconds(50 * (1 << attempt)), token).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsDiskFull(IOException error) => (error.HResult & 0xffff) is 28 or 39 or 112;

    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || Path.IsPathRooted(path)) Reject(path);
        var parts = path.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(ch => char.IsControl(ch) || "<>:\"|?*".Contains(ch))) Reject(path);
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                "123456789¹²³".Contains(stem[3])) Reject(path);
        }
        return string.Join('/', parts);
    }

    private static void Reject(string path) => throw new UpdatePackageException("update.pathRejected", path);

    internal static string Local(string root, string relative)
    {
        var result = Path.Combine(Path.GetFullPath(root), Normalize(relative).Replace('/', Path.DirectorySeparatorChar));
        RejectLinks(result);
        return result;
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UpdatePackageException("update.pathRejected", current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void RequireDisjoint(params string[] roots)
    {
        var paths = roots.Select(Path.GetFullPath).Select(path => Path.TrimEndingDirectorySeparator(path)).ToArray();
        foreach (var path in paths) RejectLinks(path);
        for (var i = 0; i < paths.Length; i++)
            for (var j = i + 1; j < paths.Length; j++)
                if (Contains(paths[i], paths[j]) || Contains(paths[j], paths[i]))
                    throw new UpdatePackageException("update.rootOverlap");
    }

    private static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<(string Relative, string Full)> EnumerateFiles(string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectLinks(root);
        var files = new List<(string, string)>();
        if (!Directory.Exists(root)) return files;
        var pending = new Stack<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(Path.GetFullPath(root));
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            RejectLinks(directory);
            foreach (var entryInfo in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (++count > 100_000) throw new UpdatePackageException("update.manifestTooLarge");
                var entry = entryInfo.FullName;
                var attributes = entryInfo.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0) Reject(entry);
                var relative = Normalize(Path.GetRelativePath(root, entry));
                if (!names.Add(relative)) Reject(relative);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else files.Add((relative, entry));
            }
        }
        return files;
    }

    // 只清理本次创建的私有工作目录，逐项拒绝链接。
    internal static void DeleteOwnedTree(string root)
    {
        if (!Directory.Exists(root)) return;
        RejectLinks(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) DeleteOwnedTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(root);
    }
}
