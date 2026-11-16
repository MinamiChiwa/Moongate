using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    public static bool IsRoomScoreDraftOperation(string kind) => kind is "room.scoreDraft.publish" or "room.scoreDraft.map" or "room.scoreDraft.clear";

    internal Task<OperationAdmission> AdmitRoomScoreDraftAsync(string principal, string command, RoomScoreDraftRequest? publish,
        RoomScoreMappingRequest? map, string? key, string? ifMatch, Func<string> currentETag, Action validateNew, CancellationToken ct)
    {
        if (key is null) throw new OperationException("idempotency.required", "草稿命令需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "草稿幂等键无效");
        if (string.IsNullOrEmpty(ifMatch)) throw new OperationException("revision.required", "草稿命令需要 If-Match");
        if (!Regex.IsMatch(ifMatch, @"\A""room-score-[0-9a-f]{32}-[0-9a-f]{32}-[0-9]{1,20}""\z"))
            throw new OperationException("request.invalid", "需要单个强乐谱草稿 ETag");
        var route = "/api/v1/rooms/score-draft/" + command;
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { command, publish, map, ifMatch }, ContractJson.Options)));
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)",
                ("$now", now.ToUnixTimeMilliseconds()));
            var args = new (string, object?)[] { ("$p", principal), ("$route", route), ("$key", key) };
            const string match = "principal=$p AND route=$route AND resource='current' AND key=$key";
            if (tx.Scalar("SELECT operation_id FROM core_idempotency WHERE " + match, args) is string existing)
            {
                if ((string?)tx.Scalar("SELECT request_hash FROM core_idempotency WHERE " + match, args) != hash)
                    throw new OperationException("idempotency.conflict", "幂等键已用于其他草稿请求或修订");
                return new OperationAdmission(Read(tx, existing), false);
            }
            if (ifMatch != currentETag()) throw new OperationException("revision.stale", "乐谱草稿或成员映射已变化");
            if ((long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "操作或幂等记录容量已满");
            validateNew();
            var id = "op-" + Guid.NewGuid().ToString("N");
            var snapshot = new OperationSnapshot(id, "room.scoreDraft." + command, principal, [new("room-score-draft", "current")],
                OperationState.Queued, "queued", new(1), null, new(now, null, null), null, null, new(CancellationState.None, null), EffectStatus.None);
            Save(tx, snapshot);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,'current',$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", route), ("$key", key), ("$hash", hash), ("$op", id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds()));
            return new OperationAdmission(snapshot, true);
        }, ct);
    }

    internal Task<OperationSnapshot?> BeginRoomScoreDraftAsync(string id) => store.WriteAsync<OperationSnapshot?>(tx =>
    {
        var previous = Read(tx, id);
        if (!IsRoomScoreDraftOperation(previous.Kind)) throw new OperationException("resource.notFound", "草稿操作不存在");
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return null;
        if (previous.State.KnownValue != OperationState.Queued) throw new OperationException("resource.busy", "草稿操作已被领取");
        var next = previous with { State = OperationState.Running, Phase = "validating", Revision = Next(previous),
            Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
        Save(tx, next); return next;
    });

    internal Task<bool> CommitRoomScoreDraftAsync(string id, CancellationToken ct) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
        if (!IsRoomScoreDraftOperation(previous.Kind) || previous.Phase != "validating")
            throw new OperationException("resource.busy", "草稿操作尚未进入提交准备阶段");
        Save(tx, previous with { Phase = "applying", Revision = Next(previous), EffectStatus = EffectStatus.Unknown }); return true;
    }, ct);

    internal Task<OperationSnapshot> FinishRoomScoreDraftAsync(string id, RoomScoreDraftCommandResult result,
        ApiProblem? problem = null, bool interrupted = false, bool unconfirmed = false) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return previous;
        var state = interrupted ? OperationState.Interrupted : problem is null ? OperationState.Succeeded : OperationState.Failed;
        var next = previous with { State = state, Phase = state.ToString().ToLowerInvariant(), Revision = Next(previous),
            Result = JsonSerializer.SerializeToElement(result, ContractJson.Options), Problem = problem,
            Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
            EffectStatus = unconfirmed || interrupted && previous.Phase == "applying" ? EffectStatus.Unknown : EffectStatus.None,
            Cancellation = previous.Cancellation.State.KnownValue != CancellationState.None || interrupted
                ? new(CancellationState.Completed, previous.Cancellation.Reason ?? "backend-stopping") : previous.Cancellation };
        Save(tx, next); return next;
    });
}
