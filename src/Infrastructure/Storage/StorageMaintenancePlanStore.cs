using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed record StorageMaintenancePlanRecord(StorageMaintenancePlan Plan, string Epoch, string DataDigest,
    string? BackupDigest, StorageDirectoryEvidence? Target);

public sealed class StorageMaintenancePlanStore(SqliteStore database, DataBackupStore backups, StorageDataCatalog data,
    string epoch, IReadOnlyList<DatabaseMigration> migrations, TimeProvider clock)
{
    public static DatabaseMigration Migration { get; } = new("platform-data-maintenance", 1, """
        CREATE TABLE platform_storage_maintenance_plans(id TEXT PRIMARY KEY,owner TEXT NOT NULL,kind TEXT NOT NULL,
            expires INTEGER NOT NULL,record TEXT NOT NULL) STRICT;
        CREATE TABLE platform_storage_maintenance_keys(owner TEXT NOT NULL,kind TEXT NOT NULL,key TEXT NOT NULL,
            hash TEXT NOT NULL,plan_id TEXT NOT NULL REFERENCES platform_storage_maintenance_plans(id) ON DELETE CASCADE,
            PRIMARY KEY(owner,kind,key)) STRICT;
        """);
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public static string ETag(StorageMaintenancePlan plan) => "\"storage-maintenance-" + plan.Id + "-" + plan.Revision + "\"";
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options)));

    public async Task<StorageMaintenancePlan> PreviewRestoreAsync(string owner, StorageRestoreRequest request, string? key, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(request.BackupId, "N", out _)) throw new OperationException("request.invalid", "备份身份无效");
        FileTransferStore.Key(key);
        if (Retry(owner, "restore", Hash(request), key!) is { } retry) return retry.Plan;
        var backup = backups.Records().SingleOrDefault(x => x.Snapshot.SnapshotId == request.BackupId && x.State == "complete")
            ?? throw new OperationException("resource.notFound", "完整备份不存在");
        using var activity = database.Activities.EnterActivity("storage", "storage.restorePreview");
        await database.VerifyDataSnapshotAsync(backup.Snapshot.SnapshotId, backup.Snapshot, ct);
        try { await database.PreflightOrdinaryDatabaseMigrationsAsync(backup.Snapshot.Database, migrations, ct); }
        catch (StorageException error) when (error.Code is "storage.migrationInvalid" or "storage.migrationIncompatible")
        { throw new StorageException("storage.backupIncompatible", "备份 schema 与当前宿主不兼容", error); }
        var current = await data.CaptureAsync(ct);
        return await SavePreviewAsync(owner, "restore", Hash(request), key!, current, backup, null, ct);
    }

    public async Task<StorageMaintenancePlan> PreviewMigrationAsync(string owner, StorageMigrationRequest request,
        StorageDirectoryEvidence target, string? key, CancellationToken ct = default)
    {
        FileTransferStore.Key(key);
        if (Retry(owner, "migrate", Hash(request), key!) is { } retry) return retry.Plan;
        using var activity = database.Activities.EnterActivity("storage", "storage.migrationPreview");
        target.Verify(); var current = await data.CaptureAsync(ct);
        return await SavePreviewAsync(owner, "migrate", Hash(request), key!, current, null, target, ct);
    }

    private Task<StorageMaintenancePlan> SavePreviewAsync(string owner, string kind, string hash, string key,
        StorageDataEvidence current, DataBackupRecord? backup, StorageDirectoryEvidence? target, CancellationToken ct) => database.WriteAsync(tx =>
    {
        tx.Execute("DELETE FROM platform_storage_maintenance_plans WHERE expires<=$now", ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()));
        if (tx.Scalar("SELECT plan_id FROM platform_storage_maintenance_keys WHERE owner=$owner AND kind=$kind AND key=$key",
            ("$owner", owner), ("$kind", kind), ("$key", key)) is string prior)
        {
            if ((string?)tx.Scalar("SELECT hash FROM platform_storage_maintenance_keys WHERE plan_id=$id", ("$id", prior)) != hash)
                throw new OperationException("idempotency.conflict", "幂等键已用于其他数据预览");
            return JsonSerializer.Deserialize<StorageMaintenancePlanRecord>((string)tx.Scalar("SELECT record FROM platform_storage_maintenance_plans WHERE id=$id", ("$id", prior))!, ContractJson.Options)!.Plan;
        }
        if ((long)tx.Scalar("SELECT count(*) FROM platform_storage_maintenance_plans")! >= 16)
            throw new OperationException("resource.capacityExceeded", "数据维护预览数量达到上限");
        var now = clock.GetUtcNow();
        var plan = new StorageMaintenancePlan(Guid.NewGuid().ToString("N"), owner, kind, new(1), now, now + Lifetime,
            backup is null ? null : DataBackupStore.Summary(backup), current.Counts, target is null ? null : Path.GetFileName(target.Path));
        var record = new StorageMaintenancePlanRecord(plan, epoch, current.Digest, backup is null ? null : Hash(backup.Snapshot), target);
        tx.Execute("INSERT INTO platform_storage_maintenance_plans VALUES($id,$owner,$kind,$expires,$record)", ("$id", plan.Id), ("$owner", owner),
            ("$kind", kind), ("$expires", plan.ExpiresAt.ToUnixTimeMilliseconds()), ("$record", JsonSerializer.Serialize(record, ContractJson.Options)));
        tx.Execute("INSERT INTO platform_storage_maintenance_keys VALUES($owner,$kind,$key,$hash,$id)", ("$owner", owner), ("$kind", kind), ("$key", key), ("$hash", hash), ("$id", plan.Id));
        return plan;
    }, ct);

    private StorageMaintenancePlanRecord? Retry(string owner, string kind, string hash, string key)
    {
        var id = database.ReadScalar("SELECT plan_id FROM platform_storage_maintenance_keys WHERE owner=$owner AND kind=$kind AND key=$key",
            ("$owner", owner), ("$kind", kind), ("$key", key)) as string;
        if (id is null) return null;
        if ((string?)database.ReadScalar("SELECT hash FROM platform_storage_maintenance_keys WHERE owner=$owner AND kind=$kind AND key=$key",
            ("$owner", owner), ("$kind", kind), ("$key", key)) != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他数据预览");
        var value = GetRecord(id, owner, kind); Validate(value); return value;
    }

    public StorageMaintenancePlan Get(string id, string owner, string kind) => GetRecord(id, owner, kind).Plan;
    public StorageMaintenancePlanRecord GetRecord(string id, string owner, string kind)
    {
        var json = database.ReadScalar("SELECT record FROM platform_storage_maintenance_plans WHERE id=$id AND owner=$owner AND kind=$kind",
            ("$id", id), ("$owner", owner), ("$kind", kind)) as string;
        return json is null ? throw new OperationException("resource.notFound", "数据维护预览不存在") :
            JsonSerializer.Deserialize<StorageMaintenancePlanRecord>(json, ContractJson.Options) ?? throw new StorageException("storage.assetDamaged", "数据维护预览损坏");
    }
    public async Task<StorageMaintenancePlanRecord> CheckAsync(string id, string owner, string kind, string? etag, CancellationToken ct = default)
    {
        var record = GetRecord(id, owner, kind); Validate(record);
        if (etag is null) throw new OperationException("revision.required", "数据维护需要预览 ETag");
        if (etag != ETag(record.Plan)) throw new OperationException("revision.stale", "数据维护预览修订已变化");
        await VerifyCurrentAsync(record, ct); return record;
    }
    public async Task VerifyCurrentAsync(StorageMaintenancePlanRecord record, CancellationToken ct = default)
    {
        Validate(record); record.Target?.Verify();
        if ((await data.CaptureAsync(ct)).Digest != record.DataDigest) throw new OperationException("storage.planStale", "预览后的业务数据已变化，请重新预览");
        if (record.Plan.Backup is { } backup)
        {
            var snapshot = BackedUpSnapshot(record);
            await database.VerifyDataSnapshotAsync(backup.Id, snapshot, ct);
        }
    }
    public DataSnapshotDescriptor BackedUpSnapshot(StorageMaintenancePlanRecord record)
    {
        var snapshot = backups.Records().SingleOrDefault(x => x.Snapshot.SnapshotId == record.Plan.Backup?.Id && x.State == "complete")?.Snapshot
            ?? throw new StorageException("storage.backupInvalid", "预览备份已不可用");
        if (Hash(snapshot) != record.BackupDigest) throw new OperationException("storage.planStale", "预览备份内容已变化");
        return snapshot;
    }
    private void Validate(StorageMaintenancePlanRecord record)
    {
        if (record.Epoch != epoch || record.Plan.State != "planned") throw new OperationException("storage.planStale", "数据维护预览来自旧宿主或已执行");
        if (clock.GetUtcNow() >= record.Plan.ExpiresAt) throw new OperationException("storage.planExpired", "数据维护预览已到期");
    }
}
