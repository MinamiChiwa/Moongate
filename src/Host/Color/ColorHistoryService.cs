using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Color;

public sealed class ColorHistoryService(OperationStore operations, AssetStore assets)
{
    private readonly SemaphoreSlim exports = new(1, 1);
    public ColorRun Get(string id, AuthSession session)
    {
        var operation = operations.Get(operations.ColorOperationId(id), session.ClientId, session.Owner);
        if (!SqliteStore.IsTerminal(operation.State.KnownValue)) throw new OperationException("resource.busy", "运行结束后才能读取历史证据");
        return operations.ColorRun(operation.Id, session.ClientId, session.Owner);
    }

    public async Task<AssetSnapshot> ExportAsync(string runId, AuthSession session, CancellationToken ct)
    {
        var run = Get(runId, session);
        if (!await exports.WaitAsync(0, ct)) throw new OperationException("resource.busy", "另一份染色历史证据正在导出");
        try
        {
            var scope = "history:" + run.Id;
            // 作用域固定不可变终态，重试及进程重启后复用已经发布的资产。
            if (assets.Find("color", scope) is { } existing) return existing;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(run, ContractJson.Options);
            await using var content = new MemoryStream(bytes, writable: false);
            var exported = await assets.PutAsync("color", scope, "application/json", content, 65536, ct);
            await RemoveOrphanedAsync(ct);
            return exported;
        }
        finally { exports.Release(); }
    }

    // 历史按条数裁剪后，已裁掉运行的导出资产随下一次导出删除；仍被下载或模块引用的资产保留到释放后。
    private async Task RemoveOrphanedAsync(CancellationToken ct)
    {
        var retained = operations.RetainedColorRunIds();
        var removed = 0;
        foreach (var asset in assets.ListByScopePrefix("color", "history:").Where(asset => !retained.Contains(asset.Scope[8..])))
        {
            try { if (await assets.RemoveAsync(asset.Id, ct)) removed++; }
            catch (StorageException error) when (error.Code == "storage.assetInUse") { }
        }
        if (removed > 0) await assets.CollectGarbageAsync(ct);
    }

    public AssetSnapshot Asset(string id, AuthSession session)
    {
        var asset = assets.GetMetadata(id);
        if (asset is null || asset.ModuleId != "color" || !asset.Scope.StartsWith("history:", StringComparison.Ordinal))
            throw new OperationException("resource.notFound", "染色历史资产不存在");
        _ = Get(asset.Scope[8..], session);
        return assets.Get(id)!;
    }

    public async Task<ColorAssetContent> ContentAsync(string id, ColorAssetRangeRequest request, AuthSession session, CancellationToken ct)
    {
        var asset = Asset(id, session);
        if (request.Offset < 0 || request.Count is < 1 or > 262144 || (ulong)request.Offset > asset.SizeBytes.Value)
            throw new OperationException("request.invalid", "资产范围无效，每次最多 256 KiB");
        var bytes = await assets.ReadRangeAsync(id, request.Offset, request.Count, 65536, ct);
        return new(id, asset.Sha256, request.Offset, checked((int)asset.SizeBytes.Value), Convert.ToBase64String(bytes));
    }
}
