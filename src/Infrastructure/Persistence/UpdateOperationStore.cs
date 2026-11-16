using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Updates;

namespace MabiTool.Infrastructure;

public enum UpdateAction { Check, Download, Apply }
public sealed record UpdateWorkRequest(UpdateAction Action, UpdatePlan? Plan, string? IfMatch);

public sealed partial class OperationStore
{
    public static string UpdatePlanETag(UpdatePlan plan) => $"\"update-plan-{plan.PlanId}-1\"";
    internal static bool IsUpdateOperation(string kind) => kind is "update.check" or "update.download" or "update.apply";

    /// <summary>本机更新任务共享受理；每个客户端的重试身份独立保存，执行器仅领取 Created=true 的任务。</summary>
    public Task<OperationAdmission> AdmitUpdateAsync(string principal, UpdateAction action, UpdatePlan? plan,
        string? key, string? ifMatch = null, bool queueAvailable = true, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(principal) || principal.Length > 128 || principal.Any(char.IsControl) || !Enum.IsDefined(action))
            throw new OperationException("request.invalid", "更新请求主体或动作无效");
        if (key is null) throw new OperationException("idempotency.required", "需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z"))
            throw new OperationException("request.invalid", "幂等键须为 8–128 位 ASCII 字母、数字、点、下划线或连字符");
        if (action == UpdateAction.Check)
        {
            if (plan is not null || ifMatch is not null) throw new OperationException("request.invalid", "检查请求不携带安装计划");
        }
        else
        {
            if (plan is null || !Regex.IsMatch(plan.PlanId, @"\A[a-f0-9]{32}\z"))
                throw new OperationException("request.invalid", "需要已生成的不可变更新计划");
            if (ifMatch is null) throw new OperationException("revision.required", "需要计划 If-Match");
        }
        var encoded = JsonSerializer.SerializeToUtf8Bytes(new UpdateWorkRequest(action, plan, ifMatch), ContractJson.Options);
        if (encoded.Length > 131_072) throw new OperationException("request.bodyTooLarge", "更新任务记录超过 128 KiB");
        // 保存独立副本；受理后不再依赖调用方集合或记录的可变引用。
        var frozen = JsonSerializer.Deserialize<UpdateWorkRequest>(encoded, ContractJson.Options)!;
        var payload = System.Text.Encoding.UTF8.GetString(encoded);
        var hash = Convert.ToHexString(SHA256.HashData(encoded));
        var route = action switch
        {
            UpdateAction.Check => "/api/v1/updates/checks",
            UpdateAction.Download => "/api/v1/updates/plans/{planId}/downloads",
            _ => "/api/v1/updates/plans/{planId}/apply"
        };
        var resource = frozen.Plan?.PlanId ?? "update-state";
        var received = clock.GetUtcNow();
        var kind = action switch { UpdateAction.Check => "update.check", UpdateAction.Download => "update.download", _ => "update.apply" };
        var queued = PrepareUpdate(new OperationSnapshot("op-" + Guid.NewGuid().ToString("N"), kind, principal,
            [new(frozen.Plan is null ? "update-state" : "update-plan", resource)],
            OperationState.Queued, "queued", new(1), null, new(received, null, null), null, null,
            new(CancellationState.None, null), EffectStatus.None));
        return store.WriteAsync(tx =>
        {
            var now = clock.GetUtcNow();
            tx.Execute("DELETE FROM core_idempotency WHERE expires_at<=$now AND operation_id IN (SELECT id FROM core_operations WHERE terminal=1)",
                ("$now", now.ToUnixTimeMilliseconds()));
            var parameters = new (string, object?)[] { ("$p", principal), ("$route", route), ("$resource", resource), ("$key", key) };
            var previousId = tx.Scalar("SELECT operation_id FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$resource AND key=$key", parameters) as string;
            if (previousId is not null)
            {
                var savedHash = tx.Scalar("SELECT request_hash FROM core_idempotency WHERE principal=$p AND route=$route AND resource=$resource AND key=$key", parameters) as string;
                if (savedHash != hash) throw new OperationException("idempotency.conflict", "相同幂等键已绑定其他更新请求");
                return new OperationAdmission(Read(tx, previousId), false);
            }
            if ((long)tx.Scalar("SELECT count(*) FROM core_idempotency")! >= MaximumIdempotencyKeys)
                throw new OperationException("resource.capacityExceeded", "幂等记录容量已满");
            // 安装完成后配对身份会改变；完整请求仍对应同一次已提交安装。
            var sharedId = action == UpdateAction.Apply ? tx.Scalar("""
                SELECT r.operation_id FROM platform_update_requests r JOIN core_operations o ON o.id=r.operation_id
                WHERE r.request_hash=$hash AND o.terminal=1
                  AND json_extract(o.snapshot,'$.kind')='update.apply'
                  AND json_extract(o.snapshot,'$.state')='succeeded'
                  AND json_extract(o.snapshot,'$.phase')='committed'
                ORDER BY r.rowid DESC LIMIT 1
                """, ("$hash", hash)) as string : null;
            sharedId ??= tx.Scalar("SELECT r.operation_id FROM platform_update_requests r JOIN core_operations o ON o.id=r.operation_id WHERE o.terminal=0 ORDER BY r.operation_id LIMIT 1") as string;
            OperationSnapshot operation;
            if (sharedId is not null)
            {
                if ((string?)tx.Scalar("SELECT request_hash FROM platform_update_requests WHERE operation_id=$id", ("$id", sharedId)) != hash)
                    throw new OperationException("resource.busy", "已有其他更新事务，请等待其结束");
                operation = Read(tx, sharedId);
            }
            else
            {
                if (frozen.Plan is { } target && (target.ExpiresAt <= now || UpdatePlanETag(target) != frozen.IfMatch))
                    throw new OperationException(target.ExpiresAt <= now ? "update.planStale" : "revision.stale", "计划已过期或修订条件已改变");
                if (!queueAvailable || (long)tx.Scalar("SELECT count(*) FROM core_operations WHERE terminal=0")! >= MaximumActiveOperations)
                    throw new OperationException("resource.capacityExceeded", "更新执行队列或活动操作容量已满");
                operation = queued.Snapshot; var id = operation.Id;
                SavePrepared(tx, queued);
                tx.Execute("INSERT INTO platform_update_requests VALUES($id,$hash,$payload)", ("$id", id), ("$hash", hash), ("$payload", payload));
            }
            tx.Execute("INSERT INTO core_idempotency VALUES($p,$route,$resource,$key,$hash,$op,$expiry)",
                ("$p", principal), ("$route", route), ("$resource", resource), ("$key", key), ("$hash", hash),
                ("$op", operation.Id), ("$expiry", (now + KeyRetention).ToUnixTimeMilliseconds() + 1));
            return new OperationAdmission(operation, sharedId is null);
        }, token);
    }

    internal UpdateWorkRequest ReadUpdateWork(string id) => store.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM platform_update_requests WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", id);
        var payload = command.ExecuteScalar() as string;
        if (payload is null) throw new OperationException(ApiErrors.ResourceNotFound, "更新任务不存在");
        return JsonSerializer.Deserialize<UpdateWorkRequest>(payload, ContractJson.Options)!;
    });
}
