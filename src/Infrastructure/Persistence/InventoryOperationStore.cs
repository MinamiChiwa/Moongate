using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    public OperationSnapshot? InventoryRetry(string principal, string route, object request, string? key, string? condition = null)
    {
        InventoryKey(key);
        var args = new (string, object?)[] { ("$p", principal), ("$route", route), ("$key", key),
            ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds()) };
        var id = store.ReadScalar("SELECT operation_id FROM core_idempotency WHERE principal=$p AND route=$route AND resource='inventory' AND key=$key AND expires_at>$now", args) as string;
        if (id is null) return null;
        if (store.ReadScalar("SELECT request_hash FROM core_idempotency WHERE principal=$p AND route=$route AND resource='inventory' AND key=$key", args) as string != InventoryHash(request, condition))
            throw new OperationException("idempotency.conflict", "幂等键已用于其他库存请求或修订");
        return Get(id, principal, false);
    }

    public Task<OperationAdmission> AdmitInventoryAsync(string principal, string route, string kind, object request,
        string? key, bool available, string? condition = null, CancellationToken ct = default)
    {
        InventoryKey(key);
        if (!kind.StartsWith("inventory.", StringComparison.Ordinal) || !route.StartsWith("/api/v1/inventory/", StringComparison.Ordinal))
            throw new ArgumentException("库存操作类型或路由无效");
        var hash = InventoryHash(request, condition);
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
            var args = new (string, object?)[] { ("$p", principal), ("$route", route), ("$key", key) };
            const string match = "principal=$p AND route=$route AND resource='inventory' AND key=$key";
            if (tx.Scalar("SELECT operation_id FROM core_idempotency WHERE " + match, args) is string existing)
            {
                if (tx.Scalar("SELECT request_hash FROM core_idempotency WHERE " + match, args) as string != hash)
                    throw new OperationException("idempotency.conflict", "幂等键已用于其他库存请求或修订");
                return new OperationAdmission(Read(tx, existing), false);
            }
            if (!available || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "库存操作队列或幂等记录已满");
            var id = "op-" + Guid.NewGuid().ToString("N");
            var snapshot = new OperationSnapshot(id, kind, principal, [new("module", "inventory")], OperationState.Queued,
                "queued", new(1), null, new(now, null, null), null, null, new(CancellationState.None, null), EffectStatus.None);
            Save(tx, snapshot);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,'inventory',$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", route), ("$key", key), ("$hash", hash), ("$op", id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds()));
            return new OperationAdmission(snapshot, true);
        }, ct);
    }

    public Task<OperationSnapshot?> BeginInventoryAsync(string id, CancellationToken ct = default) => store.WriteAsync<OperationSnapshot?>(tx =>
    {
        var previous = Read(tx, id);
        if (!previous.Kind.StartsWith("inventory.", StringComparison.Ordinal)) throw new OperationException("resource.notFound", "库存操作不存在");
        if (SqliteStore.IsTerminal(previous.State.KnownValue) || previous.State.KnownValue == OperationState.CancelRequested) return null;
        if (previous.State.KnownValue != OperationState.Queued) throw new OperationException("resource.busy", "库存操作已被领取");
        var next = previous with { State = OperationState.Running, Phase = "preparing", Revision = Next(previous),
            Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
        Save(tx, next); return next;
    }, ct);

    public Task<bool> InventoryApplyingAsync(string id, CancellationToken ct = default) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue) || previous.State.KnownValue == OperationState.CancelRequested) return false;
        var next = previous with { Phase = "applying", Revision = Next(previous) };
        Save(tx, next); return true;
    }, ct);

    public Task UpdateInventoryObservationAsync(string id, InventoryObservationStatus status, CancellationToken ct = default) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue) || previous.State.KnownValue == OperationState.CancelRequested) return false;
        var next = previous with { Phase = "observing", Revision = Next(previous), Result = JsonSerializer.SerializeToElement(status, ContractJson.Options) };
        Save(tx, next); return true;
    }, ct);

    public Task<bool> FinishInventoryAsync(string id, object? result = null, ApiProblem? problem = null,
        bool interrupted = false, bool cancelled = false, CancellationToken ct = default) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
        var final = interrupted ? OperationState.Interrupted : cancelled ? OperationState.Cancelled : problem is null ? OperationState.Succeeded : OperationState.Failed;
        var next = previous with { State = final, Phase = final.ToString().ToLowerInvariant(), Revision = Next(previous), Problem = problem,
            Result = result is null ? previous.Result : JsonSerializer.SerializeToElement(result, ContractJson.Options),
            EffectStatus = EffectStatus.None, Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
            Cancellation = cancelled ? new(CancellationState.Completed, previous.Cancellation.Reason) : previous.Cancellation };
        Save(tx, next); return true;
    }, ct);
    private static void InventoryKey(string? key)
    {
        if (key is null) throw new OperationException("idempotency.required", "库存请求需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "库存幂等键无效");
    }
    private static string InventoryHash(object request, string? condition) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { request, condition }, ContractJson.Options)));
}
