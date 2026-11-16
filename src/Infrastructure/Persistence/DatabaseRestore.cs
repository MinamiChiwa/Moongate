using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

internal enum DatabaseRestorePersistencePoint { CandidateVerified, SourceCheckpointed, Replaced }
internal sealed record DatabaseRestoreResult(string TransactionId, string BackupId, string RetainedDatabaseSha256);

public sealed partial class SqliteStore
{
    /// <summary>未提交安装回退的内部步骤；调用方负责安装日志、匹配资产及后续健康确认。</summary>
    internal static async Task<DatabaseRestoreResult> RestoreDatabaseBackupOfflineAsync(DataRootLease lease,
        string transactionId, DatabaseBackupDescriptor expected, CancellationToken token = default,
        Func<DatabaseRestorePersistencePoint, Task>? persisted = null)
    {
        using var ownership = lease.AcquireDatabaseOwnership();
        return await RestoreDatabaseBackupOwnedAsync(lease, ownership, transactionId, expected, token, persisted).ConfigureAwait(false);
    }

    internal static async Task<DatabaseRestoreResult> RestoreDatabaseBackupOwnedAsync(DataRootLease lease,
        DataRootLease.DatabaseOwnership ownership, string transactionId, DatabaseBackupDescriptor expected,
        CancellationToken token = default, Func<DatabaseRestorePersistencePoint, Task>? persisted = null,
        DatabaseRestoreResult? completed = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var usage = ownership.Use(lease);
        ArgumentNullException.ThrowIfNull(expected);
        if (transactionId is not { Length: 32 } || transactionId.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw BackupInvalid("数据库恢复事务 ID 无效");
        // 冻结调用方集合，异步验证过程中不跟随描述变动。
        expected = JsonSerializer.Deserialize<DatabaseBackupDescriptor>(JsonSerializer.SerializeToUtf8Bytes(expected, BackupJson), BackupJson)!;
        if (completed is not null && (completed.TransactionId != transactionId || completed.BackupId != expected.BackupId))
            throw BackupInvalid("恢复结果身份与固定事务不匹配");
        var paths = lease.Paths;
        if (lease.DatabaseId is null || expected.DatabaseId != lease.DatabaseId || expected.RootId != paths.RootId)
            throw BackupInvalid("数据库恢复身份与数据根不匹配");
        using var backup = await OpenVerifiedBackupAsync(paths, expected.BackupId, expected, token).ConfigureAwait(false);
        var destination = paths.Database;
        var retained = paths.Resolve("recovery", "database-" + transactionId + ".sqlite");
        var temporary = paths.Resolve("store", "restore-" + transactionId + "-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            if (File.Exists(retained))
            {
                // 原子替换已发生；新写入使哈希不同，禁止再次覆盖。
                RequireNoDatabaseSidecars(destination);
                using var current = OpenRestoreFile(destination);
                await RequireRestoreHashAsync(current, expected, token).ConfigureAwait(false);
                using var previous = OpenRestoreFile(retained);
                var result = new DatabaseRestoreResult(transactionId, expected.BackupId, await HashRestoreFileAsync(previous, token).ConfigureAwait(false));
                if (completed is not null && completed != result) throw BackupInvalid("保留数据库与已记录的恢复证据不匹配");
                return result;
            }
            if (completed is not null) throw BackupInvalid("已记录的数据库恢复缺少保留证据");
            if (Directory.Exists(retained)) throw BackupInvalid("数据库恢复保留路径被目录占用");
            EnsureBackupSpace(expected.SizeBytes, paths.DataRoot);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                backup.Database.Position = 0;
                await backup.Database.CopyToAsync(output, token).ConfigureAwait(false);
                await RequireRestoreHashAsync(output, expected, token).ConfigureAwait(false);
                output.Flush(true);
            }
            if (persisted is not null) await persisted(DatabaseRestorePersistencePoint.CandidateVerified).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                LocalDiscoverySecurity.RejectLinks(destination + suffix);
            using (var current = OpenBackupDatabase(destination, SqliteOpenMode.ReadWrite))
            {
                var state = InspectBackupDatabase(current);
                if (state.Root != expected.RootId || state.Database != expected.DatabaseId || state.Mode != expected.BackendMode)
                    throw BackupInvalid("当前数据库身份与恢复备份不匹配");
                using var checkpoint = current.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                using (var row = checkpoint.ExecuteReader())
                    if (!row.Read() || row.GetInt32(0) != 0) throw BackupInvalid("当前数据库无法完成 WAL checkpoint");
                checkpoint.CommandText = "PRAGMA journal_mode=DELETE";
                if (!string.Equals(checkpoint.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                    throw BackupInvalid("当前数据库无法退出 WAL 模式");
            }
            RequireNoDatabaseSidecars(destination);
            if (persisted is not null) await persisted(DatabaseRestorePersistencePoint.SourceCheckpointed).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // 校验期间拒绝其他写入；Windows 替换前关闭文件句柄，生命周期锁继续保持。
            string previousHash;
            using (var original = OpenRestoreFile(destination))
            {
                original.Flush(true);
                previousHash = await HashRestoreFileAsync(original, token).ConfigureAwait(false);
            }
            using (var candidate = OpenRestoreFile(temporary))
                await RequireRestoreHashAsync(candidate, expected, token).ConfigureAwait(false);
            RequireNoDatabaseSidecars(destination);
            LocalDiscoverySecurity.RejectLinks(retained);
            token.ThrowIfCancellationRequested();
            File.Replace(temporary, destination, retained);
            if (persisted is not null) await persisted(DatabaseRestorePersistencePoint.Replaced).ConfigureAwait(false);
            return new(transactionId, expected.BackupId, previousHash);
        }
        catch (SqliteException error) { throw new StorageException("storage.backupInvalid", "离线数据库恢复验证失败", error); }
        finally
        {
            LocalDiscoverySecurity.RejectLinks(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static FileStream OpenRestoreFile(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        LocalDiscoverySecurity.RejectLinks(path);
        return new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static void RequireNoDatabaseSidecars(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            LocalDiscoverySecurity.RejectLinks(path + suffix);
            if (File.Exists(path + suffix) || Directory.Exists(path + suffix))
                throw BackupInvalid("数据库恢复要求独立主文件，仍存在 journal/WAL/SHM");
        }
    }

    private static async Task RequireRestoreHashAsync(FileStream file, DatabaseBackupDescriptor expected, CancellationToken token)
    {
        if (file.Length != expected.SizeBytes ||
            !string.Equals(await HashRestoreFileAsync(file, token).ConfigureAwait(false), expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw BackupInvalid("数据库恢复内容与固定备份不匹配，或恢复后已有新数据");
    }

    private static async Task<string> HashRestoreFileAsync(FileStream file, CancellationToken token)
    {
        if (file.Length is <= 0 or > MaximumBackupDatabaseBytes) throw BackupInvalid("数据库恢复文件大小超限");
        file.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).ToLowerInvariant();
    }
}
