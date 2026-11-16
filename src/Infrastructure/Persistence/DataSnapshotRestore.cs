using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

internal sealed record DataSnapshotRestorePlan(string TransactionId, string SnapshotId, string ManifestSha256);
internal sealed record DataSnapshotRestoreResult(DataSnapshotRestorePlan Plan);
internal enum DataSnapshotRestorePersistencePoint { Prepared, AssetsRetained, AssetsRestored, SettingsRetained, SettingsRestored }

public sealed partial class SqliteStore
{
    private sealed record RestoreTree(IReadOnlyList<DataSnapshotFile> Files, IReadOnlyList<string> Directories);
    private sealed record FileRestoreIntent(int SchemaVersion, string TransactionId, DataSnapshotDescriptor Snapshot, RestoreTree Original);
    private enum RestoreDirectoryState { Pending, Retained, Applied }
    private static readonly string[] RestoreRoots = ["assets", "settings"];

    /// <summary>数据库替换前固定文件目标与原内容证据；返回的摘要须持久保存到安装日志。</summary>
    internal static async Task<DataSnapshotRestorePlan> PrepareDataSnapshotRestoreOwnedAsync(DataRootLease lease,
        DataRootLease.DatabaseOwnership ownership, string transactionId, DataSnapshotDescriptor snapshot,
        CancellationToken token = default, DataSnapshotRestorePlan? expected = null,
        DataSnapshotRestoreResult? completed = null, Func<DataSnapshotRestorePersistencePoint, Task>? persisted = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var usage = ownership.Use(lease);
        if (!SnapshotIdValid(transactionId)) throw BackupInvalid("文件恢复事务身份无效");
        snapshot = JsonSerializer.Deserialize<DataSnapshotDescriptor>(JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJson), SnapshotJson)!;
        ValidateDataSnapshot(snapshot, lease.Paths, snapshot.SnapshotId);
        if (snapshot.Database.DatabaseId != lease.DatabaseId) throw BackupInvalid("文件快照数据库身份不匹配");
        snapshot = await VerifyDataSnapshotAsync(lease.Paths, snapshot.SnapshotId, snapshot, token).ConfigureAwait(false);
        var root = lease.Paths.Resolve("recovery", "files-" + transactionId);
        if (Directory.Exists(root))
        {
            var loaded = await LoadFileRestoreAsync(root, transactionId, snapshot, expected, token).ConfigureAwait(false);
            if (completed is not null && completed.Plan != loaded.Plan) throw BackupInvalid("文件恢复结果身份不匹配");
            var states = await ValidateFileRestoreStateAsync(lease.Paths, root, loaded.Intent, completed is not null, token).ConfigureAwait(false);
            if (expected is null && states.Any(state => state != RestoreDirectoryState.Pending))
                throw BackupInvalid("安装日志尚未固定文件恢复意图，目录已被移动");
            return loaded.Plan;
        }
        if (expected is not null || completed is not null || File.Exists(root)) throw BackupInvalid("文件恢复缺少固定意图");
        var original = await ReadOriginalRestoreTreeAsync(lease.Paths, token).ConfigureAwait(false);
        var intent = new FileRestoreIntent(1, transactionId, snapshot, original);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent, SnapshotJson);
        if (bytes.Length > MaximumSnapshotManifestBytes) throw BackupInvalid("文件恢复意图超限");
        var required = snapshot.Files.Sum(file => file.SizeBytes);
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < required + required / 5 + MaximumSnapshotManifestBytes)
            throw new StorageException("storage.diskSpaceInsufficient", "文件恢复暂存空间不足");
        var work = root + ".work-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(Path.Combine(work, "stage"));
            Directory.CreateDirectory(Path.Combine(work, "retained"));
            // 顶层目录始终保留，即使快照只含空树。
            foreach (var name in RestoreRoots) LocalDiscoverySecurity.EnsureDirectory(UpdatePaths.Local(work, "stage/" + name));
            foreach (var directory in snapshot.Directories) Directory.CreateDirectory(UpdatePaths.Local(work, "stage/" + directory));
            var source = lease.Paths.Resolve("backups", "data", snapshot.SnapshotId);
            foreach (var file in snapshot.Files)
                await CopySnapshotFileAsync(UpdatePaths.Local(source, file.RelativePath), UpdatePaths.Local(work, "stage/" + file.RelativePath), file, token).ConfigureAwait(false);
            var final = await ReadOriginalRestoreTreeAsync(lease.Paths, token).ConfigureAwait(false);
            if (!SameRestoreTree(original, final)) throw BackupInvalid("文件恢复准备期间原内容变化");
            await using (var manifest = new FileStream(Path.Combine(work, "manifest.json"), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await manifest.WriteAsync(bytes, token).ConfigureAwait(false);
                await manifest.FlushAsync(token).ConfigureAwait(false);
                manifest.Flush(true);
            }
            await ValidateFileRestoreStateAsync(lease.Paths, work, intent, false, token).ConfigureAwait(false);
            await UpdatePaths.PublishOwnedDirectoryAsync(work, root, token).ConfigureAwait(false);
            if (persisted is not null) await persisted(DataSnapshotRestorePersistencePoint.Prepared).ConfigureAwait(false);
            return new(transactionId, snapshot.SnapshotId, RestoreDigest(bytes));
        }
        finally { UpdatePaths.DeleteOwnedTree(work); }
    }

    /// <summary>在固定意图下保留原树并放入快照；每次重试核对目标和保留证据。</summary>
    internal static async Task<DataSnapshotRestoreResult> RestoreDataSnapshotFilesOwnedAsync(DataRootLease lease,
        DataRootLease.DatabaseOwnership ownership, DataSnapshotRestorePlan plan, DataSnapshotDescriptor snapshot,
        CancellationToken token = default, DataSnapshotRestoreResult? completed = null,
        Func<DataSnapshotRestorePersistencePoint, Task>? persisted = null)
    {
        using var usage = ownership.Use(lease);
        if (!SnapshotIdValid(plan.TransactionId) || !SnapshotIdValid(plan.SnapshotId) || !SnapshotHashValid(plan.ManifestSha256) ||
            snapshot.SnapshotId != plan.SnapshotId || snapshot.Database.DatabaseId != lease.DatabaseId ||
            completed is not null && completed.Plan != plan) throw BackupInvalid("文件恢复固定身份无效");
        snapshot = await VerifyDataSnapshotAsync(lease.Paths, snapshot.SnapshotId, snapshot, token).ConfigureAwait(false);
        var root = lease.Paths.Resolve("recovery", "files-" + plan.TransactionId);
        var loaded = await LoadFileRestoreAsync(root, plan.TransactionId, snapshot, plan, token).ConfigureAwait(false);
        var states = await ValidateFileRestoreStateAsync(lease.Paths, root, loaded.Intent, completed is not null, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // 进入移动后不再在两棵业务树之间响应取消，安装协调器持有同一生命周期所有权。
        for (var i = 0; i < RestoreRoots.Length; i++)
        {
            var name = RestoreRoots[i];
            var active = UpdatePaths.Local(lease.Paths.DataRoot, name);
            var retained = UpdatePaths.Local(root, "retained/" + name);
            var stage = UpdatePaths.Local(root, "stage/" + name);
            if (states[i] == RestoreDirectoryState.Applied) continue;
            if (states[i] == RestoreDirectoryState.Pending)
            {
                await VerifyRestoreDirectoryAsync(active, name, loaded.Intent.Original, CancellationToken.None).ConfigureAwait(false);
                await UpdatePaths.PublishOwnedDirectoryAsync(active, retained, CancellationToken.None).ConfigureAwait(false);
                if (persisted is not null) await persisted(i == 0 ? DataSnapshotRestorePersistencePoint.AssetsRetained : DataSnapshotRestorePersistencePoint.SettingsRetained).ConfigureAwait(false);
            }
            await VerifyRestoreDirectoryAsync(retained, name, loaded.Intent.Original, CancellationToken.None).ConfigureAwait(false);
            await VerifyRestoreDirectoryAsync(stage, name, TargetRestoreTree(snapshot), CancellationToken.None).ConfigureAwait(false);
            // 重新打开数据根会补建缺失的空目录；只有两份证据均匹配时允许移除此空占位。
            UpdatePaths.RejectLinks(active);
            if (Directory.Exists(active)) Directory.Delete(active, recursive: false);
            if (File.Exists(active)) throw BackupInvalid("文件恢复目标被未知文件占用");
            await UpdatePaths.PublishOwnedDirectoryAsync(stage, active, CancellationToken.None).ConfigureAwait(false);
            if (persisted is not null) await persisted(i == 0 ? DataSnapshotRestorePersistencePoint.AssetsRestored : DataSnapshotRestorePersistencePoint.SettingsRestored).ConfigureAwait(false);
        }
        await ValidateFileRestoreStateAsync(lease.Paths, root, loaded.Intent, true, CancellationToken.None).ConfigureAwait(false);
        return new(plan);
    }

    private static async Task<(FileRestoreIntent Intent, DataSnapshotRestorePlan Plan)> LoadFileRestoreAsync(string root,
        string transactionId, DataSnapshotDescriptor snapshot, DataSnapshotRestorePlan? expected, CancellationToken token)
    {
        await using var input = OpenSnapshotFile(UpdatePaths.Local(root, "manifest.json"));
        if (input.Length is <= 0 or > MaximumSnapshotManifestBytes) throw BackupInvalid("文件恢复意图大小无效");
        var bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var plan = new DataSnapshotRestorePlan(transactionId, snapshot.SnapshotId, RestoreDigest(bytes));
        if (expected is not null && expected != plan) throw BackupInvalid("文件恢复意图与固定摘要不匹配");
        FileRestoreIntent intent;
        try
        {
            using var document = StrictJson.Parse(bytes, MaximumSnapshotManifestBytes);
            intent = document.RootElement.Deserialize<FileRestoreIntent>(SnapshotJson) ?? throw BackupInvalid("文件恢复意图为空");
        }
        catch (JsonException error) { throw new StorageException("storage.backupInvalid", "文件恢复意图格式无效", error); }
        catch (TrustValidationException error) { throw new StorageException("storage.backupInvalid", "文件恢复意图格式无效", error); }
        if (intent.SchemaVersion != 1 || intent.TransactionId != transactionId || intent.Snapshot is null ||
            JsonSerializer.Serialize(intent.Snapshot, SnapshotJson) != JsonSerializer.Serialize(snapshot, SnapshotJson))
            throw BackupInvalid("文件恢复意图身份不匹配");
        ValidateOriginalRestoreTree(intent.Original);
        return (intent, plan);
    }

    private static async Task<RestoreDirectoryState[]> ValidateFileRestoreStateAsync(PathService paths, string root,
        FileRestoreIntent intent, bool completed, CancellationToken token)
    {
        RequireRestoreChildren(root, ["manifest.json", "stage", "retained"], true);
        RequireRestoreChildren(UpdatePaths.Local(root, "stage"), RestoreRoots, false);
        RequireRestoreChildren(UpdatePaths.Local(root, "retained"), RestoreRoots, false);
        var states = new RestoreDirectoryState[RestoreRoots.Length];
        var target = TargetRestoreTree(intent.Snapshot);
        for (var i = 0; i < RestoreRoots.Length; i++)
        {
            var name = RestoreRoots[i];
            var active = UpdatePaths.Local(paths.DataRoot, name);
            var stage = UpdatePaths.Local(root, "stage/" + name);
            var retained = UpdatePaths.Local(root, "retained/" + name);
            if (File.Exists(active) || File.Exists(stage) || File.Exists(retained)) throw BackupInvalid("文件恢复目录被文件占用");
            var hasStage = Directory.Exists(stage);
            var hasRetained = Directory.Exists(retained);
            if (hasRetained) await VerifyRestoreDirectoryAsync(retained, name, intent.Original, token).ConfigureAwait(false);
            if (hasStage) await VerifyRestoreDirectoryAsync(stage, name, target, token).ConfigureAwait(false);
            if (!hasRetained)
            {
                if (!hasStage || completed) throw BackupInvalid("文件恢复缺少保留证据");
                await VerifyRestoreDirectoryAsync(active, name, intent.Original, token).ConfigureAwait(false);
                states[i] = RestoreDirectoryState.Pending;
            }
            else if (hasStage)
            {
                if (completed || Directory.Exists(active) && Directory.EnumerateFileSystemEntries(active).Any())
                    throw BackupInvalid("文件恢复目标出现未知新内容");
                states[i] = RestoreDirectoryState.Retained;
            }
            else
            {
                await VerifyRestoreDirectoryAsync(active, name, target, token).ConfigureAwait(false);
                states[i] = RestoreDirectoryState.Applied;
            }
        }
        return states;
    }

    private static void RequireRestoreChildren(string root, string[] names, bool exact)
    {
        UpdatePaths.RejectLinks(root);
        if (!Directory.Exists(root)) throw BackupInvalid("文件恢复目录缺失");
        var actual = Directory.EnumerateFileSystemEntries(root).Take(names.Length + 1).ToArray();
        foreach (var path in actual)
        {
            UpdatePaths.RejectLinks(path);
            if (!names.Contains(Path.GetFileName(path), StringComparer.Ordinal)) throw BackupInvalid("文件恢复存在未知条目");
        }
        if (exact && actual.Length != names.Length) throw BackupInvalid("文件恢复目录不完整");
    }

    private static RestoreTree TargetRestoreTree(DataSnapshotDescriptor snapshot) =>
        new(snapshot.Files, snapshot.Directories.Concat(RestoreRoots).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    private static async Task VerifyRestoreDirectoryAsync(string root, string name, RestoreTree expected, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Directory.Exists(root)) throw BackupInvalid("文件恢复树缺失");
        LocalDiscoverySecurity.EnsureDirectory(root);
        var actual = ListSnapshotTree(root, token);
        var prefix = name + "/";
        var files = expected.Files.Where(file => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var directories = expected.Directories.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).Select(path => path[prefix.Length..]);
        if (!actual.Files.Select(file => prefix + file.Path).SequenceEqual(files.Select(file => file.RelativePath)) ||
            !actual.Directories.SequenceEqual(directories)) throw BackupInvalid("文件恢复树集合不匹配或恢复后已有新数据");
        foreach (var file in files)
            await VerifySnapshotFileAsync(UpdatePaths.Local(root, file.RelativePath[prefix.Length..]), file, token).ConfigureAwait(false);
    }

    private static async Task<RestoreTree> ReadOriginalRestoreTreeAsync(PathService paths, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var files = new List<DataSnapshotFile>();
        var directories = new List<string>();
        long total = 0;
        foreach (var name in RestoreRoots)
        {
            var root = UpdatePaths.Local(paths.DataRoot, name);
            if (!Directory.Exists(root)) throw BackupInvalid("原文件恢复目录缺失");
            LocalDiscoverySecurity.EnsureDirectory(root);
            var tree = ListSnapshotTree(root, token);
            directories.Add(name);
            directories.AddRange(tree.Directories.Select(path => name + "/" + path));
            if (directories.Count + (long)files.Count + tree.Files.Count > MaximumSnapshotEntries) throw BackupInvalid("原文件恢复条目超限");
            foreach (var file in tree.Files)
            {
                if (file.Size is < 0 or > MaximumSnapshotFileBytes || file.Size > MaximumSnapshotContentBytes - total)
                    throw BackupInvalid("原文件恢复内容超限");
                total += file.Size;
                var path = UpdatePaths.Local(root, file.Path);
                await using var input = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var content = await new UpdateIoBudget().CopyAndHashAsync(input, null, file.Size, token).ConfigureAwait(false);
                if (content.Length != file.Size) throw BackupInvalid("原文件恢复内容变化");
                input.Flush(true);
                files.Add(new(name + "/" + file.Path, content.Length, content.Hash));
            }
        }
        var result = new RestoreTree(files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(), directories.Order(StringComparer.Ordinal).ToArray());
        ValidateOriginalRestoreTree(result);
        return result;
    }

    private static void ValidateOriginalRestoreTree(RestoreTree tree)
    {
        if (tree is null || tree.Files is null || tree.Directories is null || tree.Files.Count + (long)tree.Directories.Count > MaximumSnapshotEntries)
            throw BackupInvalid("原文件恢复清单超限或为空");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        string? previous = null;
        foreach (var file in tree.Files)
        {
            if (file is null || !SnapshotRelativeValid(file.RelativePath) || !names.Add(file.RelativePath) || !SnapshotHashValid(file.Sha256) ||
                file.SizeBytes is < 0 or > MaximumSnapshotFileBytes || file.SizeBytes > MaximumSnapshotContentBytes - total ||
                previous is not null && string.CompareOrdinal(previous, file.RelativePath) >= 0) throw BackupInvalid("原文件恢复清单无效");
            total += file.SizeBytes;
            previous = file.RelativePath;
        }
        previous = null;
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in tree.Directories)
        {
            if (!SnapshotRelativeValid(path, true) || !names.Add(path) || !directories.Add(path) ||
                previous is not null && string.CompareOrdinal(previous, path) >= 0) throw BackupInvalid("原文件恢复目录无效");
            previous = path;
        }
        if (RestoreRoots.Any(name => !directories.Contains(name))) throw BackupInvalid("原文件恢复缺少顶层目录");
        foreach (var path in names)
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (!directories.Contains(path[..slash])) throw BackupInvalid("原文件恢复目录层级缺失");
    }

    private static bool SameRestoreTree(RestoreTree first, RestoreTree second) =>
        first.Files.SequenceEqual(second.Files) && first.Directories.SequenceEqual(second.Directories);
    private static string RestoreDigest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
