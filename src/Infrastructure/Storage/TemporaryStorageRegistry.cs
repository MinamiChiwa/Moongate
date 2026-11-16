using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed class TemporaryStorageRegistry(SqliteStore database, PathService paths, TimeProvider clock, string epoch)
{
    public static DatabaseMigration Migration { get; } = new("platform-temporary", 1, """
        CREATE TABLE platform_temporary_files(path TEXT PRIMARY KEY,module TEXT NOT NULL,epoch TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('active','completed','abandoned')),terminated_at INTEGER) STRICT;
        """);
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    public const int MaximumFiles = 10000;
    private bool Available => (long)database.ReadScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='platform_temporary_files'")! == 1;

    public async Task<TemporaryFileLease?> TrackAsync(string module, string path, CancellationToken ct = default,
        ActivityBarrier.MaintenanceLease? maintenance = null)
    {
        var full = StoragePaths.ValidateLocal(path);
        if (!StoragePaths.IsWithin(full, paths.Temporary) || full == paths.Temporary || string.IsNullOrWhiteSpace(module) || module.Length > 64)
            throw new StorageException("storage.pathRejected", "临时文件须属于受控临时目录和有效模块");
        if (!Available) return null;
        if ((long)database.ReadScalar("SELECT count(*) FROM platform_temporary_files")! >= MaximumFiles)
            await PruneMissingAsync(ct, maintenance);
        Func<DatabaseTransaction, int> save = tx =>
        {
            if ((string?)tx.Scalar("SELECT state FROM platform_temporary_files WHERE path=$path", ("$path", full)) == "active")
                throw new StorageException("resource.busy", "临时文件已由活动任务登记");
            if ((long)tx.Scalar("SELECT count(*) FROM platform_temporary_files")! >= MaximumFiles &&
                tx.Scalar("SELECT path FROM platform_temporary_files WHERE path=$path", ("$path", full)) is null)
                throw new StorageException("resource.capacityExceeded", "临时文件登记达到数量上限");
            return tx.Execute("""
            INSERT INTO platform_temporary_files VALUES($path,$module,$epoch,'active',NULL)
            ON CONFLICT(path) DO UPDATE SET module=excluded.module,epoch=excluded.epoch,state='active',terminated_at=NULL
            """, ("$path", full), ("$module", module), ("$epoch", epoch));
        };
        if (maintenance is null) await database.WriteAsync(save, ct);
        else await database.WriteMaintenanceAsync(maintenance, save, ct);
        return new(this, full, maintenance);
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        if (!Available) return;
        await database.WriteAsync(tx => tx.Execute(
            "UPDATE platform_temporary_files SET state='abandoned',terminated_at=$now WHERE state='active' AND epoch<>$epoch",
            ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()), ("$epoch", epoch)), ct);
        await PruneMissingAsync(ct);
    }

    internal async Task RelocateAsync(string originalRoot, CancellationToken ct)
    {
        if (!Available) return;
        var entries = database.Read(connection =>
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT path FROM platform_temporary_files ORDER BY path LIMIT 10001";
            using var reader = command.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result;
        });
        if (entries.Count > MaximumFiles) throw new StorageException("storage.quotaExceeded", "临时文件重定位数量超限");
        foreach (var batch in entries.Chunk(100))
            await database.WriteAsync(tx =>
            {
                foreach (var path in batch)
                {
                    if (StoragePaths.IsWithin(path, paths.Temporary)) continue;
                    var sourceTemporary = Path.Combine(originalRoot, "temp");
                    if (!StoragePaths.IsWithin(path, sourceTemporary) || path == sourceTemporary) throw new StorageException("storage.pathRejected", "迁移临时登记不属于原受控目录");
                    var relative = Path.GetRelativePath(sourceTemporary, path);
                    var target = StoragePaths.ValidateLocal(Path.Combine(paths.Temporary, relative));
                    tx.Execute("UPDATE platform_temporary_files SET path=$target WHERE path=$path", ("$target", target), ("$path", path));
                }
                return 0;
            }, ct);
    }

    public async Task PruneMissingAsync(CancellationToken ct = default, ActivityBarrier.MaintenanceLease? maintenance = null, string? path = null)
    {
        if (!Available) return;
        var entries = database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path,epoch,state,terminated_at FROM platform_temporary_files WHERE state<>'active' AND ($path IS NULL OR path=$path) ORDER BY path LIMIT $limit";
            command.Parameters.AddWithValue("$limit", MaximumFiles);
            command.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
            using var rows = command.ExecuteReader(); var result = new List<(string Path, string Epoch, string State, long Terminated)>();
            while (rows.Read()) if (!rows.IsDBNull(3)) result.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetInt64(3)));
            return result;
        });
        var missing = new List<(string Path, string Epoch, string State, long Terminated)>();
        var directoryEntries = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(entry.Path);
            if (!Path.IsPathFullyQualified(entry.Path) || full != entry.Path || !StoragePaths.IsWithin(full, paths.Temporary) || full == paths.Temporary)
                throw new StorageException("storage.pathRejected", "临时登记不属于受控目录");
            var parent = Path.GetDirectoryName(full)!;
            if (!directoryEntries.TryGetValue(parent, out var names))
            {
                StoragePaths.ValidateLocal(parent);
                if (path is not null)
                {
                    try { File.GetAttributes(full); }
                    catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { missing.Add(entry); }
                    continue;
                }
                try
                {
                    names = Directory.EnumerateFileSystemEntries(parent).Take(100001).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (names.Count > 100000) throw new StorageException("resource.capacityExceeded", "临时目录巡检超过条目预算");
                }
                catch (DirectoryNotFoundException) { names = new(StringComparer.OrdinalIgnoreCase); }
                directoryEntries.Add(parent, names);
            }
            if (!names.Contains(full)) missing.Add(entry);
        }
        foreach (var batch in missing.Chunk(100))
        {
            Func<DatabaseTransaction, int> save = tx =>
            {
                var removed = 0;
                foreach (var entry in batch) removed += tx.Execute(
                    "DELETE FROM platform_temporary_files WHERE path=$path AND epoch=$epoch AND state=$state AND terminated_at=$at",
                    ("$path", entry.Path), ("$epoch", entry.Epoch), ("$state", entry.State), ("$at", entry.Terminated));
                return removed;
            };
            if (maintenance is null) await database.WriteAsync(save, ct);
            else await database.WriteMaintenanceAsync(maintenance, save, ct);
        }
    }

    public (string Module, bool Eligible, string? Protection) State(string path)
    {
        if (!Available) return ("platform", false, "unregisteredTemporary");
        return database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT module,state,terminated_at FROM platform_temporary_files WHERE path=$path";
            command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
            using var row = command.ExecuteReader();
            if (!row.Read()) return ("platform", false, "unregisteredTemporary");
            if (row.GetString(1) == "active" || row.IsDBNull(2)) return (row.GetString(0), false, "activeTask");
            if (clock.GetUtcNow().ToUnixTimeMilliseconds() - row.GetInt64(2) < Retention.TotalMilliseconds)
                return (row.GetString(0), false, "temporaryRetention");
            return (row.GetString(0), true, (string?)null);
        });
    }

    private async ValueTask CompleteAsync(string path, ActivityBarrier.MaintenanceLease? maintenance)
    {
        var exists = File.Exists(path);
        Func<DatabaseTransaction, int> save = tx => exists ? tx.Execute(
            "UPDATE platform_temporary_files SET state='completed',terminated_at=$now WHERE path=$path AND epoch=$epoch AND state='active'",
            ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()), ("$path", path), ("$epoch", epoch)) :
            tx.Execute("DELETE FROM platform_temporary_files WHERE path=$path AND epoch=$epoch AND state='active'", ("$path", path), ("$epoch", epoch));
        if (maintenance is null) await database.WriteAsync(save);
        else await database.WriteMaintenanceAsync(maintenance, save);
    }
    public sealed class TemporaryFileLease : IAsyncDisposable
    {
        private TemporaryStorageRegistry? owner;
        private readonly string path;
        private readonly ActivityBarrier.MaintenanceLease? maintenance;
        internal TemporaryFileLease(TemporaryStorageRegistry owner, string path, ActivityBarrier.MaintenanceLease? maintenance)
        { this.owner = owner; this.path = path; this.maintenance = maintenance; }
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref owner, null)?.CompleteAsync(path, maintenance) ?? ValueTask.CompletedTask;
    }
}
