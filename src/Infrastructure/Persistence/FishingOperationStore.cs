using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    public const string FishingKind = "life.fishing.run";
    private const string FishingRoute = "/api/v1/life/fishing/runs";

    /// <summary>钓鱼运行历史保留的终态条数，按完成时间从新到旧计；进行中的运行不计入。</summary>
    public const int FishingHistoryRetention = 20;

    internal static int PruneFishingHistory(DatabaseTransaction tx)
    {
        const string ids = """
            SELECT id FROM core_operations WHERE terminal=1 AND json_extract(snapshot,'$.kind')='life.fishing.run'
            ORDER BY json_extract(snapshot,'$.timestamps.completedAt') DESC,id DESC LIMIT -1 OFFSET $keep
            """;
        var keep = ("$keep", (object?)FishingHistoryRetention);
        tx.Execute("DELETE FROM core_state_projection WHERE event_type='operation.changed' AND resource_id IN (" + ids + ")", keep);
        tx.Execute("DELETE FROM core_outbox WHERE event_type='operation.changed' AND resource_id IN (" + ids + ")", keep);
        tx.Execute("DELETE FROM core_idempotency WHERE operation_id IN (" + ids + ")", keep);
        return tx.Execute("DELETE FROM core_operations WHERE id IN (" + ids + ")", keep);
    }

    public string FishingOperationId(string runId) => store.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM core_operations WHERE json_extract(snapshot,'$.kind')='life.fishing.run' AND json_extract(snapshot,'$.result.id')=$id";
        command.Parameters.AddWithValue("$id", runId);
        return command.ExecuteScalar() as string ?? throw new OperationException("resource.notFound", "钓鱼运行不存在");
    });

    public FishingRun FishingRunByOperation(string id, string principal, bool owner) => ReadFishing(Get(id, principal, owner));

    /// <summary>当前运行，没有时为最近一次终态运行；没有任何运行记录时为空。</summary>
    public FishingRun? LatestFishing(string principal, bool owner) => store.Read<FishingRun?>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot FROM core_operations WHERE json_extract(snapshot,'$.kind')='life.fishing.run'
            AND ($owner=1 OR json_extract(snapshot,'$.owner')=$principal)
            ORDER BY terminal ASC, json_extract(snapshot,'$.timestamps.createdAt') DESC, id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$owner", owner ? 1 : 0);
        command.Parameters.AddWithValue("$principal", principal);
        return command.ExecuteScalar() is string json
            ? ReadFishing(JsonSerializer.Deserialize<OperationSnapshot>(json, ContractJson.Options)!) : null;
    });

    public Task<OperationAdmission> AdmitFishingAsync(string principal, FishingRunRequest request, string? key,
        Func<GameSession> resolveBinding, string epoch, bool available, CancellationToken ct = default)
    {
        if (key is null) throw new OperationException("idempotency.required", "需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z"))
            throw new OperationException("request.invalid", "幂等键须为 8–128 位 ASCII 字母、数字、点、下划线或连字符");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, ContractJson.Options))));
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
            var args = new (string, object?)[] { ("$p", principal), ("$route", FishingRoute), ("$key", key) };
            const string match = "principal=$p AND route=$route AND resource='life' AND key=$key";
            if (tx.Scalar("SELECT operation_id FROM core_idempotency WHERE " + match, args) is string previous)
            {
                if ((string?)tx.Scalar("SELECT request_hash FROM core_idempotency WHERE " + match, args) != hash)
                    throw new OperationException("idempotency.conflict", "相同幂等键已用于其他钓鱼请求");
                return new OperationAdmission(Read(tx, previous), false);
            }
            if (!available || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0 AND json_extract(snapshot,'$.kind')='life.fishing.run'")! > 0)
                throw new OperationException("resource.busy", "已有钓鱼运行正在执行或释放资源");
            if ((long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "操作或幂等记录容量已满");
            // 只复制已经准备的宿主绑定，不在数据库事务中调用游戏或执行异步工作。
            var binding = resolveBinding();
            var id = "op-" + Guid.NewGuid().ToString("N");
            var run = new FishingRun(Guid.NewGuid().ToString("N"), id, principal, binding, request.DurationSeconds, "queued", false, null,
                new(0, 0, 0, 0), 0, null, null, new(1), now, epoch);
            var operation = new OperationSnapshot(id, FishingKind, principal, [new("game-session", binding.Id), new("fishing-run", run.Id)],
                OperationState.Queued, "queued", new(1), null, new(now, null, null),
                JsonSerializer.SerializeToElement(run, ContractJson.Options), null, new(CancellationState.None, null), EffectStatus.None);
            Save(tx, operation);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,'life',$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", FishingRoute), ("$key", key), ("$hash", hash), ("$op", id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds() + 1));
            return new OperationAdmission(operation, true);
        }, ct);
    }

    public Task<OperationSnapshot?> BeginFishingAsync(string id, CancellationToken ct = default) => store.WriteAsync<OperationSnapshot?>(tx =>
    {
        var previous = Read(tx, id);
        var run = ReadFishing(previous);
        if (SqliteStore.IsTerminal(previous.State.KnownValue) || previous.State.KnownValue == OperationState.CancelRequested) return null;
        if (previous.State.KnownValue != OperationState.Queued) throw new OperationException("resource.busy", "运行已被领取");
        var value = previous with { State = OperationState.Running, Phase = "starting", Revision = Next(previous),
            Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
        SaveFishing(tx, value, run with { Phase = "starting", UpdatedAt = clock.GetUtcNow() });
        return value;
    }, ct);

    /// <summary>更新运行记录；<paramref name="terminal"/> 不为空时写入终态。取消请求已受理的运行，终态一律记为 cancelled。</summary>
    public Task<bool> UpdateFishingAsync(string id, Func<FishingRun, FishingRun> update, OperationState? terminal = null,
        ApiProblem? problem = null, EffectStatus? effect = null, CancellationToken ct = default) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
        var cancelling = previous.State.KnownValue == OperationState.CancelRequested;
        var original = ReadFishing(previous);
        var run = update(original);
        if (ReferenceEquals(run, original) && terminal is null && (effect is null || effect == previous.EffectStatus.KnownValue)) return true;
        if (cancelling && terminal is not null)
        {
            terminal = OperationState.Cancelled; problem = null;
            run = run with { Stop = run.Stop is { } stop ? stop with { Reason = previous.Cancellation.Reason ?? stop.Reason } : run.Stop };
        }
        var phase = terminal is { } end ? end.ToString().ToLowerInvariant() : cancelling ? "stopping" : run.Phase;
        var value = previous with
        {
            State = terminal ?? (cancelling ? OperationState.CancelRequested : OperationState.Running),
            Phase = phase, Revision = Next(previous), Problem = problem,
            EffectStatus = effect ?? (terminal == OperationState.Interrupted ? EffectStatus.Unknown : previous.EffectStatus),
            Cancellation = cancelling && terminal is not null ? previous.Cancellation with { State = CancellationState.Completed } : previous.Cancellation,
            Timestamps = terminal is null ? previous.Timestamps : previous.Timestamps with { CompletedAt = clock.GetUtcNow() }
        };
        SaveFishing(tx, value, run with { Phase = phase, UpdatedAt = clock.GetUtcNow() });
        if (terminal is not null) PruneFishingHistory(tx);
        return true;
    }, ct);

    internal static FishingRun ReadFishing(OperationSnapshot operation) => operation.Kind == FishingKind && operation.Result is { } result
        ? result.Deserialize<FishingRun>(ContractJson.Options) ?? throw new OperationException("resource.notFound", "钓鱼运行不存在")
        : throw new OperationException("resource.notFound", "钓鱼运行不存在");

    /// <summary>宿主重启或取消受理时把运行收口为 <paramref name="phase"/>；已有结束记录的保留，没有的写入原因，输入没有由本进程持有。</summary>
    internal static FishingRun CloseFishing(FishingRun run, string phase, string reason, DateTimeOffset at) => run with
    {
        Phase = phase, UpdatedAt = at,
        Stop = run.Stop ?? new(reason, true, FishingPoseState.Unconfirmed, at)
    };

    private static void SaveFishing(DatabaseTransaction tx, OperationSnapshot operation, FishingRun run) => Save(tx,
        operation with { Result = JsonSerializer.SerializeToElement(run with { Revision = operation.Revision }, ContractJson.Options) });
}
