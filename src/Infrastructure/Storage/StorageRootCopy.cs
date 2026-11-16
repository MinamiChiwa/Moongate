using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Updates;

namespace MabiTool.Infrastructure;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StorageRootTree(IReadOnlyList<DataSnapshotFile> Files, IReadOnlyList<string> Directories);

internal static class StorageRootCopy
{
    private const int MaximumManifestBytes = 32 * 1024 * 1024;
    private static bool Excluded(string relative) => relative is ".mabitool-data-root.json" or "writer.lock" or "store/database.lock" or
        "store/main.sqlite-wal" or "store/main.sqlite-shm" or "store/main.sqlite-journal";
    public static async Task<StorageRootTree> CaptureAsync(string root, CancellationToken ct, string? work = null)
    {
        StoragePaths.ValidateLocal(root);
        var files = new List<DataSnapshotFile>(); var directories = new List<string>(); long total = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested(); StoragePaths.ValidateLocal(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested(); var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (Excluded(relative) || work is not null && (relative == work || relative.StartsWith(work + "/", StringComparison.Ordinal))) continue;
                _ = UpdatePaths.Local(root, relative); StoragePaths.ValidateLocal(path);
                if (directories.Count + files.Count + pending.Count >= SqliteStore.MaximumSnapshotEntries) throw new StorageException("storage.quotaExceeded", "数据根迁移超过条目预算");
                if (Directory.Exists(path)) { directories.Add(relative); pending.Push(path); continue; }
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                if (input.Length > SqliteStore.MaximumSnapshotFileBytes) throw new StorageException("storage.quotaExceeded", "数据根迁移文件超过单项预算");
                total = checked(total + input.Length);
                if (total > SqliteStore.MaximumSnapshotContentBytes) throw new StorageException("storage.quotaExceeded", "数据根迁移总量超过预算");
                files.Add(new(relative, input.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct))));
            }
        }
        return new(files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray(), directories.Order(StringComparer.Ordinal).ToArray());
    }

    public static string Publish(string bootstrap, string id, StorageRootTree tree)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tree, ContractJson.Options);
        if (bytes.Length > MaximumManifestBytes) throw new StorageException("storage.quotaExceeded", "数据根迁移清单超限");
        var destination = ManifestPath(bootstrap, id); var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (File.Exists(destination)) { _ = Load(bootstrap, id, digest); return digest; }
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, destination, false);
            return digest;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static StorageRootTree Load(string bootstrap, string id, string expected)
    {
        using var file = new FileStream(ManifestPath(bootstrap, id), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumManifestBytes) throw new StorageException("storage.assetDamaged", "迁移清单大小无效");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected) throw new StorageException("storage.assetDamaged", "迁移清单与固定日志不匹配");
        using var document = StrictJson.Parse(bytes, MaximumManifestBytes);
        var tree = document.RootElement.Deserialize<StorageRootTree>(ContractJson.Options) ?? throw new StorageException("storage.assetDamaged", "迁移清单为空");
        if (tree.Files.Count + tree.Directories.Count > SqliteStore.MaximumSnapshotEntries || tree.Files.Any(x => x.SizeBytes < 0 || x.SizeBytes > SqliteStore.MaximumSnapshotFileBytes ||
            x.Sha256.Length != 64 || x.Sha256.Any(c => !Uri.IsHexDigit(c))) || tree.Files.Sum(x => x.SizeBytes) > SqliteStore.MaximumSnapshotContentBytes ||
            tree.Files.Select(x => x.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tree.Files.Count ||
            tree.Directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tree.Directories.Count)
            throw new StorageException("storage.assetDamaged", "迁移清单条目或预算无效");
        return tree;
    }

    public static async Task CopyAsync(string original, string target, string id, StorageRootTree expected, CancellationToken ct, Func<string, Task>? persisted = null)
    {
        var relativeWork = "recovery/root-copy-" + id + ".work";
        var work = UpdatePaths.Local(target, relativeWork); Directory.CreateDirectory(work);
        var owner = Path.Combine(work, "owner.json");
        if (File.Exists(owner))
        {
            if (new FileInfo(owner).Length != 32 || File.ReadAllText(owner) != id) throw new StorageException("storage.assetDamaged", "迁移暂存目录归属不匹配");
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(work).Any()) throw new StorageException("storage.assetDamaged", "迁移暂存目录含未知内容");
            using var marker = new FileStream(owner, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            marker.Write(System.Text.Encoding.ASCII.GetBytes(id)); marker.Flush(true);
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(work))
            if (Path.GetFileName(path) != "owner.json" && (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^[0-9]{6}\\.part$") || Directory.Exists(path)))
                throw new StorageException("storage.assetDamaged", "迁移暂存目录含未知内容");
        foreach (var directory in expected.Directories) Directory.CreateDirectory(UpdatePaths.Local(target, directory));
        for (var i = 0; i < expected.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); var file = expected.Files[i]; var destination = UpdatePaths.Local(target, file.RelativePath);
            if (File.Exists(destination)) { await VerifyFile(destination, file, ct); continue; }
            var source = UpdatePaths.Local(original, file.RelativePath); var part = Path.Combine(work, i.ToString("D6") + ".part");
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                if (input.Length != file.SizeBytes) throw new StorageException("storage.planStale", "迁移源文件大小发生变化");
                await input.CopyToAsync(output, 65536, ct); await output.FlushAsync(ct); output.Position = 0;
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(output, ct)) != file.Sha256) throw new StorageException("storage.planStale", "迁移源内容发生变化");
                output.Flush(true);
            }
            File.Move(part, destination, false);
            if (persisted is not null) await persisted("copy.file." + i);
        }
        await VerifyAsync(original, expected, false, ct);
        var current = await CaptureAsync(target, ct, relativeWork);
        if (!Same(current, expected, false)) throw new StorageException("storage.assetDamaged", "迁移目标树与固定清单不一致");
        UpdatePaths.DeleteOwnedTree(work);
        await VerifyAsync(target, expected, false, ct);
    }

    public static async Task VerifyAsync(string root, StorageRootTree expected, bool allowDatabaseMaintenance, CancellationToken ct)
    {
        var current = await CaptureAsync(root, ct);
        if (!Same(current, expected, allowDatabaseMaintenance)) throw new StorageException("storage.assetDamaged", "迁移树与固定证据不一致");
    }
    private static bool Same(StorageRootTree current, StorageRootTree expected, bool allowDatabaseMaintenance)
        => current.Directories.SequenceEqual(expected.Directories) &&
            current.Files.Where(x => !allowDatabaseMaintenance || x.RelativePath != "store/main.sqlite").SequenceEqual(
                expected.Files.Where(x => !allowDatabaseMaintenance || x.RelativePath != "store/main.sqlite"));
    private static async Task VerifyFile(string path, DataSnapshotFile expected, CancellationToken ct)
    {
        StoragePaths.ValidateLocal(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length != expected.SizeBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)) != expected.Sha256)
            throw new StorageException("storage.assetDamaged", "已复制迁移文件不匹配固定内容");
    }
    private static string ManifestPath(string bootstrap, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new StorageException("storage.pathRejected", "迁移事务身份无效");
        return StoragePaths.ValidateLocal(Path.Combine(bootstrap, "data-migration-" + id + ".json"));
    }
}
