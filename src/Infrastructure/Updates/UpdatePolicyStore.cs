using System.Text.Json;
using System.Security.Cryptography;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure.Updates;

public sealed record UpdateCheckSchedule(DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt);

/// <summary>策略修订和检查时间在同一短写事务内裁决；网络请求由协调器在事务外执行。</summary>
public sealed class UpdatePolicyStore(SqliteStore database, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumJitter = TimeSpan.FromMinutes(15);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    internal event Action? Changed;
    public static IReadOnlyList<DatabaseMigration> Migrations { get; } =
    [new("platform-updates", 1, """
        CREATE TABLE platform_update_policy (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            revision TEXT NOT NULL,
            policy TEXT NOT NULL,
            last_attempt_at INTEGER,
            next_attempt_at INTEGER
        ) STRICT;
        """), new("platform-updates", 2, """
        CREATE TABLE platform_update_requests (
            operation_id TEXT PRIMARY KEY REFERENCES core_operations(id),
            request_hash TEXT NOT NULL,
            payload TEXT NOT NULL
        ) STRICT;
        """), new("platform-updates", 3, """
        UPDATE platform_update_policy
        SET policy=json_remove(policy,'$.automaticDownloads','$.installWhenIdle','$.allowMeteredDownloads');
        """), new("platform-updates", 4, """
        CREATE TABLE platform_update_policy_receipts(principal TEXT NOT NULL,key TEXT NOT NULL,hash TEXT NOT NULL,
            result TEXT NOT NULL,expires INTEGER NOT NULL,PRIMARY KEY(principal,key)) STRICT;
        """), new("platform-updates", 5, """
        UPDATE platform_update_policy
        SET policy=json_set(policy,'$.maximumBytesPerSecond',NULL)
        WHERE revision='0';
        """)];

    public static string ETag(UpdatePolicySnapshot snapshot) => $"\"update-policy-{snapshot.Revision}\"";

    public UpdatePolicySnapshot Get() => database.Read<UpdatePolicySnapshot>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT policy,revision FROM platform_update_policy WHERE singleton=1";
        using var row = command.ExecuteReader();
        return row.Read() ? new(Decode(row.GetString(0)), UInt64String.Parse(row.GetString(1))) : new(new(), new(0));
    });

    public UpdateCheckSchedule Schedule() => database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_attempt_at,next_attempt_at FROM platform_update_policy WHERE singleton=1";
        using var row = command.ExecuteReader();
        return row.Read() ? new UpdateCheckSchedule(row.IsDBNull(0) ? null : DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(0)),
            row.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(1))) : new(null, null);
    });

    public async Task<UpdatePolicySnapshot> SaveAsync(UpdatePolicy policy, string? ifMatch, CancellationToken token = default)
    {
        Validate(policy);
        if (ifMatch is null) throw new OperationException("revision.required", "需要策略 If-Match");
        var result = await database.WriteAsync(tx => Save(tx, policy, ifMatch), token).ConfigureAwait(false);
        Changed?.Invoke();
        return result;
    }

    public async Task<UpdatePolicySnapshot> SaveIdempotentAsync(string principal, UpdatePolicy policy, string? key,
        string? ifMatch, CancellationToken token = default)
    {
        Validate(policy); FileTransferStore.Key(key);
        if (ifMatch is null) throw new OperationException("revision.required", "需要策略 If-Match");
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { policy, ifMatch }, ContractJson.Options)));
        var result = await database.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM platform_update_policy_receipts WHERE expires<=$now", ("$now", now.ToUnixTimeMilliseconds()));
            if (tx.Scalar("SELECT result FROM platform_update_policy_receipts WHERE principal=$p AND key=$key", ("$p", principal), ("$key", key)) is string saved)
            {
                if ((string?)tx.Scalar("SELECT hash FROM platform_update_policy_receipts WHERE principal=$p AND key=$key", ("$p", principal), ("$key", key)) != hash)
                    throw new OperationException("idempotency.conflict", "更新策略幂等键已用于其他内容或修订");
                return JsonSerializer.Deserialize<UpdatePolicySnapshot>(saved, ContractJson.Options)!;
            }
            if ((long)tx.Scalar("SELECT count(*) FROM platform_update_policy_receipts")! >= 10000)
                throw new OperationException("resource.capacityExceeded", "更新策略回执达到上限");
            var next = Save(tx, policy, ifMatch);
            tx.Execute("INSERT INTO platform_update_policy_receipts VALUES($p,$key,$hash,$result,$expires)", ("$p", principal), ("$key", key),
                ("$hash", hash), ("$result", JsonSerializer.Serialize(next, ContractJson.Options)), ("$expires", (now + OperationStore.KeyRetention).ToUnixTimeMilliseconds()));
            return next;
        }, token).ConfigureAwait(false);
        Changed?.Invoke(); return result;
    }

    private UpdatePolicySnapshot Save(DatabaseTransaction tx, UpdatePolicy policy, string ifMatch)
    {
            EnsureRow(tx);
            var previous = Read(tx);
            if (ETag(previous) != ifMatch) throw new OperationException("revision.stale", "更新策略修订已改变");
            if (previous.Policy == policy) return previous;
            if (previous.Revision.Value == ulong.MaxValue) throw new OperationException("resource.capacityExceeded", "策略修订空间耗尽");
            var next = new UpdatePolicySnapshot(policy, new(previous.Revision.Value + 1));
            tx.Execute("UPDATE platform_update_policy SET policy=$policy,revision=$revision WHERE singleton=1",
                ("$policy", JsonSerializer.Serialize(policy, ContractJson.Options)), ("$revision", next.Revision.ToString()));
            if (!previous.Policy.AutomaticChecks && policy.AutomaticChecks)
                SetNext(tx, clock.GetUtcNow() + StartupDelay);
            else if (previous.Policy.CheckIntervalHours != policy.CheckIntervalHours)
                SetNext(tx, clock.GetUtcNow() + TimeSpan.FromHours(policy.CheckIntervalHours));
            SqliteStore.AppendEvent(tx, new("update-policy-" + next.Revision, "update-policy", "update-policy", next.Revision,
                "update.policy-changed", JsonSerializer.SerializeToElement(next, ContractJson.Options)));
            return next;
    }

    public Task ScheduleStartupAsync(CancellationToken token = default) => database.WriteAsync(tx =>
    {
        EnsureRow(tx);
        var earliest = (clock.GetUtcNow() + StartupDelay).ToUnixTimeMilliseconds();
        tx.Execute("UPDATE platform_update_policy SET next_attempt_at=max(coalesce(next_attempt_at,$earliest),$earliest) WHERE singleton=1",
            ("$earliest", earliest));
        return true;
    }, token);

    public Task<bool> TryClaimCheckAsync(double jitterUnit, CancellationToken token = default)
    {
        if (!double.IsFinite(jitterUnit) || jitterUnit is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(jitterUnit));
        return database.WriteAsync(tx =>
        {
            EnsureRow(tx);
            var policy = Read(tx).Policy;
            var now = clock.GetUtcNow();
            var next = tx.Scalar("SELECT next_attempt_at FROM platform_update_policy WHERE singleton=1");
            if (!policy.AutomaticChecks || next is not long due || due > now.ToUnixTimeMilliseconds()) return false;
            if ((long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= OperationStore.MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= OperationStore.MaximumIdempotencyKeys ||
                (long)tx.Scalar("SELECT count(*) FROM platform_update_requests r JOIN core_operations o ON o.id=r.operation_id WHERE o.terminal=0")! > 0)
                return false;
            var nextAt = now + TimeSpan.FromHours(policy.CheckIntervalHours) + MaximumJitter * jitterUnit;
            tx.Execute("UPDATE platform_update_policy SET last_attempt_at=$now,next_attempt_at=$next WHERE singleton=1",
                ("$now", now.ToUnixTimeMilliseconds()), ("$next", nextAt.ToUnixTimeMilliseconds()));
            return true;
        }, token);
    }

    private static void EnsureRow(DatabaseTransaction tx) => tx.Execute(
        "INSERT OR IGNORE INTO platform_update_policy VALUES(1,'0',$policy,NULL,NULL)",
        ("$policy", JsonSerializer.Serialize(new UpdatePolicy(), ContractJson.Options)));

    private static void SetNext(DatabaseTransaction tx, DateTimeOffset next) => tx.Execute(
        "UPDATE platform_update_policy SET next_attempt_at=$next WHERE singleton=1", ("$next", next.ToUnixTimeMilliseconds()));

    private static UpdatePolicySnapshot Read(DatabaseTransaction tx) => new(
        Decode((string)tx.Scalar("SELECT policy FROM platform_update_policy WHERE singleton=1")!),
        UInt64String.Parse((string)tx.Scalar("SELECT revision FROM platform_update_policy WHERE singleton=1")!));

    private static UpdatePolicy Decode(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<UpdatePolicy>(json, ContractJson.Options)!;
            Validate(value);
            return value;
        }
        catch (Exception error) when (error is JsonException or OperationException)
        {
            throw new StorageException("storage.databaseInvalid", "持久化更新策略无效");
        }
    }

    private static void Validate(UpdatePolicy policy)
    {
        if (policy is null || policy.CheckIntervalHours is < 1 or > 168 || policy.DownloadConcurrency is < 1 or > 4 ||
            policy.MaximumBytesPerSecond is < 65_536 or > 67_108_864) // null 为不限速
            throw new OperationException("request.invalid", "更新检查周期须为 1–168 小时、下载并发 1–4、带宽 64 KiB/s–64 MiB/s");
    }
}
