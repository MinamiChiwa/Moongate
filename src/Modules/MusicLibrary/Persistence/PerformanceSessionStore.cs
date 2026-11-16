using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;

namespace MabiTool.Modules.MusicLibrary.Persistence;

/// <summary>保存演奏会话快照；事务只做 JSON 和版本比较，不执行游戏动作。</summary>
public sealed class PerformanceSessionStore(SqliteStore database, TimeProvider? timeProvider = null)
{
    private static readonly Regex ReceiptKey = new(@"\Aperformance-create:[A-Za-z0-9._-]{8,128}\z", RegexOptions.CultureInvariant);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public static DatabaseMigration Migration { get; } = new("performance", 1, """
        CREATE TABLE performance_sessions (
            id TEXT PRIMARY KEY, owner TEXT NOT NULL, generation TEXT NOT NULL,
            snapshot TEXT NOT NULL, updated_at INTEGER NOT NULL
        ) STRICT;
        """);

    public PerformanceQueueState? Get(string owner, string id)
    {
        var json = database.ReadScalar("SELECT snapshot FROM performance_sessions WHERE id=$id AND owner=$owner", ("$id", id), ("$owner", owner)) as string;
        return json is null ? null : JsonSerializer.Deserialize<PerformanceQueueState>(json, ContractJson.Options);
    }

    public IReadOnlyList<PerformanceQueueState> List(string owner) =>
        ReadMany("SELECT snapshot FROM performance_sessions WHERE owner=$owner ORDER BY id", ("$owner", owner));

    public Task CreateAsync(PerformanceQueueState state, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(state, ContractJson.Options);
        return database.WriteAsync(tx =>
        {
            InsertSession(tx, state, json, clock.GetUtcNow().ToUnixTimeMilliseconds());
            return 0;
        }, ct);
    }

    public Task<PerformanceQueueState> CreateIdempotentAsync(PerformanceQueueState state, string receiptKey,
        string requestHash, CancellationToken ct = default)
    {
        if (!ReceiptKey.IsMatch(receiptKey) || !Regex.IsMatch(requestHash, @"\A[A-F0-9]{64}\z", RegexOptions.CultureInvariant))
            throw new OperationException("request.invalid", "演奏会话幂等记录无效");
        var snapshot = JsonSerializer.Serialize(state, ContractJson.Options);
        return database.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var existing = tx.Scalar("SELECT json_object('hash',hash,'result',json(result)) FROM music_library_receipts WHERE principal=$owner AND key=$key AND expires_at>$now",
                ("$owner", state.Owner), ("$key", receiptKey), ("$now", now)) as string;
            if (existing is not null) return DecodeReceipt(existing, requestHash);

            tx.Execute("DELETE FROM music_library_receipts WHERE expires_at<=$now", ("$now", now));
            if ((long)tx.Scalar("SELECT count(*) FROM music_library_receipts")! >= MusicLibraryStore.MaximumReceipts)
                throw new OperationException("resource.capacityExceeded", "曲库幂等记录达到上限");

            InsertSession(tx, state, snapshot, now);
            tx.Execute("INSERT INTO music_library_receipts(principal,key,hash,result,expires_at) VALUES($owner,$key,$hash,$result,$expiry)",
                ("$owner", state.Owner), ("$key", receiptKey), ("$hash", requestHash), ("$result", snapshot),
                ("$expiry", checked(now + 86_400_000)));
            return state;
        }, ct);
    }

    public Task<PerformanceQueueState> UpdateAsync(string owner, string id, UInt64String expectedGeneration,
        PerformanceQueueState next, CancellationToken ct = default)
    {
        if (next.Owner != owner || next.Id != id) throw new OperationException("request.invalid", "演奏会话归属不一致");
        var json = JsonSerializer.Serialize(next, ContractJson.Options);
        return database.WriteAsync(tx =>
        {
            var current = tx.Scalar("SELECT json_object('generation',generation,'snapshot',snapshot) FROM performance_sessions WHERE id=$id AND owner=$owner",
                ("$id", id), ("$owner", owner)) as string ?? throw new OperationException("resource.notFound", "演奏会话不存在");
            using var currentDocument = JsonDocument.Parse(current);
            var currentGeneration = UInt64String.Parse(currentDocument.RootElement.GetProperty("generation").GetString()!);
            if (currentGeneration != expectedGeneration) throw new OperationException("revision.stale", "演奏会话已被其他客户端更新");
            var currentSnapshot = currentDocument.RootElement.GetProperty("snapshot").GetString()!;
            if (next.Generation.Value == expectedGeneration.Value)
            {
                if (StringComparer.Ordinal.Equals(currentSnapshot, json)) return next;
                throw new OperationException("performance.invalidState", "演奏状态改变时必须递增控制代次");
            }
            if (next.Generation.Value < expectedGeneration.Value)
                throw new OperationException("performance.invalidState", "演奏控制代次不能倒退");
            tx.Execute("UPDATE performance_sessions SET generation=$generation,snapshot=$snapshot,updated_at=$updated WHERE id=$id AND owner=$owner",
                ("$generation", next.Generation.ToString()), ("$snapshot", json), ("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ("$id", id), ("$owner", owner));
            AppendEvent(tx, next);
            return next;
        }, ct);
    }

    public Task DeleteAsync(string owner, string id, CancellationToken ct = default) => database.WriteAsync(tx =>
    {
        if (tx.Execute("DELETE FROM performance_sessions WHERE id=$id AND owner=$owner", ("$id", id), ("$owner", owner)) == 0)
            throw new OperationException("resource.notFound", "演奏会话不存在");
        return 0;
    }, ct);

    private IReadOnlyList<PerformanceQueueState> ReadMany(string sql, params (string Name, object? Value)[] parameters)
    {
        var query = sql.Replace("SELECT snapshot", "SELECT json_group_array(snapshot)", StringComparison.Ordinal);
        var json = database.ReadScalar(query, parameters) as string ?? "[]";
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(x => JsonSerializer.Deserialize<PerformanceQueueState>(x.GetString()!, ContractJson.Options)!)
            .ToArray();
    }

    private static void AppendEvent(DatabaseTransaction tx, PerformanceQueueState state)
    {
        var snapshot = new PerformanceSnapshot(state.Id, state.Owner, state.Mode, state.GameSessionId,
            state.Items.Select(x => x.Item).ToArray(), state.Phase.ToString(),
            state.StopReason?.ToString() ?? "none", state.Generation, null);
        SqliteStore.AppendEvent(tx, new("performance-" + state.Id + "-" + state.Generation,
            "performance", state.Id, state.Generation, "performance.state.changed",
            JsonSerializer.SerializeToElement(snapshot, ContractJson.Options)));
    }

    private static void InsertSession(DatabaseTransaction tx, PerformanceQueueState state, string snapshot, long now)
    {
        if (tx.Scalar("SELECT 1 FROM performance_sessions WHERE id=$id", ("$id", state.Id)) is not null)
            throw new OperationException("resource.conflict", "演奏会话 ID 已存在");
        tx.Execute("INSERT INTO performance_sessions(id,owner,generation,snapshot,updated_at) VALUES($id,$owner,$generation,$snapshot,$updated)",
            ("$id", state.Id), ("$owner", state.Owner), ("$generation", state.Generation.ToString()),
            ("$snapshot", snapshot), ("$updated", now));
        AppendEvent(tx, state);
    }

    private static PerformanceQueueState DecodeReceipt(string json, string hash)
    {
        using var value = JsonDocument.Parse(json);
        if (!StringComparer.Ordinal.Equals(value.RootElement.GetProperty("hash").GetString(), hash))
            throw new OperationException("idempotency.conflict", "幂等键已对应其他演奏会话请求");
        return value.RootElement.GetProperty("result").Deserialize<PerformanceQueueState>(ContractJson.Options)
            ?? throw new StorageException("storage.databaseDamaged", "演奏会话幂等回执损坏");
    }
}
