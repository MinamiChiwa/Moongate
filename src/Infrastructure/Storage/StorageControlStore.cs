using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed class StorageControlStore(SqliteStore database, PathService paths,
    IReadOnlyList<IStorageMaintenanceParticipant> participants, string epoch, TimeProvider? timeProvider = null,
    Func<IReadOnlyDictionary<string, long>>? quotaProvider = null)
{
    public static DatabaseMigration Migration { get; } = new("platform-storage-control", 1, """
        CREATE TABLE platform_storage_state(id INTEGER PRIMARY KEY CHECK(id=1),revision TEXT NOT NULL,fingerprint TEXT NOT NULL) STRICT;
        CREATE TABLE platform_storage_plans(id TEXT PRIMARY KEY,owner TEXT NOT NULL,epoch TEXT NOT NULL,
            expires INTEGER NOT NULL,state TEXT NOT NULL,snapshot TEXT NOT NULL,payload TEXT NOT NULL,result TEXT) STRICT;
        CREATE TABLE platform_storage_progress(plan_id TEXT NOT NULL REFERENCES platform_storage_plans(id) ON DELETE CASCADE,
            candidate_id TEXT NOT NULL,result TEXT NOT NULL,PRIMARY KEY(plan_id,candidate_id)) STRICT;
        CREATE TABLE platform_storage_keys(owner TEXT NOT NULL,kind TEXT NOT NULL,key TEXT NOT NULL,hash TEXT NOT NULL,
            plan_id TEXT NOT NULL,receipt TEXT,expires INTEGER NOT NULL,PRIMARY KEY(owner,kind,key)) STRICT;
        """);
    public const int MaximumPlans = 32, MaximumCandidates = 1000, MaximumKeys = 1000;
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);
    private static readonly string[] CleanupCategories = ["cache", "logs", "diagnostics", "temporary", "backups", "orphans"];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record FixedCandidate(string Id, string Participant, StorageManagedItem Item);
    private sealed record SavedPayload(long CreatedTimestamp, IReadOnlyList<FixedCandidate> Candidates);
    private sealed record LocatedItem(string Participant, StorageManagedItem Item);
    private IReadOnlyDictionary<string, long> Quotas => quotaProvider?.Invoke() ?? new Dictionary<string, long>
    { ["cache"] = 512L * 1024 * 1024, ["logs"] = 50L * 1024 * 1024, ["diagnostics"] = 1024L * 1024 * 1024 };
    public static string ETag(StorageCleanupPlan plan) => "\"storage-cleanup-" + plan.Id + "-" + plan.Revision + "\"";

    public async Task<StorageUsageSnapshot> UsageAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var items = await InspectAsync(ct); var quotas = Quotas;
            var categories = items.GroupBy(x => x.Item.Category, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Sum(i => i.Item.Bytes), StringComparer.Ordinal);
            var rows = items.GroupBy(x => (x.Item.Category, x.Item.Module)).OrderBy(x => x.Key.Category, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Module, StringComparer.Ordinal).Select(group => new StorageCategoryUsage(
                    group.Key.Category, group.Key.Module, new((ulong)group.Sum(x => x.Item.Bytes)),
                    new((ulong)group.Where(x => x.Item.ProtectionReasons.Count > 0).Sum(x => x.Item.Bytes)),
                    quotas.TryGetValue(group.Key.Category, out var limit) ? new UInt64String((ulong)limit) : null,
                    quotas.ContainsKey(group.Key.Category) && categories[group.Key.Category] > quotas[group.Key.Category],
                    group.SelectMany(x => x.Item.ProtectionReasons).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray();
            var fingerprint = Hash(items.OrderBy(x => x.Participant, StringComparer.Ordinal).ThenBy(x => x.Item.Key, StringComparer.Ordinal));
            var revision = await database.WriteAsync(tx =>
            {
                var previous = tx.Scalar("SELECT fingerprint FROM platform_storage_state WHERE id=1") as string;
                var current = tx.Scalar("SELECT revision FROM platform_storage_state WHERE id=1") as string;
                if (previous == fingerprint) return UInt64String.Parse(current!);
                var next = checked((current is null ? 0UL : UInt64String.Parse(current).Value) + 1);
                tx.Execute("INSERT INTO platform_storage_state VALUES(1,$revision,$fingerprint) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,fingerprint=excluded.fingerprint",
                    ("$revision", next.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$fingerprint", fingerprint));
                return new UInt64String(next);
            }, ct);
            return new(revision, clock.GetUtcNow(), rows, database.Space.Snapshot([paths.DataRoot, .. participants.SelectMany(x => x.VolumeRoots)]));
        }
        finally { gate.Release(); }
    }

    public async Task<StorageCleanupPlan> PreviewAsync(string owner, StorageCleanupRequest request, string? key, CancellationToken ct = default)
    {
        Key(key); var selected = Categories(request); var hash = Hash(selected);
        await gate.WaitAsync(ct);
        try
        {
            if (Receipt<StorageCleanupPlan>(owner, "preview", key!, hash) is { } previous) return previous;
            await PruneAsync(ct);
            if ((long)database.ReadScalar("SELECT count(*) FROM platform_storage_plans WHERE state='planned' AND epoch=$epoch AND expires>$now",
                ("$epoch", epoch), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()))! >= MaximumPlans)
                throw new OperationException("resource.capacityExceeded", "清理计划数量达到上限");
            var all = await InspectAsync(ct); var eligible = SelectEligible(all, Quotas);
            var candidates = new List<FixedCandidate>(); var blocked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in eligible.Where(x => selected.Contains(x.Item.Category, StringComparer.Ordinal)).Take(MaximumCandidates))
            {
                try
                {
                    var frozen = await Participant(item.Participant).FreezeAsync(item.Item, ct);
                    candidates.Add(new(Guid.NewGuid().ToString("N"), item.Participant, frozen));
                }
                catch (IOException error) when (error is not StorageException) { blocked.Add("fileInUse"); }
            }
            if (eligible.Count(x => selected.Contains(x.Item.Category, StringComparer.Ordinal)) > MaximumCandidates) blocked.Add("candidateCapacity");
            foreach (var category in all.GroupBy(x => x.Item.Category, StringComparer.Ordinal))
                if (Quotas.TryGetValue(category.Key, out var budget) && category.Sum(x => x.Item.Bytes) > budget &&
                    !eligible.Any(x => x.Item.Category == category.Key)) blocked.Add(category.Key + ":protectedBudget");
            var now = clock.GetUtcNow(); var id = Guid.NewGuid().ToString("N");
            var plan = new StorageCleanupPlan(id, owner, new(1), now, now + PlanLifetime,
                candidates.Select(x => new StorageCleanupCandidate(x.Id, x.Item.Category, x.Item.Module, new((ulong)x.Item.Bytes), x.Item.CleanupReason)).ToArray(),
                new((ulong)all.Where(x => x.Item.ProtectionReasons.Count > 0).Sum(x => x.Item.Bytes)), blocked.Order(StringComparer.Ordinal).ToArray());
            var encoded = Encode(plan); var payload = Encode(new SavedPayload(clock.GetTimestamp(), candidates));
            await database.WriteAsync(tx =>
            {
                RequireKeyCapacity(tx);
                tx.Execute("INSERT INTO platform_storage_plans VALUES($id,$owner,$epoch,$expires,'planned',$snapshot,$payload,NULL)",
                    ("$id", id), ("$owner", owner), ("$epoch", epoch), ("$expires", plan.ExpiresAt.ToUnixTimeMilliseconds()), ("$snapshot", encoded), ("$payload", payload));
                SaveKey(tx, owner, "preview", key!, hash, id, encoded);
                return 0;
            }, ct);
            return plan;
        }
        finally { gate.Release(); }
    }

    public StorageCleanupPlan GetPlan(string id, string owner) => ReadPlan(id, owner).Plan;

    public async Task<bool> CleanupNeededAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return SelectEligible(await InspectAsync(ct), Quotas).Count > 0; }
        finally { gate.Release(); }
    }
    public StorageCleanupResult Result(string id, string owner)
    {
        ReadPlan(id, owner);
        return database.ReadScalar("SELECT result FROM platform_storage_plans WHERE id=$id", ("$id", id)) is string result
            ? Decode<StorageCleanupResult>(result) : throw new OperationException("resource.notFound", "清理结果尚未提交");
    }

    public async Task<StorageCleanupResult> ExecuteAsync(string id, string owner, string? key, string? ifMatch, CancellationToken ct = default)
    {
        Key(key);
        if (ifMatch is null) throw new OperationException("revision.required", "执行清理需要 If-Match");
        var hash = Hash(new { id, ifMatch });
        await gate.WaitAsync(ct);
        try
        {
            if (Receipt<StorageCleanupResult>(owner, "execute", key!, hash) is { } previous) return previous;
            var (plan, payload, savedEpoch) = ReadPlan(id, owner);
            if (plan.State != "planned") throw new OperationException("resource.busy", "清理计划已执行或正在执行");
            if (ifMatch != ETag(plan)) throw new OperationException("revision.stale", "清理计划修订已变化");
            if (savedEpoch != epoch || clock.GetElapsedTime(payload.CreatedTimestamp) >= PlanLifetime || clock.GetUtcNow() >= plan.ExpiresAt)
                throw new OperationException("storage.planExpired", "清理预览已到期或来自旧宿主，请重新预览");
            using var maintenance = database.Activities.TryEnterMaintenance("storage-cleanup:" + id)
                ?? throw new OperationException("resource.busy", "活动任务仍在使用存储，请在活动结束后执行清理",
                    new Dictionary<string, JsonElement> { ["activities"] = JsonSerializer.SerializeToElement(database.Activities.Snapshot(), ContractJson.Options) });
            var fresh = await InspectAsync(ct); var currentEligible = SelectEligible(fresh, Quotas)
                .ToDictionary(x => (x.Participant, x.Item.Key));
            var current = fresh.ToDictionary(x => (x.Participant, x.Item.Key));
            var executing = plan with { Revision = new(2), State = "executing" };
            var executingJson = Encode(executing);
            await database.WriteMaintenanceAsync(maintenance, tx =>
            {
                RequireKeyCapacity(tx);
                tx.Execute("UPDATE platform_storage_plans SET state='executing',snapshot=$json WHERE id=$id", ("$json", executingJson), ("$id", id));
                SaveKey(tx, owner, "execute", key!, hash, id, null);
                return 0;
            }, ct);
            var results = new List<StorageCleanupItemResult>(); var state = "completed";
            try
            {
                foreach (var candidate in payload.Candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    StorageCleanupItemResult result;
                    if (!current.TryGetValue((candidate.Participant, candidate.Item.Key), out var live)) result = new(candidate.Id, "skipped", new(0), "missing");
                    else if (live.Item.Revision != candidate.Item.Revision) result = new(candidate.Id, "skipped", new(0), "changed");
                    else if (!currentEligible.ContainsKey((candidate.Participant, candidate.Item.Key)))
                        result = new(candidate.Id, "skipped", new(0), live.Item.ProtectionReasons.FirstOrDefault() ?? "retentionProtected");
                    else
                    {
                        await ProgressAsync(id, new(candidate.Id, "pending", new(0), null), maintenance);
                        var deleted = await Participant(candidate.Participant).DeleteAsync(candidate.Item, maintenance, ct);
                        result = new(candidate.Id, deleted.Deleted ? "removed" : "skipped", new((ulong)deleted.ReclaimedBytes), deleted.Reason);
                    }
                    results.Add(result); await ProgressAsync(id, result, maintenance);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { state = "cancelled"; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationException)
            {
                state = "failed";
                var reason = error is StorageException storage ? storage.Code : error is OperationException operation ? operation.Code : "storage.unavailable";
                FillRemaining(payload, results, reason);
            }
            FillRemaining(payload, results, state == "cancelled" ? "cancelled" : "notAttempted");
            var completed = new StorageCleanupResult(id, new(3), clock.GetUtcNow(), new(results.Aggregate(0UL, (sum, item) => checked(sum + item.ReclaimedBytes.Value))), results, state);
            await FinishAsync(executing, completed, maintenance);
            return completed;
        }
        finally { gate.Release(); }
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var pending = database.Read(connection =>
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT id,owner FROM platform_storage_plans WHERE state='executing' AND epoch<>$epoch";
            command.Parameters.AddWithValue("$epoch", epoch); using var rows = command.ExecuteReader(); var result = new List<(string Id, string Owner)>();
            while (rows.Read()) result.Add((rows.GetString(0), rows.GetString(1))); return result;
        });
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested(); var (plan, payload, _) = ReadPlan(item.Id, item.Owner);
            var progress = database.Read(connection =>
            {
                using var command = connection.CreateCommand(); command.CommandText = "SELECT result FROM platform_storage_progress WHERE plan_id=$id";
                command.Parameters.AddWithValue("$id", item.Id); using var rows = command.ExecuteReader(); var result = new List<StorageCleanupItemResult>();
                while (rows.Read()) { var row = Decode<StorageCleanupItemResult>(rows.GetString(0)); result.Add(row.State == "pending" ? row with { State = "unknown", Reason = "backendRestarted" } : row); }
                return result;
            });
            FillRemaining(payload, progress, "backendRestarted");
            await FinishAsync(plan, new(item.Id, new(3), clock.GetUtcNow(), new(progress.Aggregate(0UL, (sum, row) => checked(sum + row.ReclaimedBytes.Value))), progress, "interrupted"), null);
        }
    }

    private async Task FinishAsync(StorageCleanupPlan plan, StorageCleanupResult result, ActivityBarrier.MaintenanceLease? maintenance)
    {
        var final = plan with { Revision = result.Revision, State = result.State };
        var planJson = Encode(final); var resultJson = Encode(result);
        var changed = JsonSerializer.SerializeToElement(new StorageChanged(plan.Owner, plan.Id, result.Revision, result.State, result.ReclaimedBytes), ContractJson.Options);
        Func<DatabaseTransaction, int> save = tx =>
        {
            tx.Execute("UPDATE platform_storage_plans SET state=$state,snapshot=$snapshot,result=$result WHERE id=$id",
                ("$state", result.State), ("$snapshot", planJson), ("$result", resultJson), ("$id", plan.Id));
            tx.Execute("UPDATE platform_storage_keys SET receipt=$receipt WHERE plan_id=$id AND kind='execute'", ("$receipt", resultJson), ("$id", plan.Id));
            SqliteStore.AppendEvent(tx, new("storage-" + plan.Id + "-" + result.Revision, "storage-cleanup", plan.Id,
                result.Revision, "storage.changed", changed));
            return 0;
        };
        if (maintenance is null) await database.WriteAsync(save); else await database.WriteMaintenanceAsync(maintenance, save);
    }
    private Task ProgressAsync(string id, StorageCleanupItemResult result, ActivityBarrier.MaintenanceLease maintenance)
    {
        var encoded = Encode(result);
        return database.WriteMaintenanceAsync(maintenance, tx => tx.Execute(
            "INSERT INTO platform_storage_progress VALUES($id,$candidate,$result) ON CONFLICT(plan_id,candidate_id) DO UPDATE SET result=excluded.result",
            ("$id", id), ("$candidate", result.Id), ("$result", encoded)));
    }
    private (StorageCleanupPlan Plan, SavedPayload Payload, string Epoch) ReadPlan(string id, string owner) => database.Read(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT snapshot,payload,epoch FROM platform_storage_plans WHERE id=$id AND owner=$owner";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$owner", owner); using var row = command.ExecuteReader();
        if (!row.Read()) throw new OperationException("resource.notFound", "清理计划不存在");
        return (Decode<StorageCleanupPlan>(row.GetString(0)), Decode<SavedPayload>(row.GetString(1)), row.GetString(2));
    });
    private async Task<IReadOnlyList<LocatedItem>> InspectAsync(CancellationToken ct)
    {
        if (participants.Count > 32 || participants.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != participants.Count)
            throw new StorageException("storage.assetInvalid", "存储所有者登记重复或超过预算");
        var result = new List<LocatedItem>();
        foreach (var participant in participants)
        {
            var items = await participant.InspectAsync(ct);
            if (result.Count + items.Count > 100000 || items.Any(x => x.Bytes < 0) || items.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != items.Count)
                throw new StorageException("resource.capacityExceeded", "存储用量超过巡检预算或存在重复身份");
            result.AddRange(items.Select(x => new LocatedItem(participant.Id, x)));
        }
        return result;
    }
    private static IReadOnlyList<LocatedItem> SelectEligible(IReadOnlyList<LocatedItem> items, IReadOnlyDictionary<string, long> quotas)
    {
        var selected = new List<LocatedItem>();
        foreach (var category in items.GroupBy(x => x.Item.Category, StringComparer.Ordinal))
        {
            var remaining = category.Sum(x => x.Item.Bytes);
            foreach (var item in category.OrderBy(x => x.Item.LastUsedAt).ThenBy(x => x.Item.Key, StringComparer.Ordinal))
            {
                if (!item.Item.Eligible || item.Item.ProtectionReasons.Count > 0 || !CleanupCategories.Contains(item.Item.Category, StringComparer.Ordinal)) continue;
                if (category.Key is "cache" or "logs" && quotas.TryGetValue(category.Key, out var limit) && remaining <= limit) continue;
                selected.Add(item); remaining -= item.Item.Bytes;
            }
        }
        return selected;
    }
    private IStorageMaintenanceParticipant Participant(string id) => participants.Single(x => x.Id == id);
    private T? Receipt<T>(string owner, string kind, string key, string hash) where T : class => database.Read(connection =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT hash,receipt FROM platform_storage_keys WHERE owner=$owner AND kind=$kind AND key=$key AND expires>$now";
        command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()); using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        if (row.GetString(0) != hash) throw new OperationException("idempotency.conflict", "该幂等键已用于其他存储请求");
        if (row.IsDBNull(1)) throw new OperationException("resource.busy", "该清理请求正在执行或等待启动恢复");
        return Decode<T>(row.GetString(1));
    });
    private Task PruneAsync(CancellationToken ct) => database.WriteAsync(tx =>
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        tx.Execute("DELETE FROM platform_storage_keys WHERE expires<=$now", ("$now", now));
        tx.Execute("DELETE FROM platform_storage_plans WHERE state<>'executing' AND expires<=$now AND NOT EXISTS(SELECT 1 FROM platform_storage_keys k WHERE k.plan_id=platform_storage_plans.id)", ("$now", now));
        return 0;
    }, ct);
    private void SaveKey(DatabaseTransaction tx, string owner, string kind, string key, string hash, string planId, string? receipt) =>
        tx.Execute("INSERT INTO platform_storage_keys VALUES($owner,$kind,$key,$hash,$plan,$receipt,$expires)",
            ("$owner", owner), ("$kind", kind), ("$key", key), ("$hash", hash), ("$plan", planId), ("$receipt", receipt), ("$expires", clock.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds()));
    private static void RequireKeyCapacity(DatabaseTransaction tx)
    { if ((long)tx.Scalar("SELECT count(*) FROM platform_storage_keys")! >= MaximumKeys) throw new OperationException("resource.capacityExceeded", "存储幂等回执达到预算"); }
    private static void FillRemaining(SavedPayload payload, List<StorageCleanupItemResult> results, string reason)
    { foreach (var candidate in payload.Candidates) if (!results.Any(x => x.Id == candidate.Id)) results.Add(new(candidate.Id, "skipped", new(0), reason)); }
    private static string[] Categories(StorageCleanupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); var selected = request.Categories?.ToArray() ?? CleanupCategories;
        if (selected.Length is < 1 or > 6 || selected.Any(x => !CleanupCategories.Contains(x, StringComparer.Ordinal)) || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new OperationException("request.invalid", "清理类别必须是缓存、日志、诊断、临时文件、备份或孤儿内容");
        return selected.Order(StringComparer.Ordinal).ToArray();
    }
    private static void Key(string? key)
    { if (key is null) throw new OperationException("idempotency.required", "存储操作需要 Idempotency-Key"); if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "存储幂等键格式无效"); }
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
    private static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, ContractJson.Options) ?? throw new StorageException("storage.assetInvalid", "存储控制记录无效");
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(value))));
}
