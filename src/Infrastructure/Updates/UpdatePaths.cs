namespace MabiTool.Infrastructure.Updates;

internal static class UpdatePaths
{
    // 已发布记录只经原子替换修改；读者固定旧文件，并允许发布和收尾删除。
    internal static async Task<FileStream> OpenPublishedReadAsync(string path, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            RejectLinks(path);
            try { return new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous); }
            catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 4 && (error.HResult & 0xffff) is 32 or 33)
            {
                await delay(TimeSpan.FromMilliseconds(50 * (1 << attempt)), token).ConfigureAwait(false);
            }
        }
    }

    internal static async Task ReplaceOwnedFileAsync(string source, string destination, CancellationToken token,
        Action<string, string>? replace = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        replace ??= (from, to) => File.Replace(from, to, null);
        delay ??= Task.Delay;
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            RejectLinks(source);
            RejectLinks(destination);
            try { replace(source, destination); return; }
            // 1175 保留原来的两个文件名；1176/1177 可能发生部分移动，交由事务恢复。
            catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 4 &&
                (error.HResult & 0xffff) is 5 or 32 or 33 or 1175 && File.Exists(source) && File.Exists(destination))
            {
                await delay(TimeSpan.FromMilliseconds(50 * (1 << attempt)), token).ConfigureAwait(false);
            }
        }
    }

    internal static async Task PublishOwnedDirectoryAsync(string work, string destination, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            RejectLinks(work);
            RejectLinks(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new FileTreeException("update.stageExists");
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

    private static void Reject(string path) => throw new FileTreeException("update.pathRejected", path);

    internal static string Local(string root, string relative)
    {
        var result = Path.Combine(Path.GetFullPath(root), Normalize(relative).Replace('/', Path.DirectorySeparatorChar));
        RejectLinks(result);
        return result;
    }

    // 路径超过 MAX_PATH 时，dotnet 宿主给出的程序集位置带 \\?\ 扩展前缀；比较前还原为普通完整路径。
    internal static bool SameLocalFile(string actual, string expected) =>
        Path.GetFullPath(WithoutExtendedPrefix(actual)).Equals(Path.GetFullPath(WithoutExtendedPrefix(expected)), StringComparison.OrdinalIgnoreCase);

    private static string WithoutExtendedPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] :
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new FileTreeException("update.pathRejected", current);
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
                    throw new FileTreeException("update.rootOverlap");
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
                if (++count > 100_000) throw new FileTreeException("update.manifestTooLarge");
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
