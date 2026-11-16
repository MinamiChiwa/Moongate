using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

public sealed record DataSnapshotFile(string RelativePath, long SizeBytes, string Sha256);
public sealed record DataSnapshotDescriptor(int SchemaVersion, string SnapshotId, string RootId,
    DateTimeOffset CreatedAt, DatabaseBackupDescriptor Database, IReadOnlyList<DataSnapshotFile> Files,
    IReadOnlyList<string> Directories, long TotalSizeBytes);
internal enum DataSnapshotPersistencePoint { DatabaseBackedUp, FilesCopied, Published }

public sealed partial class SqliteStore
{
    public const int MaximumDataSnapshots = 128;
    public const int MaximumSnapshotEntries = 100_000;
    public const long MaximumSnapshotFileBytes = 8L * 1024 * 1024 * 1024;
    public const long MaximumSnapshotContentBytes = 64L * 1024 * 1024 * 1024;
    private const int MaximumSnapshotManifestBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web)
    { MaxDepth = 24, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>维护期间固定数据库，并独立复制该库引用的资产和 settings；后续 GC 不影响快照。</summary>
    public Task<DataSnapshotDescriptor> CreateDataSnapshotAsync(ActivityBarrier.MaintenanceLease maintenance,
        CancellationToken token = default) => CreateDataSnapshotAsync(maintenance, token, null);

    internal async Task<DataSnapshotDescriptor> CreateDataSnapshotAsync(ActivityBarrier.MaintenanceLease maintenance,
        CancellationToken token, Func<DataSnapshotPersistencePoint, Task>? persisted)
        => await CreateDataSnapshotCoreAsync(maintenance, null, DataBackupPurpose.UpdateRecovery, token, persisted).ConfigureAwait(false);

    public Task<DataSnapshotDescriptor> CreateOrdinaryDataSnapshotAsync(AssetStore assets, CancellationToken token = default)
        => CreateOrdinaryDataSnapshotAsync(assets, token, null);

    public Task<DataSnapshotDescriptor> CreateOrdinaryDataSnapshotAsync(AssetStore assets, DataBackupPurpose purpose, CancellationToken token = default)
    {
        if (purpose is not (DataBackupPurpose.Automatic or DataBackupPurpose.Manual)) throw new ArgumentOutOfRangeException(nameof(purpose));
        if (!assets.BelongsTo(this)) throw new ArgumentException("资产所有者须属于当前数据库", nameof(assets));
        return CreateDataSnapshotCoreAsync(null, assets, purpose, token, null);
    }

    internal Task<DataSnapshotDescriptor> CreateRecoveryDataSnapshotAsync(ActivityBarrier.MaintenanceLease maintenance,
        CancellationToken token = default) => CreateDataSnapshotCoreAsync(maintenance, null, DataBackupPurpose.RestoreRecovery, token, null);

    internal Task<DataSnapshotDescriptor> CreateOrdinaryDataSnapshotAsync(AssetStore assets, CancellationToken token,
        Func<DataSnapshotPersistencePoint, Task>? persisted)
    {
        if (!assets.BelongsTo(this)) throw new ArgumentException("资产所有者须属于当前数据库", nameof(assets));
        return CreateDataSnapshotCoreAsync(null, assets, DataBackupPurpose.Automatic, token, persisted);
    }

    private async Task<DataSnapshotDescriptor> CreateDataSnapshotCoreAsync(ActivityBarrier.MaintenanceLease? maintenance,
        AssetStore? assetStore, DataBackupPurpose purpose, CancellationToken token, Func<DataSnapshotPersistencePoint, Task>? persisted)
    {
        token.ThrowIfCancellationRequested();
        if (maintenance is null && assetStore is null)
            throw new ActivityBarrierException("update.maintenanceLeaseInvalid");
        using var activity = maintenance is null ? Activities.EnterActivity("backup", "storage.dataSnapshot") :
            Activities.EnterMaintenanceActivity(maintenance, "storage.dataSnapshot");
        await databaseBackupGate.WaitAsync(token).ConfigureAwait(false);
        DatabaseBackupDescriptor? backup = null;
        AssetStore.AssetBackupPin? assetPin = null;
        string? work = null;
        var published = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref stopped) != 0, this);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
            var ct = cancellation.Token;
            var root = paths.Resolve("backups", "data");
            Directory.CreateDirectory(root);
            if (Directory.EnumerateFileSystemEntries(root).Take(MaximumDataSnapshots).Count() >= MaximumDataSnapshots)
                throw new StorageException("resource.capacityExceeded", "数据快照达到上限，需要先执行清理计划");
            var id = Guid.NewGuid().ToString("N");
            var destination = Path.Combine(root, id);
            work = destination + ".work-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(work);
            if (assetStore is null)
                backup = await Task.Run(() => CreateDatabaseBackup(ct), CancellationToken.None).ConfigureAwait(false);
            else
                assetPin = await assetStore.FreezeBackupAsync(async captureToken =>
                {
                    backup = await Task.Run(() => CreateDatabaseBackup(captureToken), CancellationToken.None).ConfigureAwait(false);
                    using var copy = await OpenVerifiedBackupAsync(paths, backup.BackupId, backup, captureToken).ConfigureAwait(false);
                    return (backup, ReadSnapshotAssets(copy.Database.Name, captureToken));
                }, ct).ConfigureAwait(false);
            if (persisted is not null) await persisted(DataSnapshotPersistencePoint.DatabaseBackedUp).ConfigureAwait(false);
            using var database = await OpenVerifiedBackupAsync(paths, backup!.BackupId, backup, ct).ConfigureAwait(false);
            var assets = assetPin?.Files ?? ReadSnapshotAssets(database.Database.Name, ct);
            var settings = await ReadSettingsSnapshotAsync(paths, ct).ConfigureAwait(false);
            var files = assets.Concat(settings.Files).OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
            var directories = SnapshotDirectories(files, settings.Directories);
            var descriptor = new DataSnapshotDescriptor(1, id, paths.RootId, storageClock.GetUtcNow(), backup,
                files, directories, checked(backup.SizeBytes + files.Sum(file => file.SizeBytes)));
            ValidateDataSnapshot(descriptor, paths, id);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(descriptor, SnapshotJson);
            if (bytes.Length > MaximumSnapshotManifestBytes) throw BackupInvalid("数据快照清单超限");
            var remaining = descriptor.TotalSizeBytes - backup.SizeBytes;
            using var space = Space.Reserve("data-snapshot", new VolumeSpaceRequirement(root,
                checked(remaining + remaining / 5 + MaximumSnapshotManifestBytes)));
            foreach (var directory in directories) Directory.CreateDirectory(UpdatePaths.Local(work, directory));
            foreach (var file in files)
            {
                var source = UpdatePaths.Local(paths.DataRoot, file.RelativePath);
                var target = UpdatePaths.Local(work, file.RelativePath);
                await CopySnapshotFileAsync(source, target, file, ct).ConfigureAwait(false);
            }
            if (persisted is not null) await persisted(DataSnapshotPersistencePoint.FilesCopied).ConfigureAwait(false);
            // 配置没有内容寻址身份；复制前后均核对，拒绝复制期间的外部修改。
            var finalSettings = await ReadSettingsSnapshotAsync(paths, ct).ConfigureAwait(false);
            if (!settings.Files.SequenceEqual(finalSettings.Files) || !settings.Directories.SequenceEqual(finalSettings.Directories))
                throw BackupInvalid("复制期间配置内容发生变化");
            await using (var manifest = new FileStream(Path.Combine(work, "manifest.json"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await manifest.WriteAsync(bytes, ct).ConfigureAwait(false);
                await manifest.FlushAsync(ct).ConfigureAwait(false);
                manifest.Flush(true);
            }
            await VerifyDataSnapshotContentsAsync(paths, work, descriptor, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await UpdatePaths.PublishOwnedDirectoryAsync(work, destination, ct).ConfigureAwait(false);
            published = true;
            await new DataBackupStore(this, paths).RegisterAsync(descriptor, purpose,
                maintenance, CancellationToken.None).ConfigureAwait(false);
            if (persisted is not null) await persisted(DataSnapshotPersistencePoint.Published).ConfigureAwait(false);
            return descriptor;
        }
        catch (SqliteException error) { throw new StorageException("storage.backupInvalid", "数据快照的资产索引无效", error); }
        finally
        {
            try
            {
                if (work is not null) UpdatePaths.DeleteOwnedTree(work);
                // 尚未发布的组合快照没有对外暴露数据库描述，可以清理本次独占建立的副本。
                if (!published && backup is not null)
                    UpdatePaths.DeleteOwnedTree(paths.Resolve("backups", "database", backup.BackupId));
            }
            finally
            {
                try { if (assetPin is not null) await assetPin.DisposeAsync().ConfigureAwait(false); }
                finally { databaseBackupGate.Release(); }
            }
        }
    }

    public Task<DataSnapshotDescriptor> VerifyDataSnapshotAsync(string snapshotId, DataSnapshotDescriptor? expected = null,
        CancellationToken token = default) => VerifyDataSnapshotAsync(paths, snapshotId, expected, token);

    internal static async Task<DataSnapshotDescriptor> VerifyDataSnapshotAsync(PathService paths, string snapshotId,
        DataSnapshotDescriptor? expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SnapshotIdValid(snapshotId)) throw BackupInvalid("数据快照 ID 无效");
        // 固定调用方可变集合；异步读取不跟随调用方修改。
        var frozen = expected is null ? null : JsonSerializer.Serialize(expected, SnapshotJson);
        var root = paths.Resolve("backups", "data", snapshotId);
        var manifestPath = UpdatePaths.Local(root, "manifest.json");
        await using var manifest = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous);
        if (manifest.Length is <= 0 or > MaximumSnapshotManifestBytes) throw BackupInvalid("数据快照清单大小无效");
        var bytes = new byte[(int)manifest.Length];
        await manifest.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        DataSnapshotDescriptor descriptor;
        try
        {
            using var document = StrictJson.Parse(bytes, MaximumSnapshotManifestBytes);
            descriptor = document.RootElement.Deserialize<DataSnapshotDescriptor>(SnapshotJson) ?? throw BackupInvalid("数据快照清单为空");
        }
        catch (JsonException error) { throw new StorageException("storage.backupInvalid", "数据快照清单格式无效", error); }
        catch (TrustValidationException error) { throw new StorageException("storage.backupInvalid", "数据快照清单格式无效", error); }
        ValidateDataSnapshot(descriptor, paths, snapshotId);
        if (frozen is not null && frozen != JsonSerializer.Serialize(descriptor, SnapshotJson))
            throw BackupInvalid("数据快照与固定描述不匹配");
        try { await VerifyDataSnapshotContentsAsync(paths, root, descriptor, token).ConfigureAwait(false); }
        catch (SqliteException error) { throw new StorageException("storage.backupInvalid", "数据快照的资产索引无效", error); }
        return descriptor;
    }

    private static async Task VerifyDataSnapshotContentsAsync(PathService paths, string root,
        DataSnapshotDescriptor descriptor, CancellationToken token)
    {
        using var backup = await OpenVerifiedBackupAsync(paths, descriptor.Database.BackupId, descriptor.Database, token).ConfigureAwait(false);
        var assets = ReadSnapshotAssets(backup.Database.Name, token);
        if (!assets.SequenceEqual(descriptor.Files.Where(file => file.RelativePath.StartsWith("assets/", StringComparison.Ordinal))))
            throw BackupInvalid("数据快照资产与数据库引用不匹配");
        var actual = ListSnapshotTree(root, token);
        var expectedPaths = descriptor.Files.Select(file => file.RelativePath).Append("manifest.json").Order(StringComparer.Ordinal);
        if (!actual.Files.Select(file => file.Path).SequenceEqual(expectedPaths) || !actual.Directories.SequenceEqual(descriptor.Directories))
            throw BackupInvalid("数据快照文件或目录集合不匹配");
        foreach (var file in descriptor.Files)
            await VerifySnapshotFileAsync(UpdatePaths.Local(root, file.RelativePath), file, token).ConfigureAwait(false);
    }

    private static List<DataSnapshotFile> ReadSnapshotAssets(string databasePath, CancellationToken token)
    {
        using var database = OpenBackupDatabase(databasePath, SqliteOpenMode.ReadOnly);
        using var command = database.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='platform_assets'";
        if ((long)command.ExecuteScalar()! == 0) return [];
        command.CommandText = "SELECT count(*) FROM platform_asset_commits WHERE state='pending'";
        if ((long)command.ExecuteScalar()! != 0) throw BackupInvalid("资产提交尚未恢复，不能建立匹配快照");
        command.CommandText = """
            SELECT DISTINCT a.sha256,a.size_bytes,b.size_bytes,b.state
            FROM platform_assets a JOIN platform_asset_blobs b ON b.sha256=a.sha256 ORDER BY a.sha256
            """;
        using var reader = command.ExecuteReader();
        var files = new List<DataSnapshotFile>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var hash = reader.GetString(0);
            var size = reader.GetInt64(1);
            if (files.Count >= MaximumSnapshotEntries || !SnapshotHashValid(hash) || size is < 0 or > MaximumSnapshotFileBytes ||
                size != reader.GetInt64(2) || reader.GetString(3) != "ready" || !hashes.Add(hash))
                throw BackupInvalid("数据库资产身份、长度或状态无效");
            files.Add(new($"assets/sha256/{hash[..2]}/{hash}", size, hash));
        }
        return files;
    }

    private static async Task<SettingsSnapshot> ReadSettingsSnapshotAsync(PathService paths, CancellationToken token)
    {
        var tree = ListSnapshotTree(paths.Settings, token);
        var files = new List<DataSnapshotFile>();
        long totalBytes = 0;
        foreach (var file in tree.Files)
        {
            if (file.Size is < 0 or > MaximumSnapshotFileBytes || file.Size > MaximumSnapshotContentBytes - totalBytes)
                throw BackupInvalid("配置文件或配置总量超限");
            totalBytes += file.Size;
            var name = "settings/" + file.Path;
            var source = UpdatePaths.Local(paths.DataRoot, name);
            await using var input = OpenSnapshotFile(source);
            var content = await new UpdateIoBudget().CopyAndHashAsync(input, null, file.Size, token).ConfigureAwait(false);
            if (content.Length != file.Size) throw BackupInvalid("配置文件长度发生变化");
            files.Add(new(name, content.Length, content.Hash));
        }
        return new(files, Directory.Exists(paths.Settings)
            ? tree.Directories.Select(directory => "settings/" + directory).Prepend("settings").Order(StringComparer.Ordinal).ToArray()
            : []);
    }

    private static string[] SnapshotDirectories(IEnumerable<DataSnapshotFile> files, IEnumerable<string> settingsDirectories)
    {
        var directories = settingsDirectories.ToHashSet(StringComparer.Ordinal);
        directories.Add("assets");
        foreach (var file in files)
            for (var slash = file.RelativePath.IndexOf('/'); slash >= 0; slash = file.RelativePath.IndexOf('/', slash + 1))
                directories.Add(file.RelativePath[..slash]);
        return directories.Order(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateDataSnapshot(DataSnapshotDescriptor descriptor, PathService paths, string id)
    {
        if (descriptor.SchemaVersion != 1 || descriptor.CreatedAt == default || descriptor.SnapshotId != id || !SnapshotIdValid(id) || descriptor.RootId != paths.RootId ||
            descriptor.Database is null || descriptor.Database.RootId != paths.RootId || descriptor.Files is null || descriptor.Directories is null ||
            descriptor.Files.Count + (long)descriptor.Directories.Count > MaximumSnapshotEntries ||
            descriptor.TotalSizeBytes is <= 0 or > MaximumSnapshotContentBytes || descriptor.Database.SizeBytes <= 0)
            throw BackupInvalid("数据快照身份、数量或大小无效");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = descriptor.Database.SizeBytes;
        string? previous = null;
        foreach (var file in descriptor.Files)
        {
            if (file is null || !SnapshotRelativeValid(file.RelativePath) || !names.Add(file.RelativePath) ||
                !SnapshotHashValid(file.Sha256) || file.SizeBytes is < 0 or > MaximumSnapshotFileBytes ||
                previous is not null && string.CompareOrdinal(previous, file.RelativePath) >= 0)
                throw BackupInvalid("数据快照文件清单无效");
            if (file.RelativePath.StartsWith("assets/", StringComparison.Ordinal) &&
                file.RelativePath != $"assets/sha256/{file.Sha256[..2]}/{file.Sha256}") throw BackupInvalid("资产快照路径与内容身份不匹配");
            if (file.SizeBytes > MaximumSnapshotContentBytes - total) throw BackupInvalid("数据快照总量超限");
            total += file.SizeBytes;
            previous = file.RelativePath;
        }
        if (total != descriptor.TotalSizeBytes) throw BackupInvalid("数据快照总量不匹配");
        previous = null;
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in descriptor.Directories)
        {
            if (!SnapshotRelativeValid(directory, true) || !names.Add(directory) || !directories.Add(directory) ||
                previous is not null && string.CompareOrdinal(previous, directory) >= 0)
                throw BackupInvalid("数据快照目录清单无效");
            previous = directory;
        }
        foreach (var path in names)
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (!directories.Contains(path[..slash])) throw BackupInvalid("数据快照目录层级缺失");
        var allowed = SnapshotDirectories(descriptor.Files, descriptor.Directories.Where(directory => directory == "settings" || directory.StartsWith("settings/", StringComparison.Ordinal)));
        if (!allowed.SequenceEqual(descriptor.Directories)) throw BackupInvalid("数据快照包含额外资产目录");
    }

    private static bool SnapshotRelativeValid(string? path, bool directory = false) => path is not null &&
        (directory && (path is "assets" or "settings") || path.StartsWith("settings/", StringComparison.Ordinal) || path.StartsWith("assets/", StringComparison.Ordinal)) &&
        UpdatePaths.Normalize(path) == path;
    private static bool SnapshotIdValid(string? id) => id is { Length: 32 } && id.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
    private static bool SnapshotHashValid(string? hash) => hash is { Length: 64 } && hash.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
    private static FileStream OpenSnapshotFile(string path)
    {
        UpdatePaths.RejectLinks(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static async Task CopySnapshotFileAsync(string source, string target, DataSnapshotFile expected, CancellationToken token)
    {
        await using var input = OpenSnapshotFile(source);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        var actual = await new UpdateIoBudget().CopyAndHashAsync(input, output, expected.SizeBytes, token).ConfigureAwait(false);
        if (actual.Length != expected.SizeBytes || actual.Hash != expected.Sha256) throw BackupInvalid("快照源文件与固定内容不匹配");
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
    }

    private static async Task VerifySnapshotFileAsync(string path, DataSnapshotFile expected, CancellationToken token)
    {
        await using var input = OpenSnapshotFile(path);
        if (input.Length != expected.SizeBytes || Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)).ToLowerInvariant() != expected.Sha256)
            throw BackupInvalid("数据快照内容与固定哈希不匹配");
    }

    private static SnapshotTree ListSnapshotTree(string root, CancellationToken token)
    {
        UpdatePaths.RejectLinks(root);
        var files = new List<(string Path, long Size)>();
        var directories = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        if (File.Exists(root)) throw BackupInvalid("快照目录被文件占用");
        if (Directory.Exists(root)) pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            UpdatePaths.RejectLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                UpdatePaths.RejectLinks(entry);
                var relative = UpdatePaths.Normalize(Path.GetRelativePath(root, entry));
                if (!names.Add(relative) || names.Count > MaximumSnapshotEntries + 1) throw BackupInvalid("快照目录条目冲突或超限");
                if (Directory.Exists(entry)) { directories.Add(relative); pending.Push(entry); }
                else files.Add((relative, new FileInfo(entry).Length));
            }
        }
        return new(files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), directories.Order(StringComparer.Ordinal).ToArray());
    }

    private sealed record SettingsSnapshot(IReadOnlyList<DataSnapshotFile> Files, IReadOnlyList<string> Directories);
    private sealed record SnapshotTree(IReadOnlyList<(string Path, long Size)> Files, IReadOnlyList<string> Directories);
}
