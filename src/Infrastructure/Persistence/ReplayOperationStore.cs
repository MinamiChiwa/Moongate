using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    private const string ReplayRoute = "/api/v1/replay/runs";

    public Task<OperationAdmission> AdmitReplayAsync(string principal, ReplayRequest request, string? key, CancellationToken ct = default, bool queueAvailable = true)
    {
        if (key is null) throw new OperationException("idempotency.required", "需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z"))
            throw new OperationException("request.invalid", "幂等键须为 8–128 位 ASCII 字母、数字、点、下划线或连字符");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, ContractJson.Options))));
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
            var parameters = new (string, object?)[] { ("$p", principal), ("$route", ReplayRoute), ("$key", key) };
            var previousId = tx.Scalar("SELECT operation_id FROM core_idempotency WHERE principal=$p AND route=$route AND resource='replay' AND key=$key", parameters) as string;
            if (previousId is not null)
            {
                var savedHash = tx.Scalar("SELECT request_hash FROM core_idempotency WHERE principal=$p AND route=$route AND resource='replay' AND key=$key", parameters) as string;
                if (savedHash != hash) throw new OperationException("idempotency.conflict", "相同幂等键已用于其他回放请求");
                return new OperationAdmission(Read(tx, previousId), false);
            }
            if ((long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0 AND json_extract(snapshot,'$.kind')='replay.run'")! != 0)
                throw new OperationException("resource.busy", "已有活动回放；请等待或取消");
            if (!queueAvailable || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "操作或幂等记录容量已满");
            var id = "op-" + Guid.NewGuid().ToString("N");
            var value = new OperationSnapshot(id, "replay.run", principal, [new("replay-observation", id)],
                OperationState.Queued, "queued", new(1), 0, new(now, null, null), null, null,
                new(CancellationState.None, null), EffectStatus.None);
            Save(tx, value);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,'replay',$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", ReplayRoute), ("$key", key), ("$hash", hash), ("$op", id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds() + 1));
            return new OperationAdmission(value, true);
        }, ct);
    }

    /// <summary>取消与帧落盘串行裁决；终态之后不再提交新的模拟观测。</summary>
    public Task<bool> CommitReplayFrameAsync(string id, Observation<ReplayState> observation, int frameCount, bool completes,
        CancellationToken ct = default) => store.WriteAsync(tx =>
    {
        var previous = ReadReplay(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
        if (frameCount is < 1 or > 128 || completes && observation.Sequence.Value != (ulong)frameCount)
            throw new OperationException("request.invalid", "回放帧数量或完成标志无效");
        var sequence = previous.Result is { } result ? result.GetProperty("sequence").Deserialize<UInt64String>(ContractJson.Options).Value : 0;
        if (observation.Value.OperationId != id || observation.Sequence.Value != sequence + 1 || observation.Sequence.Value > (ulong)frameCount)
            throw new OperationException("request.invalid", "回放帧顺序或操作身份不匹配");
        var payload = JsonSerializer.SerializeToElement(observation, ContractJson.Options);
        SqliteStore.AppendEvent(tx, new("replay-" + id + "-" + observation.Sequence, "replay-observation", id,
            observation.Sequence, "replay.observation-changed", payload));
        var value = previous with
        {
            State = completes ? OperationState.Succeeded : OperationState.Running,
            Phase = completes ? "succeeded" : "replaying", Revision = Next(previous),
            Progress = (double)observation.Sequence.Value / frameCount, Result = payload,
            Timestamps = new(previous.Timestamps.CreatedAt, previous.Timestamps.StartedAt ?? clock.GetUtcNow(), completes ? clock.GetUtcNow() : null)
        };
        Save(tx, value);
        return true;
    }, ct);

    public Task<OperationSnapshot> FailReplayAsync(string id) => store.WriteAsync(tx =>
    {
        var previous = ReadReplay(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return previous;
        var value = previous with { State = OperationState.Failed, Phase = "failed", Revision = Next(previous),
            Problem = ApiErrors.Create("provider.unavailable", "模拟 Provider 失联", id),
            Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() } };
        Save(tx, value);
        return value;
    });

    private static OperationSnapshot ReadReplay(DatabaseTransaction tx, string id)
    {
        var value = Read(tx, id);
        if (value.Kind != "replay.run") throw new OperationException("request.invalid", "操作不是回放任务");
        return value;
    }
}
