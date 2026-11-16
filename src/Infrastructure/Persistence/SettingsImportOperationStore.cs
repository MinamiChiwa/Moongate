using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    private static string SettingsRoute(string project) => project switch
    {
        "mabisync" => "/api/v1/rooms/imports/{id}/commit",
        "color-studio" => "/api/v1/color/imports/{id}/commit",
        _ => throw new OperationException("request.invalid", "设置导入项目无效")
    };
    private static void ValidateSettingsImportRequest(string project, string id, string? key, string? etag)
    {
        _ = SettingsRoute(project);
        if (!Regex.IsMatch(id ?? "", @"\A[a-f0-9]{32}\z")) throw new OperationException("request.invalid", "导入 ID 无效");
        if (key is null) throw new OperationException("idempotency.required", "设置导入提交需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "设置导入幂等键无效");
        if (etag is null) throw new OperationException("revision.required", "设置导入提交需要 If-Match");
        if (!Regex.IsMatch(etag, "\\A\"settings-[A-F0-9]{64}\"\\z")) throw new OperationException("request.invalid", "设置导入 ETag 无效");
    }
    public OperationSnapshot? SettingsImportRetry(string project, string principal, string id, string? key, string? etag)
    {
        ValidateSettingsImportRequest(project, id, key, etag);
        var json = store.ReadScalar("SELECT json_object('hash',request_hash,'id',operation_id) FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$id AND key=$key AND (expires_at>$now OR operation_id IN (SELECT id FROM core_operations WHERE terminal=0))",
            ("$p", principal), ("$route", SettingsRoute(project)), ("$id", id), ("$key", key), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds())) as string;
        if (json is null) return null;
        using var value = JsonDocument.Parse(json);
        if (value.RootElement.GetProperty("hash").GetString() != ImportHash(id, etag)) throw new OperationException("idempotency.conflict", "幂等键已对应其他设置修订条件");
        return Get(value.RootElement.GetProperty("id").GetString()!, principal, false);
    }
    public Task<OperationAdmission> AdmitSettingsImportAsync(string project, string principal, string id, string? key, string? etag,
        bool queueAvailable, CancellationToken ct = default)
    {
        ValidateSettingsImportRequest(project, id, key, etag); var hash = ImportHash(id, etag); var route = SettingsRoute(project);
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
            var saved = tx.Scalar("SELECT json_object('hash',request_hash,'id',operation_id) FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$id AND key=$key",
                ("$p", principal), ("$route", route), ("$id", id), ("$key", key)) as string;
            if (saved is not null)
            {
                using var value = JsonDocument.Parse(saved);
                if (value.RootElement.GetProperty("hash").GetString() != hash) throw new OperationException("idempotency.conflict", "幂等键已对应其他设置修订条件");
                return new OperationAdmission(Read(tx, value.RootElement.GetProperty("id").GetString()!), false);
            }
            if (!queueAvailable || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "设置导入操作队列或幂等记录已满");
            var operation = new OperationSnapshot("op-" + Guid.NewGuid().ToString("N"), project == "mabisync" ? "room.settings.import" : "color.settings.import",
                principal, [new("settings-import", id)], OperationState.Queued, "queued", new(1), null, new(now, null, null), null, null,
                new(CancellationState.None, null), EffectStatus.None);
            Save(tx, operation);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,$id,$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", route), ("$id", id), ("$key", key), ("$hash", hash), ("$op", operation.Id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds() + 1));
            return new OperationAdmission(operation, true);
        }, ct);
    }
    private static bool SettingsImportKind(string kind) => kind is "room.settings.import" or "color.settings.import";
    public Task<OperationSnapshot?> BeginSettingsImportAsync(string id, CancellationToken ct = default) => store.WriteAsync<OperationSnapshot?>(tx =>
    {
        var previous = Read(tx, id);
        if (!SettingsImportKind(previous.Kind)) throw new OperationException("request.invalid", "操作不属于设置导入");
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return null;
        if (previous.State.KnownValue != OperationState.Queued) throw new OperationException("resource.busy", "设置导入操作已被领取");
        var next = previous with { State = OperationState.Running, Phase = "applying", Revision = Next(previous),
            Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
        Save(tx, next); return next;
    }, ct);
    public Task<OperationSnapshot> FinishSettingsImportAsync(string id, SettingsImportReceipt? receipt, ApiProblem? problem) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (!SettingsImportKind(previous.Kind)) throw new OperationException("request.invalid", "操作不属于设置导入");
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return previous;
        if (previous.Phase != "applying") throw new OperationException("resource.busy", "设置导入尚未进入提交阶段");
        var success = receipt is not null && problem is null;
        var next = previous with { State = success ? OperationState.Succeeded : OperationState.Failed, Phase = success ? "committed" : "failed",
            Revision = Next(previous), Progress = success ? 1 : null, Result = receipt is null ? null : JsonSerializer.SerializeToElement(receipt, ContractJson.Options),
            Problem = problem, Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
            Cancellation = previous.Cancellation.State.KnownValue == CancellationState.Deferred ? new(CancellationState.Completed, "import-transaction-completed") : previous.Cancellation };
        Save(tx, next); return next;
    });
}
