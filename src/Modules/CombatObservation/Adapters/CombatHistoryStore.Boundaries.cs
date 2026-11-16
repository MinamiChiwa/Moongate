using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Modules.CombatObservation.Adapters;

public sealed record CombatBoundaryReceipt(string Principal, string Route, string Resource, string Key, string Hash,
    string ExpectedWorkspaceETag, CombatEncounter Result);

public sealed partial class CombatHistoryStore
{
    public Task<CombatEncounter?> BoundaryReceiptAsync(string principal, string route, string resource, string? key,
        string hash, CancellationToken ct = default) => database.WriteAsync(tx => Receipt<CombatEncounter>(tx, principal, route, resource, key, hash), ct);
    private void SaveBoundary(DatabaseTransaction tx, CombatBoundaryReceipt receipt)
    {
        var previous = Receipt<CombatEncounter>(tx, receipt.Principal, receipt.Route, receipt.Resource, receipt.Key, receipt.Hash);
        if (previous is not null) throw new OperationException("idempotency.conflict", "战斗边界已经提交，请读取原回执");
        var json = tx.Scalar("SELECT workspace FROM combat_observations WHERE id=$id", ("$id", receipt.Result.ObservationId)) as string;
        var workspace = json is null ? null : JsonSerializer.Deserialize<CombatWorkspace>(json, ContractJson.Options);
        if (workspace is null || WorkspaceETag(workspace) != receipt.ExpectedWorkspaceETag)
            throw new OperationException("revision.stale", "观察检查点已变化");
        SaveReceipt(tx, receipt.Principal, receipt.Route, receipt.Resource, receipt.Key, receipt.Hash, receipt.Result);
    }
}
