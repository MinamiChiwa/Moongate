using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

public sealed record DatabaseMigrationStamp(string ModuleId, int Version, string Checksum);
public sealed record DatabaseBackupDescriptor(int SchemaVersion, string BackupId, string RootId, string DatabaseId,
    string BackendMode, DateTimeOffset CreatedAt, long SizeBytes, string Sha256, IReadOnlyList<DatabaseMigrationStamp> Migrations);
public sealed record DatabaseMigrationPreflight(string BackupId, long SizeBytes, IReadOnlyList<DatabaseMigrationStamp> Migrations);

public sealed partial class SqliteStore
{
    public const long MaximumBackupDatabaseBytes = 8L * 1024 * 1024 * 1024;
    public const int MaximumDatabaseBackups = 128;
    private const int MaximumBackupManifestBytes = 512 * 1024;
    private static readonly JsonSerializerOptions BackupJson = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly SemaphoreSlim databaseBackupGate = new(1, 1);

    /// <summary>维护屏障内通过 SQLite 备份接口复制，发布完整且独立于源 WAL 的数据库。</summary>
    public async Task<DatabaseBackupDescriptor> CreateDatabaseBackupAsync(ActivityBarrier.MaintenanceLease maintenance,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var activity = Activities.EnterMaintenanceActivity(maintenance, "storage.databaseBackup");
        await databaseBackupGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref stopped) != 0, this);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
            // BackupDatabase 是同步调用；调用结束前不释放活动或让调用方误认已取消完成。
            return await Task.Run(() => CreateDatabaseBackup(cancellation.Token), CancellationToken.None).ConfigureAwait(false);
        }
        finally { databaseBackupGate.Release(); }
    }

    /// <summary>调用方可用安装日志中固定的描述复验备份，拒绝只替换清单及数据库的情况。</summary>
    public async Task<DatabaseBackupDescriptor> VerifyDatabaseBackupAsync(string backupId,
        DatabaseBackupDescriptor? expected = null, CancellationToken token = default)
    {
        using var backup = await OpenVerifiedBackupAsync(paths, backupId, expected, token).ConfigureAwait(false);
        return backup.Descriptor;
    }

    /// <summary>在私有副本上使用宿主相同迁移规则预检；不改写当前数据库或已发布备份。</summary>
    public Task<DatabaseMigrationPreflight> PreflightDatabaseMigrationsAsync(DatabaseBackupDescriptor expected,
        IReadOnlyList<DatabaseMigration> moduleMigrations, ActivityBarrier.MaintenanceLease maintenance,
        CancellationToken token = default)
        => maintenance is null ? Task.FromException<DatabaseMigrationPreflight>(new ActivityBarrierException("update.maintenanceLeaseInvalid"))
            : PreflightDatabaseMigrationsCoreAsync(expected, moduleMigrations, maintenance, token);

    public Task<DatabaseMigrationPreflight> PreflightOrdinaryDatabaseMigrationsAsync(DatabaseBackupDescriptor expected,
        IReadOnlyList<DatabaseMigration> moduleMigrations, CancellationToken token = default)
        => PreflightDatabaseMigrationsCoreAsync(expected, moduleMigrations, null, token);

    private async Task<DatabaseMigrationPreflight> PreflightDatabaseMigrationsCoreAsync(DatabaseBackupDescriptor expected,
        IReadOnlyList<DatabaseMigration> moduleMigrations, ActivityBarrier.MaintenanceLease? maintenance, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(moduleMigrations);
        var migrations = new[] { CoreMigrations.Initial, CoreMigrations.Operations, CoreMigrations.ProjectionMetadata, CoreMigrations.BackendMode }
            .Concat(moduleMigrations).ToArray();
        using var activity = maintenance is null ? Activities.EnterActivity("storage", "storage.migrationPreflight") :
            Activities.EnterMaintenanceActivity(maintenance, "storage.migrationPreflight");
        await databaseBackupGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref stopped) != 0, this);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
            var ct = cancellation.Token;
            using var backup = await OpenVerifiedBackupAsync(paths, expected.BackupId, expected, ct).ConfigureAwait(false);
            var work = paths.Resolve("temp", "database-preflight-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var target = Path.Combine(work, "main.sqlite");
                EnsureBackupSpace(expected.SizeBytes, paths.Temporary);
                using var space = Space.Reserve("database-preflight", new VolumeSpaceRequirement(paths.Temporary,
                    checked(expected.SizeBytes + expected.SizeBytes / 5 + MaximumBackupManifestBytes)));
                await using var temporary = await TemporaryFiles.TrackAsync("core", target, ct, maintenance);
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous))
                {
                    backup.Database.Position = 0;
                    await backup.Database.CopyToAsync(output, ct).ConfigureAwait(false);
                }
                return await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    using var database = OpenBackupDatabase(target, SqliteOpenMode.ReadWrite);
                    ExecuteBackupSql(database, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
                    ApplyMigrations(database, migrations);
                    ct.ThrowIfCancellationRequested();
                    var state = InspectBackupDatabase(database);
                    if (state.Root != expected.RootId || state.Database != expected.DatabaseId || state.Mode != expected.BackendMode)
                        throw BackupInvalid("预检迁移改变了数据库身份");
                    ExecuteBackupSql(database, "PRAGMA journal_mode=DELETE;");
                    return new DatabaseMigrationPreflight(expected.BackupId, new FileInfo(target).Length, state.Migrations);
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqliteException error) { throw new StorageException("storage.migrationInvalid", "备份迁移预检失败", error); }
            finally
            {
                Updates.UpdatePaths.DeleteOwnedTree(work);
                await TemporaryFiles.PruneMissingAsync(CancellationToken.None, maintenance, Path.Combine(work, "main.sqlite")).ConfigureAwait(false);
            }
        }
        finally { databaseBackupGate.Release(); }
    }

    private DatabaseBackupDescriptor CreateDatabaseBackup(CancellationToken token)
    {
        var root = paths.Resolve("backups", "database");
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Take(MaximumDatabaseBackups).Count() >= MaximumDatabaseBackups)
            throw new StorageException("resource.capacityExceeded", "数据库备份达到上限，需要先执行清理计划");
        var id = Guid.NewGuid().ToString("N");
        var target = Path.Combine(root, id);
        var work = target + ".work-" + Guid.NewGuid().ToString("N");
        var estimatedSize = (long)ReadScalar("SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()")!;
        using var space = Space.Reserve("database-backup", new VolumeSpaceRequirement(root,
            checked(estimatedSize + estimatedSize / 5 + MaximumBackupManifestBytes)));
        Directory.CreateDirectory(work);
        try
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(work, "main.sqlite");
            Read(source =>
            {
                using var count = source.CreateCommand();
                count.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
                var size = (long)count.ExecuteScalar()!;
                EnsureBackupSpace(size, root);
                token.ThrowIfCancellationRequested();
                using var copy = OpenBackupDatabase(destination, SqliteOpenMode.ReadWriteCreate);
                source.BackupDatabase(copy);
                // 发布的副本不得依赖 -wal/-shm；关闭写连接后只发布主文件。
                ExecuteBackupSql(copy, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;");
                return true;
            });
            token.ThrowIfCancellationRequested();
            var sizeBytes = new FileInfo(destination).Length;
            if (sizeBytes <= 0 || sizeBytes > MaximumBackupDatabaseBytes) throw BackupInvalid("数据库备份大小超限");
            BackupDatabaseState state;
            using (var copy = OpenBackupDatabase(destination, SqliteOpenMode.ReadOnly)) state = InspectBackupDatabase(copy);
            if (state.Root != paths.RootId) throw BackupInvalid("数据库备份的数据根身份不匹配");
            string hash;
            using (var data = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                data.Flush(true);
                hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            }
            var descriptor = new DatabaseBackupDescriptor(1, id, state.Root, state.Database, state.Mode,
                storageClock.GetUtcNow(), sizeBytes, hash, state.Migrations);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(descriptor, BackupJson);
            if (manifest.Length > MaximumBackupManifestBytes) throw BackupInvalid("数据库备份清单超限");
            using (var output = new FileStream(Path.Combine(work, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(manifest);
                output.Flush(true);
            }
            RequireBackupFiles(work);
            token.ThrowIfCancellationRequested();
            Updates.UpdatePaths.RejectLinks(target);
            Directory.Move(work, target);
            return descriptor;
        }
        catch (SqliteException error) { throw new StorageException("storage.backupInvalid", "无法创建一致数据库备份", error); }
        finally { Updates.UpdatePaths.DeleteOwnedTree(work); }
    }

    private static async Task<VerifiedDatabaseBackup> OpenVerifiedBackupAsync(PathService paths, string id,
        DatabaseBackupDescriptor? expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (id is not { Length: 32 } || id.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw BackupInvalid("数据库备份 ID 无效");
        var root = paths.Resolve("backups", "database", id);
        RequireBackupFiles(root);
        DatabaseBackupDescriptor descriptor;
        try
        {
            await using var manifest = new FileStream(Path.Combine(root, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (manifest.Length is <= 0 or > MaximumBackupManifestBytes) throw BackupInvalid("数据库备份清单大小无效");
            descriptor = await JsonSerializer.DeserializeAsync<DatabaseBackupDescriptor>(manifest, BackupJson, token).ConfigureAwait(false)
                ?? throw BackupInvalid("数据库备份清单为空");
        }
        catch (JsonException error) { throw new StorageException("storage.backupInvalid", "数据库备份清单格式无效", error); }
        if (descriptor.SchemaVersion != 1 || descriptor.BackupId != id || descriptor.RootId != paths.RootId ||
            !Guid.TryParseExact(descriptor.DatabaseId, "N", out _) || descriptor.BackendMode is not ("live" or "mock") ||
            descriptor.SizeBytes is <= 0 or > MaximumBackupDatabaseBytes || descriptor.Sha256 is not { Length: 64 } ||
            !descriptor.Sha256.All(Uri.IsHexDigit) || descriptor.Migrations is not { Count: > 0 and <= 4096 } ||
            descriptor.Migrations.Any(item => item is null || item.Version < 1 || item.ModuleId is null || item.Checksum is null) ||
            expected is not null && JsonSerializer.Serialize(descriptor, BackupJson) != JsonSerializer.Serialize(expected, BackupJson))
            throw BackupInvalid("数据库备份清单与固定身份不匹配");
        var file = Path.Combine(root, "main.sqlite");
        var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (input.Length != descriptor.SizeBytes ||
                !Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)).Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                throw BackupInvalid("数据库备份内容与哈希不匹配");
            using var database = OpenBackupDatabase(file, SqliteOpenMode.ReadOnly);
            var state = InspectBackupDatabase(database);
            if (state.Root != descriptor.RootId || state.Database != descriptor.DatabaseId || state.Mode != descriptor.BackendMode ||
                !state.Migrations.SequenceEqual(descriptor.Migrations)) throw BackupInvalid("数据库备份身份或迁移历史不匹配");
            RequireBackupFiles(root);
            token.ThrowIfCancellationRequested();
            return new(input, descriptor);
        }
        catch (SqliteException error) { input.Dispose(); throw new StorageException("storage.backupInvalid", "数据库备份完整性验证失败", error); }
        catch { input.Dispose(); throw; }
    }

    private static void RequireBackupFiles(string root)
    {
        Updates.UpdatePaths.RejectLinks(root);
        var files = Updates.UpdatePaths.EnumerateFiles(root, CancellationToken.None);
        if (files.Count != 2 || !files.Select(item => item.Relative).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "main.sqlite", "manifest.json" }) || Directory.EnumerateDirectories(root).Any())
            throw BackupInvalid("数据库备份目录内容无效或缺失");
    }

    private static SqliteConnection OpenBackupDatabase(string path, SqliteOpenMode mode)
    {
        Updates.UpdatePaths.RejectLinks(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 1 }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static void ExecuteBackupSql(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static BackupDatabaseState InspectBackupDatabase(SqliteConnection database)
    {
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        using (var rows = command.ExecuteReader())
            if (!rows.Read() || rows.GetString(0) != "ok" || rows.Read()) throw BackupInvalid("数据库完整性检查失败");
        command.CommandText = "PRAGMA foreign_key_check";
        if (command.ExecuteScalar() is not null) throw BackupInvalid("数据库外键校验失败");
        command.CommandText = "SELECT root_id,database_id FROM core_database_identity WHERE singleton=1";
        string root, id;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) throw BackupInvalid("数据库缺少身份");
            root = row.GetString(0); id = row.GetString(1);
            if (!Guid.TryParseExact(root, "N", out _) || !Guid.TryParseExact(id, "N", out _) || row.Read())
                throw BackupInvalid("数据库身份无效");
        }
        command.CommandText = "SELECT mode FROM core_backend_mode WHERE singleton=1";
        var mode = command.ExecuteScalar() as string;
        if (mode is not ("live" or "mock")) throw BackupInvalid("数据库缺少模式身份");
        command.CommandText = "SELECT module_id,version,checksum FROM core_migrations ORDER BY module_id,version";
        var stamps = new List<DatabaseMigrationStamp>();
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                var module = rows.GetString(0);
                var version = rows.GetInt32(1);
                var checksum = rows.GetString(2);
                if (stamps.Count >= 4096 || !System.Text.RegularExpressions.Regex.IsMatch(module, @"\A[a-z][a-z0-9-]{0,63}\z") ||
                    version != versions.GetValueOrDefault(module) + 1 || checksum.Length != 64 || !checksum.All(Uri.IsHexDigit))
                    throw BackupInvalid("数据库迁移记录无效");
                stamps.Add(new(module, version, checksum));
                versions[module] = version;
            }
        if (versions.GetValueOrDefault("core") < 4) throw BackupInvalid("数据库缺少核心迁移");
        return new(root, id, mode, stamps);
    }

    private static void EnsureBackupSpace(long size, string destination)
    {
        if (size is <= 0 or > MaximumBackupDatabaseBytes) throw BackupInvalid("数据库备份大小超限");
        if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < size + size / 5 + MaximumBackupManifestBytes)
            throw new StorageException("storage.diskSpaceInsufficient", "数据库备份或迁移预检空间不足");
    }

    private static StorageException BackupInvalid(string message) => new("storage.backupInvalid", message);
    private sealed record BackupDatabaseState(string Root, string Database, string Mode, IReadOnlyList<DatabaseMigrationStamp> Migrations);
    private sealed record VerifiedDatabaseBackup(FileStream Database, DatabaseBackupDescriptor Descriptor) : IDisposable
    {
        public void Dispose() => Database.Dispose();
    }
}
