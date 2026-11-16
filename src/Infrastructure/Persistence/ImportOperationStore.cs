using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed partial class OperationStore
{
    private const string ImportRoute = "/api/v1/music/imports/{id}/commit";
    private static string ImportHash(string id, string? etag) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "\n" + etag)));
    private static void ValidateImportRequest(string id, string? key, string? etag)
    {
        if (!Regex.IsMatch(id ?? "", @"\A[a-f0-9]{32}\z")) throw new OperationException("request.invalid", "导入 ID 无效");
        if (key is null) throw new OperationException("idempotency.required", "导入提交需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "导入幂等键格式无效");
        if (etag is null) throw new OperationException("revision.required", "导入提交需要 If-Match");
        if (!Regex.IsMatch(etag, "\\A\"music-library-(0|[1-9][0-9]{0,19})\"\\z")) throw new OperationException("request.invalid", "曲库 ETag 格式无效");
    }
    public OperationSnapshot? ImportRetry(string principal, string id, string? key, string? etag)
    {
        ValidateImportRequest(id, key, etag);
        var json = store.ReadScalar("SELECT json_object('hash',request_hash,'id',operation_id) FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$id AND key=$key AND (expires_at>$now OR operation_id IN (SELECT id FROM core_operations WHERE terminal=0))",
            ("$p", principal), ("$route", ImportRoute), ("$id", id), ("$key", key), ("$now", clock.GetUtcNow().ToUnixTimeMilliseconds())) as string;
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("hash").GetString() != ImportHash(id, etag)) throw new OperationException("idempotency.conflict", "幂等键已对应其他修订条件");
        return Get(document.RootElement.GetProperty("id").GetString()!, principal, false);
    }

    public Task<OperationAdmission> AdmitImportAsync(string principal, string id, string? key, string? etag, bool queueAvailable,
        CancellationToken ct = default)
    {
        ValidateImportRequest(id, key, etag);
        var hash = ImportHash(id, etag);
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)", ("$now", now.ToUnixTimeMilliseconds()));
            var existing = tx.Scalar("SELECT operation_id FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$id AND key=$key",
                ("$p", principal), ("$route", ImportRoute), ("$id", id), ("$key", key)) as string;
            if (existing is not null)
            {
                if ((string)tx.Scalar("SELECT request_hash FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$id AND key=$key",
                    ("$p", principal), ("$route", ImportRoute), ("$id", id), ("$key", key))! != hash)
                    throw new OperationException("idempotency.conflict", "幂等键已对应其他修订条件");
                return new OperationAdmission(Read(tx, existing), false);
            }
            if (!queueAvailable || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations ||
                (long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "导入操作队列或幂等记录已满");
            var operation = new OperationSnapshot("op-" + Guid.NewGuid().ToString("N"), "music.import", principal, [new("music-import", id)],
                OperationState.Queued, "queued", new(1), null, new(now, null, null), null, null, new(CancellationState.None, null), EffectStatus.None);
            Save(tx, operation);
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,$id,$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", ImportRoute), ("$id", id), ("$key", key), ("$hash", hash), ("$op", operation.Id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds() + 1));
            return new OperationAdmission(operation, true);
        }, ct);
    }
    public Task<OperationSnapshot?> BeginImportAsync(string id, CancellationToken ct = default) => store.WriteAsync<OperationSnapshot?>(tx =>
    {
        var previous = Read(tx, id);
        if (previous.Kind != "music.import") throw new OperationException("request.invalid", "操作不属于音乐导入");
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return null;
        if (previous.State.KnownValue != OperationState.Queued) throw new OperationException("resource.busy", "导入操作已被领取");
        var next = previous with { State = OperationState.Running, Phase = "applying", Revision = Next(previous),
            Timestamps = previous.Timestamps with { StartedAt = clock.GetUtcNow() } };
        Save(tx, next); return next;
    }, ct);
    public Task<OperationSnapshot> FinishImportAsync(string id, MusicImportReceipt? receipt, ApiProblem? problem = null) => store.WriteAsync(tx =>
    {
        var previous = Read(tx, id);
        if (previous.Kind != "music.import") throw new OperationException("request.invalid", "操作不属于音乐导入");
        if (SqliteStore.IsTerminal(previous.State.KnownValue)) return previous;
        if (previous.Phase != "applying") throw new OperationException("resource.busy", "导入尚未进入提交阶段");
        var succeeded = receipt is not null && problem is null;
        var next = previous with { State = succeeded ? OperationState.Succeeded : OperationState.Failed,
            Phase = succeeded ? "committed" : "failed", Revision = Next(previous), Progress = succeeded ? 1 : null,
            Result = receipt is null ? null : JsonSerializer.SerializeToElement(receipt, ContractJson.Options), Problem = problem,
            Timestamps = previous.Timestamps with { CompletedAt = clock.GetUtcNow() },
            Cancellation = previous.Cancellation.State.KnownValue == CancellationState.Deferred
                ? new(CancellationState.Completed, "import-transaction-completed") : previous.Cancellation };
        Save(tx, next); return next;
    });
}
