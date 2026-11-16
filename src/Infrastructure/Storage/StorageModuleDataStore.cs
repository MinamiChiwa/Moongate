using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed record StorageModuleDeletionRecord(StorageModuleDeletionPlan Plan, string Epoch, string DataDigest);

public sealed class StorageModuleDataStore(SqliteStore database, AssetStore assets, StorageDataCatalog data,
    OperationStore operations, IReadOnlyList<IStorageModuleDataOwner> owners, Func<string, ModuleConfiguration> configuration,
    string epoch, TimeProvider clock, Action<DatabaseTransaction, string>? resetPlatformData = null)
{
    public static DatabaseMigration Migration { get; } = new("platform-module-data", 1, """
        CREATE TABLE platform_module_data_state(module_id TEXT PRIMARY KEY,revision TEXT NOT NULL) STRICT;
        CREATE TABLE platform_module_deletion_plans(id TEXT PRIMARY KEY,owner TEXT NOT NULL,expires INTEGER NOT NULL,record TEXT NOT NULL) STRICT;
        CREATE TABLE platform_module_deletion_keys(owner TEXT NOT NULL,key TEXT NOT NULL,hash TEXT NOT NULL,plan_id TEXT NOT NULL,expires INTEGER NOT NULL,
            PRIMARY KEY(owner,key)) STRICT;
        """);
    public static string ETag(StorageModuleDeletionPlan plan) => "\"module-deletion-" + plan.Id + "-" + plan.Revision + "\"";
    public const int MaximumPendingPlans = 16;
    public static TimeSpan PreviewLifetime { get; } = TimeSpan.FromMinutes(15);

    private IStorageModuleDataOwner Owner(string moduleId) => owners.SingleOrDefault(x => x.ModuleId == moduleId)
        ?? throw new OperationException("module.notFound", "模块没有已登记的数据所有者");

    public async Task<StorageModuleDataSnapshot> DescribeAsync(string moduleId, CancellationToken ct = default)
    {
        _ = Owner(moduleId);
        var state = await data.CaptureAsync(ct);
        return Describe(moduleId, state);
    }

    private StorageModuleDataSnapshot Describe(string moduleId, StorageDataEvidence state)
    {
        var config = configuration(moduleId); var content = assets.ModuleData(moduleId);
        var reasons = new List<string>();
        if (config.Enabled) reasons.Add("storage.moduleEnabled");
        if (content.References.Count > 0 || content.ActivePins) reasons.Add("storage.assetInUse");
        return new(moduleId, config.Enabled, config.Revision, state.Counts.Single(x => x.Module == moduleId).Records,
            new(content.Assets), new(content.Bytes), content.References, reasons);
    }

    public async Task<StorageModuleDeletionPlan> PreviewAsync(string owner, StorageModuleDeletionRequest request, string? key, CancellationToken ct = default)
    {
        FileTransferStore.Key(key); _ = Owner(request.ModuleId);
        using var activity = database.Activities.EnterActivity("storage", "storage.moduleDeletionPreview");
        var hash = StorageMaintenancePlanStore.Hash(JsonSerializer.SerializeToElement(request, ContractJson.Options));
        var saved = database.ReadScalar("SELECT plan_id FROM platform_module_deletion_keys WHERE owner=$owner AND key=$key AND expires>$now",
            ("$owner", owner), ("$key", key), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds())) as string;
        if (saved is not null)
        {
            if ((string?)database.ReadScalar("SELECT hash FROM platform_module_deletion_keys WHERE owner=$owner AND key=$key", ("$owner", owner), ("$key", key)) != hash)
                throw new OperationException("idempotency.conflict", "模块删除预览的幂等键已用于其他内容");
            return Get(saved, owner);
        }
        var state = await data.CaptureAsync(ct); var description = Describe(request.ModuleId, state);
        var now = clock.GetUtcNow();
        var plan = new StorageModuleDeletionPlan(Guid.NewGuid().ToString("N"), owner, new(1), now, now + PreviewLifetime, description);
        var record = new StorageModuleDeletionRecord(plan, epoch, state.Digest);
        return await database.WriteAsync(tx =>
        {
            tx.Execute("DELETE FROM platform_module_deletion_keys WHERE expires<=$now", ("$now", now.ToUnixTimeMilliseconds()));
            tx.Execute("DELETE FROM platform_module_deletion_plans WHERE expires<=$now", ("$now", now.ToUnixTimeMilliseconds()));
            if ((long)tx.Scalar("SELECT count(*) FROM platform_module_deletion_plans WHERE json_extract(record,'$.plan.state')='planned'")! >= MaximumPendingPlans ||
                (long)tx.Scalar("SELECT count(*) FROM platform_module_deletion_plans")! >= 128)
                throw new OperationException("resource.capacityExceeded", "模块删除预览达到上限");
            if (tx.Scalar("SELECT plan_id FROM platform_module_deletion_keys WHERE owner=$owner AND key=$key", ("$owner", owner), ("$key", key)) is string concurrent)
            {
                if ((string?)tx.Scalar("SELECT hash FROM platform_module_deletion_keys WHERE owner=$owner AND key=$key", ("$owner", owner), ("$key", key)) != hash)
                    throw new OperationException("idempotency.conflict", "模块删除预览的幂等键已用于其他内容");
                return Decode((string)tx.Scalar("SELECT record FROM platform_module_deletion_plans WHERE id=$id", ("$id", concurrent))!).Plan;
            }
            tx.Execute("INSERT INTO platform_module_deletion_plans VALUES($id,$owner,$expires,$record)", ("$id", plan.Id), ("$owner", owner),
                ("$expires", plan.ExpiresAt.ToUnixTimeMilliseconds()), ("$record", JsonSerializer.Serialize(record, ContractJson.Options)));
            tx.Execute("INSERT INTO platform_module_deletion_keys VALUES($owner,$key,$hash,$id,$expires)", ("$owner", owner), ("$key", key), ("$hash", hash),
                ("$id", plan.Id), ("$expires", plan.ExpiresAt.ToUnixTimeMilliseconds()));
            return plan;
        }, ct);
    }

    public StorageModuleDeletionPlan Get(string id, string owner) => Read(id, owner).Plan;
    private StorageModuleDeletionRecord Read(string id, string owner) => database.ReadScalar(
        "SELECT record FROM platform_module_deletion_plans WHERE id=$id AND owner=$owner", ("$id", id), ("$owner", owner)) is string json ? Decode(json) :
        throw new OperationException("resource.notFound", "模块删除预览不存在");
    private static StorageModuleDeletionRecord Decode(string json) => JsonSerializer.Deserialize<StorageModuleDeletionRecord>(json, ContractJson.Options)
        ?? throw new StorageException("storage.assetDamaged", "模块删除预览损坏");

    public async Task<StorageModuleDeletionRecord> CheckAsync(string id, string owner, string? etag, CancellationToken ct = default)
    {
        var record = Read(id, owner);
        if (etag is null) throw new OperationException("revision.required", "模块删除需要 If-Match");
        if (etag != ETag(record.Plan)) throw new OperationException("revision.stale", "模块删除预览修订不匹配");
        await VerifyCurrentAsync(record, ct); return record;
    }
    public async Task VerifyCurrentAsync(StorageModuleDeletionRecord record, CancellationToken ct = default)
    {
        if (record.Epoch != epoch || record.Plan.State != "planned") throw new OperationException("storage.planStale", "模块删除预览已失效");
        if (record.Plan.ExpiresAt <= clock.GetUtcNow()) throw new OperationException("storage.planExpired", "模块删除预览已到期");
        var current = await data.CaptureAsync(ct); var content = Describe(record.Plan.Data.ModuleId, current);
        if (current.Digest != record.DataDigest || content.ConfigurationRevision != record.Plan.Data.ConfigurationRevision)
            throw new OperationException("storage.planStale", "模块数据、配置或引用在预览后改变");
        if (content.BlockingReasons.FirstOrDefault() is { } reason)
            throw new OperationException(reason, "模块数据删除条件未满足");
    }

    public async Task<OperationSnapshot> ApplyAsync(StorageModuleDeletionRecord record, string operationId,
        ActivityBarrier.MaintenanceLease maintenance, CancellationToken ct = default)
    {
        await VerifyCurrentAsync(record, ct);
        var participant = Owner(record.Plan.Data.ModuleId); var deletion = participant.PrepareDeletion();
        var result = await database.WriteMaintenanceAsync(maintenance, tx =>
        {
            var savedConfig = tx.Scalar("SELECT enabled FROM core_module_configuration WHERE id=$id", ("$id", participant.ModuleId));
            if (savedConfig is not null && (long)savedConfig != 0) throw new OperationException("storage.moduleEnabled", "模块仍启用");
            operations.ForgetModuleHistory(tx, participant.OperationKinds, participant.EventTypes);
            deletion.Apply(tx); assets.ApplyModuleDataDeletion(tx, participant.ModuleId); resetPlatformData?.Invoke(tx, participant.ModuleId);
            var revision = UInt64String.Parse(tx.Scalar("SELECT revision FROM platform_module_data_state WHERE module_id=$id", ("$id", participant.ModuleId)) as string ?? "0");
            revision = new(checked(revision.Value + 1));
            tx.Execute("INSERT INTO platform_module_data_state VALUES($id,$revision) ON CONFLICT(module_id) DO UPDATE SET revision=excluded.revision",
                ("$id", participant.ModuleId), ("$revision", revision.ToString()));
            var completed = record with { Plan = record.Plan with { Revision = new(2), State = "completed" } };
            tx.Execute("UPDATE platform_module_deletion_plans SET record=$record,expires=$expires WHERE id=$id", ("$id", record.Plan.Id),
                ("$record", JsonSerializer.Serialize(completed, ContractJson.Options)), ("$expires", (clock.GetUtcNow() + OperationStore.KeyRetention).ToUnixTimeMilliseconds()));
            return operations.CompleteModuleDataDeletion(tx, operationId, new(record.Plan.Id, participant.ModuleId,
                record.Plan.Data.Assets, record.Plan.Data.AssetBytes, revision));
        }, ct);
        deletion.Committed?.Invoke();
        return result;
    }
}
