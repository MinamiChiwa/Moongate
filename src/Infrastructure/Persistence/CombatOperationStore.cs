using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    private const string CombatRoute = "/api/v1/combat/observations";
    public OperationSnapshot? ExistingCombat(string principal, CombatObservationRequest request, string? key)
    {
        if (key is null) throw new OperationException("idempotency.required", "启动战斗观察需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "幂等键格式无效");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options)));
        var json = store.ReadScalar("SELECT json_object('hash',request_hash,'id',operation_id) FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$game AND key=$key AND (expires_at>$now OR operation_id IN (SELECT id FROM core_operations WHERE terminal=0))",
            ("$p", principal), ("$route", CombatRoute), ("$game", request.GameSessionId), ("$key", key), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds())) as string;
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("hash").GetString() != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他战斗观察请求");
        return Get(document.RootElement.GetProperty("id").GetString()!, principal, false);
    }
    public Task<OperationSnapshot> FailCombatAsync(string id, string code, string reason, CancellationToken ct = default) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (previous.Kind != "combat.observe") throw new OperationException("request.invalid", "操作不是战斗观察");
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return previous;
        var cancelled = previous.State.KnownValue == OperationState.CancelRequested;
        var next = previous with { State = cancelled ? OperationState.Cancelled : OperationState.Failed, Phase = cancelled ? "stopped" : "failed",
            Revision = Next(previous), Problem = cancelled ? null : ApiErrors.Create(code, reason, id),
            Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
            Cancellation = cancelled ? new(CancellationState.Completed, previous.Cancellation.Reason) : previous.Cancellation };
        Save(tx, next); return next;
    }, ct);
    /// <summary>
    /// 战斗观察已登记兴趣、等待本机身份：操作进入 running / identity-pending，Problem 给出 <c>combat.identityPending</c>，
    /// 身份出现后的首个检查点改为 observing 并清除该原因。已终止或已请求取消的操作不变。
    /// </summary>
    public Task CombatIdentityPendingAsync(string id, CancellationToken ct = default) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (previous.Kind != "combat.observe" || SqliteStore.IsTerminal(previous.State.KnownValue) ||
            previous.State.KnownValue == OperationState.CancelRequested || previous.Result is not null) return previous;
        var next = previous with { State = OperationState.Running, Phase = "identity-pending", Revision = Next(previous),
            Problem = ApiErrors.Create("combat.identityPending", "等待识别本机角色：请切换一次地图或重新登录角色，识别后自动开始。", id),
            Timestamps = previous.Timestamps with { StartedAt = previous.Timestamps.StartedAt ?? clock.GetUtcNow() } };
        Save(tx, next); return next;
    }, ct);
    public Task<OperationAdmission> AdmitCombatAsync(string principal, CombatObservationRequest request, string? key,
        bool queueAvailable = true, CancellationToken ct = default)
    {
        if (key is null) throw new OperationException("idempotency.required", "启动战斗观察需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "幂等键格式无效");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options)));
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
            var previousId = tx.Scalar("SELECT operation_id FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$game AND key=$key",
                ("$p", principal), ("$route", CombatRoute), ("$game", request.GameSessionId), ("$key", key)) as string;
            if (previousId is not null)
            {
                var previousHash = tx.Scalar("SELECT request_hash FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$game AND key=$key",
                    ("$p", principal), ("$route", CombatRoute), ("$game", request.GameSessionId), ("$key", key)) as string;
                if (previousHash != hash) throw new OperationException("idempotency.conflict", "幂等键已用于其他战斗观察请求");
                return new OperationAdmission(Read(tx, previousId), false);
            }
            if ((long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0 AND json_extract(snapshot,'$.kind')='combat.observe'")! > 0)
                throw new OperationException("resource.busy", "当前已有战斗观察，可由多个前端共同读取");
            if (!queueAvailable || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "战斗观察或幂等记录已达到上限");
            var id = "op-" + Guid.NewGuid().ToString("N");
            var value = new OperationSnapshot(id, "combat.observe", principal, [new("game-session", request.GameSessionId), new("combat-observation", id)],
                OperationState.Queued, "queued", new(1), null, new(now, null, null), null, null, new(CancellationState.None, null), EffectStatus.None);
            Save(tx, value);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,$game,$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", CombatRoute), ("$game", request.GameSessionId), ("$key", key), ("$hash", hash),
                ("$op", id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds() + 1));
            return new OperationAdmission(value, true);
        }, ct);
    }

    /// <summary>模块检查点与操作状态在同一事务提交；取消先提交时不再接受普通观测检查点。</summary>
    public Task<bool> CommitCombatAsync(string id, CombatObservationStatus status, Action<DatabaseTransaction> persist,
        OperationState? terminal = null, ApiProblem? problem = null, CancellationToken ct = default) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (previous.Kind != "combat.observe" || previous.Owner != status.Owner || status.Id != id ||
            previous.ResourceRefs[0].Id != status.GameSessionId) throw new OperationException("request.invalid", "战斗观察身份不匹配");
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
        if (previous.State.KnownValue == OperationState.CancelRequested && terminal is not (OperationState.Cancelled or OperationState.Interrupted)) return false;
        if (previous.Result is { } result && result.Deserialize<CombatObservationStatus>(ContractJson.Options) is { } saved && saved.Revision.Value >= status.Revision.Value)
        {
            if (saved == status || JsonElement.DeepEquals(result, JsonSerializer.SerializeToElement(status, ContractJson.Options))) return true;
            throw new OperationException("revision.stale", "战斗检查点修订已变化");
        }
        persist(tx);
        var now = clock.GetUtcNow();
        var snapshot = previous with
        {
            State = terminal ?? OperationState.Running, Phase = terminal is null ? "observing" : status.State,
            Revision = Next(previous), Result = JsonSerializer.SerializeToElement(status, ContractJson.Options), Problem = problem,
            Timestamps = new(previous.Timestamps.CreatedAt, previous.Timestamps.StartedAt ?? now, terminal is null ? null : now),
            Cancellation = previous.Cancellation.State.KnownValue == CancellationState.Requested
                ? new(CancellationState.Completed, previous.Cancellation.Reason) : previous.Cancellation,
            EffectStatus = terminal == OperationState.Interrupted ? EffectStatus.Unknown : EffectStatus.None
        };
        Save(tx, snapshot); return true;
    }, ct);
}
