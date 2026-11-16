using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    public static bool IsRoomRoundOperation(string kind) => kind is "room.round.prepare" or "room.round.control";

    internal Task<OperationAdmission> AdmitRoomRoundAsync(string principal, string? roundId,
        RoomRoundRequest? prepare, RoomRoundControlRequest? control, string? key, string? ifMatch,
        Func<string> readCurrentETag, Action validateNewRequest, CancellationToken ct = default)
    {
        if (key is null) throw new OperationException("idempotency.required", "轮次命令需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z"))
            throw new OperationException("request.invalid", "轮次幂等键无效");
        if (string.IsNullOrEmpty(ifMatch)) throw new OperationException("revision.required", "轮次命令需要 If-Match");
        if (!Regex.IsMatch(ifMatch, @"\A""room-round-[0-9a-f]{32}-[0-9a-f]{32}-[0-9]{1,20}""\z"))
            throw new OperationException("request.invalid", "需要单个强轮次 ETag");
        var route = prepare is not null ? "/api/v1/rooms/rounds" : "/api/v1/rooms/rounds/{id}/commands";
        var resource = roundId ?? "current";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize<object?>(prepare is not null ? prepare : control, ContractJson.Options) + "\n" + ifMatch)));
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)",
                ("$now", now.ToUnixTimeMilliseconds()));
            var args = new (string, object?)[] { ("$p", principal), ("$route", route), ("$resource", resource), ("$key", key) };
            const string match = "principal=$p AND route=$route AND resource=$resource AND key=$key";
            if (tx.Scalar("SELECT operation_id FROM core_idempotency WHERE " + match, args) is string existing)
            {
                if ((string?)tx.Scalar("SELECT request_hash FROM core_idempotency WHERE " + match, args) != hash)
                    throw new OperationException("idempotency.conflict", "幂等键已用于其他轮次请求或修订");
                return new OperationAdmission(Read(tx, existing), false);
            }
            if (ifMatch != readCurrentETag()) throw new OperationException("revision.stale", "轮次或准备条件已变化，请读取最新状态");
            if ((long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "操作或幂等记录容量已满");
            validateNewRequest();
            var id = "op-" + Guid.NewGuid().ToString("N");
            var operation = new OperationSnapshot(id, prepare is not null ? "room.round.prepare" : "room.round.control", principal,
                [new("room-round", resource)], OperationState.Queued, "queued", new(1), null,
                new(now, null, null), null, null, new(CancellationState.None, null), EffectStatus.None);
            Save(tx, operation);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,$resource,$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", route), ("$resource", resource), ("$key", key), ("$hash", hash), ("$op", id),
                ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds()));
            return new OperationAdmission(operation, true);
        }, ct);
    }

    internal Task<OperationSnapshot?> BeginRoomRoundAsync(string id, CancellationToken ct = default) =>
        store.WriteAsync<OperationSnapshot?>(tx =>
        {
            var previous = Read(tx, id);
            if (!IsRoomRoundOperation(previous.Kind)) throw new OperationException("resource.notFound", "轮次操作不存在");
            if (SqliteStore.IsTerminal(previous.State.KnownValue)) return null;
            if (previous.State.KnownValue != OperationState.Queued) throw new OperationException("resource.busy", "轮次操作已被领取");
            var next = previous with { State = OperationState.Running, Phase = "validating", Revision = Next(previous),
                Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
            Save(tx, next); return next;
        }, ct);

    internal Task<bool> CommitRoomRoundAsync(string id, CancellationToken ct) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
        if (!IsRoomRoundOperation(previous.Kind) || previous.Phase != "validating")
            throw new OperationException("resource.busy", "轮次操作未进入提交准备阶段");
        Save(tx, previous with { Phase = "applying", Revision = Next(previous), EffectStatus = EffectStatus.Unknown });
        return true;
    }, ct);

    internal Task<OperationSnapshot> FinishRoomRoundAsync(string id, RoomRoundCommandResult result,
        ApiProblem? problem = null, bool interrupted = false, bool unconfirmed = false, CancellationToken ct = default) =>
        store.WriteAsync(tx =>
        {
            var previous = Read(tx, id);
            if (SqliteStore.IsTerminal(previous.State.KnownValue)) return previous;
            var state = interrupted ? OperationState.Interrupted : problem is null ? OperationState.Succeeded : OperationState.Failed;
            var next = previous with { State = state, Phase = state.ToString().ToLowerInvariant(), Revision = Next(previous),
                Result = JsonSerializer.SerializeToElement(result, ContractJson.Options), Problem = problem,
                Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
                EffectStatus = result.ServerConfirmed || unconfirmed || interrupted && previous.Phase == "applying" ? EffectStatus.Unknown : EffectStatus.None,
                Cancellation = previous.Cancellation.State.KnownValue != CancellationState.None || interrupted
                    ? new(CancellationState.Completed, previous.Cancellation.Reason ?? "backend-stopping") : previous.Cancellation };
            Save(tx, next); return next;
        }, ct);
}
