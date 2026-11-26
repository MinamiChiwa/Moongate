using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed class OperationException(string code, string message, IReadOnlyDictionary<string, JsonElement>? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, JsonElement>? Details { get; } = details;
}

public sealed record OperationAdmission(OperationSnapshot Snapshot, bool Created);

/// <summary>操作受理、取消和资源修订共用写事务；执行器只领取本次进程受理的操作。</summary>
public sealed partial class OperationStore(SqliteStore store, TimeProvider? timeProvider = null)
{
    public const int MaximumActiveOperations = 128;
    public const int MaximumIdempotencyKeys = 10000;
    public static readonly TimeSpan KeyRetention = TimeSpan.FromHours(24);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private const string ConfigureRoute = "/api/v1/modules/{id}/configuration";

    public static string ConfigurationETag(ModuleConfiguration value) => $"\"module-{value.Id}-{value.Revision}\"";
    public static string OperationETag(OperationSnapshot value) => $"\"{value.Id}-{value.Revision}\"";
    public static bool IsSharedOperation(string kind) => IsUpdateOperation(kind) || IsRoomServerOperation(kind);

    public ModuleConfiguration Configuration(string id, bool defaultEnabled) => store.Read<ModuleConfiguration>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled,revision FROM core_module_configuration WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var row = command.ExecuteReader();
        return row.Read() ? new(id, row.GetInt64(0) != 0, UInt64String.Parse(row.GetString(1))) : new(id, defaultEnabled, new(0));
    });

    public IReadOnlyList<ModuleConfiguration> SavedConfigurations() => store.Read<IReadOnlyList<ModuleConfiguration>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,enabled,revision FROM core_module_configuration";
        using var rows = command.ExecuteReader();
        var result = new List<ModuleConfiguration>();
        while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetInt64(1) != 0, UInt64String.Parse(rows.GetString(2))));
        return result;
    });

    public OperationSnapshot Get(string id, string principal, bool owner)
    {
        var value = store.GetOperation(id);
        if (value is null || !owner && value.Owner != principal && !IsSharedOperation(value.Kind))
            throw new OperationException(ApiErrors.ResourceNotFound, "操作不存在");
        return value;
    }

    public Task<OperationAdmission> AdmitConfigurationAsync(string principal, string id, bool defaultEnabled,
        ModuleConfigurationRequest request, string? key, string? ifMatch, bool queueAvailable = true,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? activeOperationIds = null)
    {
        if (key is null) throw new OperationException("idempotency.required", "需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z"))
            throw new OperationException("request.invalid", "幂等键须为 8–128 位 ASCII 字母、数字、点、下划线或连字符");
        if (string.IsNullOrEmpty(ifMatch)) throw new OperationException("revision.required", "需要配置 If-Match");
        if (ifMatch.Length > 128 || !Regex.IsMatch(ifMatch, @"\A""module-[a-z][a-z0-9-]*-[0-9]+""\z"))
            throw new OperationException("request.invalid", "需要单个强配置 ETag");
        cancellationToken.ThrowIfCancellationRequested();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(request, ContractJson.Options) + "\n" + ifMatch)));
        var received = clock.GetUtcNow();
        var queued = SqliteStore.PrepareOperation(new OperationSnapshot("op-" + Guid.NewGuid().ToString("N"),
            "module.configure", principal, [new("module", id)], OperationState.Queued, "queued", new(1), null,
            new(received, null, null), null, null, new(CancellationState.None, null), EffectStatus.None));
        return store.WriteAsync<OperationAdmission>(tx =>
        {
            var now = clock.GetUtcNow();
            // 只回收到期且已终止的记录；活动操作的重试身份始终保留。
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
            var previousId = tx.Scalar("SELECT operation_id FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$resource AND key=$key",
                ("$p", principal), ("$route", ConfigureRoute), ("$resource", id), ("$key", key)) as string;
            if (previousId is not null)
            {
                var previousHash = tx.Scalar("SELECT request_hash FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$resource AND key=$key",
                    ("$p", principal), ("$route", ConfigureRoute), ("$resource", id), ("$key", key)) as string;
                if (previousHash != hash) throw new OperationException("idempotency.conflict", "相同幂等键已用于其他请求内容或修订条件");
                return new(Read(tx, previousId), false);
            }
            if (!request.Enabled && activeOperationIds is { Count: > 0 })
                throw new OperationException("module.inUse", "模块仍有活动操作", new Dictionary<string, JsonElement>
                    { ["operationIds"] = JsonSerializer.SerializeToElement(activeOperationIds) });
            if (!queueAvailable || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "操作队列或幂等记录容量已满");
            tx.Execute("INSERT INTO core_module_configuration VALUES($id,$enabled,'0',NULL) ON CONFLICT(id) DO NOTHING", ("$id", id), ("$enabled", defaultEnabled ? 1 : 0));
            var revision = UInt64String.Parse((string)tx.Scalar("SELECT revision FROM core_module_configuration WHERE id=$id", ("$id", id))!);
            if (ConfigurationETag(new(id, defaultEnabled, revision)) != ifMatch)
                throw new OperationException("revision.stale", "配置修订已改变，请读取最新配置");
            if (revision.Value == ulong.MaxValue) throw new OperationException("resource.capacityExceeded", "配置修订空间耗尽");
            if (tx.Scalar("SELECT pending_operation FROM core_module_configuration WHERE id=$id", ("$id", id)) is string)
                throw new OperationException("resource.busy", "该模块配置已有活动操作");
            var snapshot = queued.Snapshot; var operationId = snapshot.Id;
            SqliteStore.SavePreparedOperation(tx, queued);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,$resource,$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", ConfigureRoute), ("$resource", id), ("$key", key), ("$hash", hash), ("$op", operationId), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds() + 1));
            tx.Execute("UPDATE core_module_configuration SET pending_operation=$op WHERE id=$id", ("$op", operationId), ("$id", id));
            return new(snapshot, true);
        }, cancellationToken);
    }

    public Task<OperationSnapshot> CancelAsync(string id, string principal, bool owner, string? reason, CancellationToken ct = default)
    {
        if (reason is { Length: > 256 } || reason?.Any(char.IsControl) == true)
            throw new OperationException("request.invalid", "取消原因最多 256 字符且不能包含控制字符");
        if (store.GetOperation(id) is { } operation && IsUpdateOperation(operation.Kind))
            return CancelUpdateAsync(id, principal, owner, reason, ct);
        return store.WriteAsync(tx => Cancel(tx, id, principal, owner, reason), ct);
    }

    private OperationSnapshot Cancel(DatabaseTransaction tx, string id, string principal, bool owner, string? reason)
    {
        var previous = Read(tx, id);
        if (!owner && previous.Owner != principal) throw new OperationException(ApiErrors.ResourceNotFound, "操作不存在");
        if (SqliteStore.IsTerminal(previous.State.KnownValue) || previous.Cancellation.State.KnownValue != CancellationState.None) return previous;
        var deferred = previous.Phase == "applying";
        var color = previous.Kind == "color.search";
        var combat = previous.Kind == "combat.observe" || previous.Kind.StartsWith("inventory.", StringComparison.Ordinal) || previous.Kind == FishingKind;
        var roomServer = IsRoomServerOperation(previous.Kind);
        var storage = previous.Kind is "storage.backup" or "storage.restore" or "storage.migrate" or "storage.moduleDelete";
        var snapshot = previous with
        {
            State = deferred || color || combat || storage || roomServer ? OperationState.CancelRequested : OperationState.Cancelled,
            Phase = deferred ? previous.Phase : color || combat || storage || roomServer ? "stopping" : "cancelled", Revision = Next(previous),
            Cancellation = new(deferred ? CancellationState.Deferred : color || combat || storage || roomServer ? CancellationState.Requested : CancellationState.Completed, reason),
            Timestamps = deferred || color || combat || storage || roomServer ? previous.Timestamps : previous.Timestamps with { CompletedAt = clock.GetUtcNow() }
        };
        if (color) snapshot = snapshot with { Result = JsonSerializer.SerializeToElement(
            CloseColor(ReadColor(previous), "stopping", reason) with { Revision = snapshot.Revision }, ContractJson.Options) };
        Save(tx, snapshot);
        return snapshot;
    }

    // 与取消请求通过同一事务队列裁决；返回 null 表示已终止，执行器不得再调用模块。
    public Task<OperationSnapshot?> BeginConfigurationAsync(string id, CancellationToken ct = default) => store.WriteAsync<OperationSnapshot?>(tx =>
    {
        var previous = Read(tx, id);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return null;
        if (previous.State.KnownValue != OperationState.Queued) throw new OperationException("resource.busy", "操作已被执行器领取");
        var snapshot = previous with { State = OperationState.Running, Phase = "applying", Revision = Next(previous),
            Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
        Save(tx, snapshot);
        return snapshot;
    }, ct);

    public Task<OperationSnapshot> FinishConfigurationAsync(string operationId, bool enabled, ApiProblem? problem = null, bool interrupted = false) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, operationId);
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return previous;
        if (previous.Phase != "applying") throw new OperationException("resource.busy", "操作未进入应用阶段");
        ModuleConfiguration? configuration = null;
        var id = previous.ResourceRefs.Single().Id;
        if (problem is null && !interrupted)
        {
            if ((string?)tx.Scalar("SELECT pending_operation FROM core_module_configuration WHERE id=$id", ("$id", id)) != operationId)
                throw new OperationException("resource.busy", "配置操作租约已改变");
            var revision = UInt64String.Parse((string)tx.Scalar("SELECT revision FROM core_module_configuration WHERE id=$id", ("$id", id))!);
            configuration = new(id, enabled, new(checked(revision.Value + 1)));
            tx.Execute("UPDATE core_module_configuration SET enabled=$enabled,revision=$revision WHERE id=$id",
                ("$enabled", enabled ? 1 : 0), ("$revision", configuration.Revision.ToString()), ("$id", id));
            SqliteStore.AppendEvent(tx, new("module-config-" + id + "-" + configuration.Revision, "module-configuration", id,
                configuration.Revision, "module.configuration-changed", JsonSerializer.SerializeToElement(configuration, ContractJson.Options)));
        }
        var state = interrupted ? OperationState.Interrupted : problem is null ? OperationState.Succeeded : OperationState.Failed;
        var snapshot = previous with
        {
            State = state, Phase = state.ToString().ToLowerInvariant(), Revision = Next(previous), Progress = problem is null && !interrupted ? 1 : null,
            Result = configuration is null ? null : JsonSerializer.SerializeToElement(configuration, ContractJson.Options), Problem = problem,
            EffectStatus = interrupted ? EffectStatus.Unknown : EffectStatus.None,
            Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
            Cancellation = previous.Cancellation.State.KnownValue == CancellationState.Deferred ? new(CancellationState.Completed, "application-completed") : previous.Cancellation
        };
        Save(tx, snapshot);
        return snapshot;
    });

    private static UInt64String Next(OperationSnapshot previous) => new(checked(previous.Revision.Value + 1));
    private static OperationSnapshot Read(DatabaseTransaction tx, string id)
    {
        var json = tx.Scalar("SELECT snapshot FROM core_operations WHERE id=$id", ("$id", id)) as string;
        return json is null ? throw new OperationException(ApiErrors.ResourceNotFound, "操作不存在") : JsonSerializer.Deserialize<OperationSnapshot>(json, ContractJson.Options)!;
    }
    private static void Save(DatabaseTransaction tx, OperationSnapshot snapshot)
    {
        SqliteStore.SaveOperation(tx, snapshot);
        if (IsUpdateOperation(snapshot.Kind)) SaveUpdateProgress(tx, snapshot);
        if (SqliteStore.IsTerminal(snapshot.State.KnownValue))
            tx.Execute("UPDATE core_module_configuration SET pending_operation=NULL WHERE pending_operation=$id", ("$id", snapshot.Id));
    }
}
