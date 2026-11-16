using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    public static string RoomServerETag(RoomServerSnapshot snapshot) => $"\"room-server-{snapshot.BackendEpoch}-{snapshot.Revision}\"";
    public static bool IsRoomServerOperation(string kind) => kind is "room.server.start" or "room.server.stop";

    internal Task<OperationAdmission> AdmitRoomServerAsync(string principal, bool start, RoomServerStartRequest? request,
        string? key, string? ifMatch, string currentETag, Action validateNewRequest, CancellationToken ct = default)
    {
        if (key is null) throw new OperationException("idempotency.required", "建房服务启停需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z"))
            throw new OperationException("request.invalid", "建房服务幂等键无效");
        if (string.IsNullOrEmpty(ifMatch)) throw new OperationException("revision.required", "建房服务启停需要 If-Match");
        var etag = Regex.Match(ifMatch, @"\A""room-server-([0-9a-f]{32})-[0-9]{1,20}""\z");
        if (!etag.Success)
            throw new OperationException("request.invalid", "需要单个强建房服务 ETag");
        if (start && request?.Mode.KnownValue is null)
            throw new OperationException("request.invalid", "建房模式须为 loopback、lan 或 public");
        var route = start ? "/api/v1/rooms/server/starts" : "/api/v1/rooms/server/stops";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(request, ContractJson.Options) + "\n" + ifMatch)));
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)",
                ("$now", now.ToUnixTimeMilliseconds()));
            var args = new (string, object?)[] { ("$p", principal), ("$route", route), ("$key", key) };
            const string match = "principal=$p AND route=$route AND resource='room-server' AND key=$key";
            if (tx.Scalar("SELECT operation_id FROM core_idempotency WHERE " + match, args) is string existing)
            {
                if ((string?)tx.Scalar("SELECT request_hash FROM core_idempotency WHERE " + match, args) != hash)
                    throw new OperationException("idempotency.conflict", "幂等键已用于其他建房请求或修订");
                return new OperationAdmission(Read(tx, existing), false);
            }
            if (!currentETag.StartsWith("\"room-server-" + etag.Groups[1].Value + "-", StringComparison.Ordinal) &&
                tx.Scalar("SELECT i.operation_id FROM core_idempotency i JOIN core_operations o ON o.id=i.operation_id " +
                    "WHERE i.route=$route AND i.resource='room-server' AND i.key=$key AND i.request_hash=$hash AND o.terminal=1",
                    ("$route", route), ("$key", key), ("$hash", hash)) is string historical)
                return new OperationAdmission(Read(tx, historical), false);
            if (ifMatch != currentETag) throw new OperationException("revision.stale", "建房服务修订已改变，请读取最新状态");
            if ((long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "操作或幂等记录容量已满");
            validateNewRequest();
            var id = "op-" + Guid.NewGuid().ToString("N");
            var operation = new OperationSnapshot(id, start ? "room.server.start" : "room.server.stop", principal,
                [new("room-server", "local")], OperationState.Queued, "queued", new(1), null,
                new(now, null, null), null, null, new(CancellationState.None, null), EffectStatus.None);
            Save(tx, operation);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,'room-server',$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", route), ("$key", key), ("$hash", hash), ("$op", id),
                ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds()));
            return new OperationAdmission(operation, true);
        }, ct);
    }

    internal Task<OperationSnapshot?> BeginRoomServerAsync(string id, CancellationToken ct = default) =>
        store.WriteAsync<OperationSnapshot?>(tx =>
        {
            var previous = Read(tx, id);
            if (!IsRoomServerOperation(previous.Kind)) throw new OperationException("resource.notFound", "建房服务操作不存在");
            if (SqliteStore.IsTerminal(previous.State.KnownValue)) return null;
            if (previous.State.KnownValue == OperationState.CancelRequested) return previous;
            if (previous.State.KnownValue != OperationState.Queued)
                throw new OperationException("resource.busy", "建房服务操作已被领取");
            var next = previous with { State = OperationState.Running,
                Phase = previous.Kind == "room.server.start" ? "starting" : "stopping", Revision = Next(previous),
                Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
            Save(tx, next); return next;
        }, ct);

    internal Task<OperationSnapshot> FinishRoomServerAsync(string id, RoomServerSnapshot result, ApiProblem? problem = null,
        bool cancelled = false, bool interrupted = false, CancellationToken ct = default) => store.WriteAsync(tx =>
        {
            var previous = Read(tx, id);
            if (SqliteStore.IsTerminal(previous.State.KnownValue)) return previous;
            var state = interrupted ? OperationState.Interrupted : cancelled ? OperationState.Cancelled :
                problem is null ? OperationState.Succeeded : OperationState.Failed;
            var next = previous with { State = state, Phase = state.ToString().ToLowerInvariant(), Revision = Next(previous),
                Result = JsonSerializer.SerializeToElement(result, ContractJson.Options), Problem = problem,
                Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() }, EffectStatus = EffectStatus.None,
                Cancellation = cancelled || interrupted || previous.Cancellation.State.KnownValue != CancellationState.None
                    ? new(CancellationState.Completed, previous.Cancellation.Reason ?? (interrupted ? "backend-stopping" : "cancelled"))
                    : previous.Cancellation };
            Save(tx, next); return next;
        }, ct);
}
