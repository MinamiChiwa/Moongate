using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    /// <summary>寻色历史保留的终态运行条数，按完成时间从新到旧计；与原工具 Color Studio 相同在写入新结果时裁剪。</summary>
    public const int ColorHistoryRetention = 30;

    /// <summary>删除最新 <see cref="ColorHistoryRetention"/> 条之外的终态寻色运行，连同其幂等记录与操作事件投影。进行中的运行不计入也不删除。</summary>
    internal static int PruneColorHistory(DatabaseTransaction tx)
    {
        const string ids = """
            SELECT id FROM core_operations WHERE terminal=1 AND json_extract(snapshot,'$.kind')='color.search'
            ORDER BY json_extract(snapshot,'$.timestamps.completedAt') DESC,id DESC LIMIT -1 OFFSET $keep
            """;
        var keep = ("$keep", (object?)ColorHistoryRetention);
        tx.Execute("DELETE FROM core_state_projection WHERE event_type='operation.changed' AND resource_id IN (" + ids + ")", keep);
        tx.Execute("DELETE FROM core_outbox WHERE event_type='operation.changed' AND resource_id IN (" + ids + ")", keep);
        tx.Execute("DELETE FROM core_idempotency WHERE operation_id IN (" + ids + ")", keep);
        return tx.Execute("DELETE FROM core_operations WHERE id IN (" + ids + ")", keep);
    }

    /// <summary>仍在历史中的寻色运行编号（不分归属）。</summary>
    public IReadOnlySet<string> RetainedColorRunIds() => store.Read<IReadOnlySet<string>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json_extract(snapshot,'$.result.id') FROM core_operations WHERE json_extract(snapshot,'$.kind')='color.search'";
        using var rows = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (rows.Read()) if (!rows.IsDBNull(0)) result.Add(rows.GetString(0));
        return result;
    });

    public ApiList<ColorHistoryItem> ColorHistory(string principal, bool owner, ColorHistoryQuery query)
    {
        if (query.PageSize is < 1 or > 50 || query.BeforeOperationId is { Length: > 128 })
            throw new OperationException("request.invalid", "染色历史每页为 1–50 条");
        var boundary = query.BeforeOperationId is null ? null : Get(query.BeforeOperationId, principal, owner);
        if (boundary is not null && (boundary.Kind != "color.search" || !SqliteStore.IsTerminal(boundary.State.KnownValue)))
            throw new OperationException("resource.notFound", "历史分页起点不存在");
        var snapshots = store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT snapshot FROM core_operations WHERE terminal=1 AND json_extract(snapshot,'$.kind')='color.search'
                AND ($owner=1 OR json_extract(snapshot,'$.owner')=$principal)
                AND ($boundary IS NULL OR json_extract(snapshot,'$.timestamps.completedAt')<$boundary
                    OR (json_extract(snapshot,'$.timestamps.completedAt')=$boundary AND id<$id))
                ORDER BY json_extract(snapshot,'$.timestamps.completedAt') DESC,id DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$owner", owner ? 1 : 0);
            command.Parameters.AddWithValue("$principal", principal);
            command.Parameters.AddWithValue("$boundary", boundary is null ? DBNull.Value : store.ReadScalar("SELECT json_extract(snapshot,'$.timestamps.completedAt') FROM core_operations WHERE id=$id", ("$id", boundary.Id))!);
            command.Parameters.AddWithValue("$id", query.BeforeOperationId ?? "");
            command.Parameters.AddWithValue("$limit", query.PageSize + 1);
            using var rows = command.ExecuteReader();
            var result = new List<OperationSnapshot>();
            while (rows.Read()) result.Add(JsonSerializer.Deserialize<OperationSnapshot>(rows.GetString(0), ContractJson.Options)!);
            return result;
        });
        return new(snapshots.Take(query.PageSize).Select(operation =>
        {
            var run = ReadColor(operation);
            return new ColorHistoryItem(run.Id, operation.Id, operation.Owner, operation.State,
                operation.Timestamps.CompletedAt!.Value, run.RulesSnapshot, run.Result, run.BestMeasured, run.LastMeasured);
        }).ToArray(), snapshots.Count > query.PageSize ? snapshots[query.PageSize - 1].Id : null,
            "history:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshots, ContractJson.Options))));
    }

    public string ColorOperationId(string runId) => store.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM core_operations WHERE json_extract(snapshot,'$.kind')='color.search' AND json_extract(snapshot,'$.result.id')=$id";
        command.Parameters.AddWithValue("$id", runId);
        return command.ExecuteScalar() as string ?? throw new OperationException("resource.notFound", "染色运行不存在");
    });

    public ColorRun ColorRun(string id, string principal, bool owner)
    {
        var operation = Get(id, principal, owner);
        return ReadColor(operation);
    }

    public Task<OperationAdmission> AdmitColorAsync(string principal, ColorRunStartRequest request, string? key,
        Func<GameSession> resolveBinding, string epoch, double deadlineMonotonicMs, bool available,
        CancellationToken ct = default)
    {
        if (key is null) throw new OperationException("idempotency.required", "需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z"))
            throw new OperationException("request.invalid", "幂等键须为 8–128 位 ASCII 字母、数字、点、下划线或连字符");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, ContractJson.Options))));
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
            var args = new (string, object?)[] { ("$p", principal), ("$key", key) };
            const string match = "principal=$p AND route='/api/v1/color/runs' AND resource='color' AND key=$key";
            var previous = tx.Scalar("SELECT operation_id FROM core_idempotency WHERE " + match, args) as string;
            if (previous is not null)
            {
                if ((string?)tx.Scalar("SELECT request_hash FROM core_idempotency WHERE " + match, args) != hash)
                    throw new OperationException("idempotency.conflict", "相同幂等键已用于其他染色请求");
                return new OperationAdmission(Read(tx, previous), false);
            }
            if (!available || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0 AND json_extract(snapshot,'$.kind')='color.search'")! > 0)
                throw new OperationException("resource.busy", "已有染色运行正在执行或释放资源");
            if ((long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "操作或幂等记录容量已满");
            // 仅复制已经准备的宿主绑定，不在数据库事务中调用游戏或执行异步工作。
            var binding = resolveBinding();
            var id = "op-" + Guid.NewGuid().ToString("N");
            var run = new ColorRun(Guid.NewGuid().ToString("N"), id, binding, request.Profile,
                "queued", deadlineMonotonicMs, null, null, null, null, null)
                { Owner = principal, HostClockEpoch = epoch, Revision = new(1) };
            var operation = new OperationSnapshot(id, "color.search", principal, [new("color-run", run.Id)],
                OperationState.Queued, "queued", new(1), null, new(now, null, null),
                JsonSerializer.SerializeToElement(run, ContractJson.Options), null, new(CancellationState.None, null), EffectStatus.None);
            Save(tx, operation);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,'/api/v1/color/runs','color',$key,$hash,$op,$expiry)",
                ("$p", principal), ("$key", key), ("$hash", hash), ("$op", id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds() + 1));
            return new OperationAdmission(operation, true);
        }, ct);
    }

    public Task<OperationSnapshot?> BeginColorAsync(string id, CancellationToken ct = default) => store.WriteAsync<OperationSnapshot?>(tx =>
    {
        var previous = Read(tx, id);
        _ = ReadColor(previous);
        if (SqliteStore.IsTerminal(previous.State.KnownValue) || previous.State.KnownValue == OperationState.CancelRequested) return null;
        if (previous.State.KnownValue != OperationState.Queued) throw new OperationException("resource.busy", "运行已被领取");
        var value = previous with { State = OperationState.Running, Phase = "starting", Revision = Next(previous),
            Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
        SaveColor(tx, value, ReadColor(previous) with { Phase = "starting" });
        return value;
    }, ct);

    public Task<bool> UpdateColorAsync(string id, Func<ColorRun, ColorRun> update, OperationState? terminal = null,
        ApiProblem? problem = null, CancellationToken ct = default, EffectStatus? effect = null) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return false;
        var cancelling = previous.State.KnownValue == OperationState.CancelRequested;
        var original = ReadColor(previous);
        var run = update(original);
        if (ReferenceEquals(run, original) && terminal is null && (effect is null || effect == previous.EffectStatus.KnownValue)) return true;
        if (cancelling)
        {
            if (terminal is not null) terminal = OperationState.Cancelled;
            run = CloseColor(run, terminal is null ? "stopping" : "cancelled", previous.Cancellation.Reason);
        }
        else if (terminal is not null) run = CloseColor(run, terminal.Value.ToString().ToLowerInvariant(), run.StopReason,
            clearCurrent: terminal != OperationState.Succeeded);
        var value = previous with { State = terminal ?? (cancelling ? OperationState.CancelRequested : run.Interaction is { SelectionEnabled: true } or { RetryEnabled: true }
                ? OperationState.WaitingUser : OperationState.Running),
            Phase = run.Phase, Revision = Next(previous), Problem = problem,
            EffectStatus = effect ?? (terminal == OperationState.Interrupted ? EffectStatus.Unknown : previous.EffectStatus),
            Cancellation = cancelling && terminal is not null ? previous.Cancellation with { State = CancellationState.Completed } : previous.Cancellation,
            Timestamps = terminal is null ? previous.Timestamps : previous.Timestamps with { CompletedAt = clock.GetUtcNow() } };
        SaveColor(tx, value, run);
        if (terminal is not null) PruneColorHistory(tx);
        return true;
    }, ct);

    internal static ColorRun ReadColor(OperationSnapshot operation) => operation.Kind == "color.search" && operation.Result is { } result
        ? result.Deserialize<ColorRun>(ContractJson.Options) ?? throw new OperationException("resource.notFound", "染色运行不存在")
        : throw new OperationException("resource.notFound", "染色运行不存在");

    internal static ColorRun CloseColor(ColorRun run, string phase, string? reason, bool clearCurrent = true) => run with
    {
        Phase = phase, StopReason = reason, CandidateBatchId = null, Candidates = null,
        Interaction = run.Interaction is { } interaction ? interaction with { BatchId = null, SelectionEnabled = false, RetryEnabled = false } : null,
        CurrentVerified = clearCurrent ? null : run.CurrentVerified,
        Result = run.Result is { } result ? result with { CurrentVerified = clearCurrent ? null : result.CurrentVerified,
            Outcome = clearCurrent && result.Outcome is "matched" or "compromise" ? "unverified" : result.Outcome,
            StopReason = reason ?? result.StopReason } : null
    };

    private static void SaveColor(DatabaseTransaction tx, OperationSnapshot operation, ColorRun run) => Save(tx,
        operation with { Result = JsonSerializer.SerializeToElement(run with { Revision = operation.Revision }, ContractJson.Options) });
}
