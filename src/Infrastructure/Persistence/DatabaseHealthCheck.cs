using System.Text.RegularExpressions;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

internal sealed record DatabaseHealthResult(string RootId, string DatabaseId, string BackendMode,
    IReadOnlyList<DatabaseMigrationStamp> Migrations);

public sealed partial class SqliteStore
{
    /// <summary>在维护权下只读复核固定的迁移目标；不执行迁移或修复。</summary>
    internal async Task<DatabaseHealthResult> VerifyDatabaseHealthAsync(ActivityBarrier.MaintenanceLease maintenance,
        string expectedDatabaseId, string expectedMode, IReadOnlyList<DatabaseMigrationStamp> expectedMigrations,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(expectedDatabaseId, "N", out _) || expectedMode is not ("live" or "mock") ||
            expectedMigrations is null || expectedMigrations.Count is < 4 or > 4096)
            throw BackupInvalid("数据库健康检查的目标身份或迁移历史无效");
        // 固定调用方集合；后续检查不跟随计划的内存修改。
        var expected = expectedMigrations.ToArray();
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stamp in expected.OrderBy(stamp => stamp?.ModuleId, StringComparer.Ordinal).ThenBy(stamp => stamp?.Version))
        {
            if (stamp is null || stamp.ModuleId is null || !Regex.IsMatch(stamp.ModuleId, @"\A[a-z][a-z0-9-]{0,63}\z") ||
                stamp.Version != versions.GetValueOrDefault(stamp.ModuleId) + 1 || stamp.Checksum is not { Length: 64 } ||
                !stamp.Checksum.All(Uri.IsHexDigit)) throw BackupInvalid("数据库健康检查的目标迁移历史无效");
            versions[stamp.ModuleId] = stamp.Version;
        }
        if (versions.GetValueOrDefault("core") < 4) throw BackupInvalid("数据库健康检查缺少核心迁移目标");
        expected = expected.OrderBy(stamp => stamp.ModuleId, StringComparer.Ordinal).ThenBy(stamp => stamp.Version).ToArray();
        using var activity = Activities.EnterMaintenanceActivity(maintenance, "storage.databaseHealth");
        await databaseBackupGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref stopped) != 0, this);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
            var ct = cancellation.Token;
            // SQLite 检查完成、只读连接关闭后才交还活动和所有权；取消不提前报告成功。
            return await Task.Run(() => Read(connection =>
            {
                ct.ThrowIfCancellationRequested();
                ExecuteBackupSql(connection, "BEGIN");
                try
                {
                    var state = InspectBackupDatabase(connection);
                    ct.ThrowIfCancellationRequested();
                    if (state.Root != paths.RootId || state.Database != expectedDatabaseId || state.Mode != expectedMode ||
                        !state.Migrations.SequenceEqual(expected))
                        throw BackupInvalid("活动数据库与固定健康检查目标不匹配");
                    return new DatabaseHealthResult(state.Root, state.Database, state.Mode,
                        Array.AsReadOnly(state.Migrations.ToArray()));
                }
                finally { ExecuteBackupSql(connection, "ROLLBACK"); }
            }), CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException error)
        {
            throw new StorageException("storage.backupInvalid", "数据库健康检查失败", error);
        }
        finally { databaseBackupGate.Release(); }
    }
}
