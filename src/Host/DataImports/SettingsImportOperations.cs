using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.DataImports;

public sealed class SettingsImportOperations(SettingsImportCoordinator imports, OperationStore operations, ModuleRuntime runtime) : BackgroundService
{
    private sealed record Work(string Project, string OperationId, string Owner, string ImportId, string Key, string ETag, IAsyncDisposable Demand);
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(OperationStore.MaximumActiveOperations) { SingleReader = true });
    private bool accepting;
    public async Task<OperationSnapshot> SubmitAsync(string project, string owner, string id, string? key, string? etag, CancellationToken ct)
    {
        await admission.WaitAsync(ct); IAsyncDisposable? demand = null;
        try
        {
            if (operations.SettingsImportRetry(project, owner, id, key, etag) is { } retry) return retry;
            if (!accepting) throw new OperationException("storage.unavailable", "设置导入执行器正在关闭");
            imports.ValidateCommit(project, owner, id, etag);
            demand = await runtime.AcquireAsync("core", "settings-import", ct);
            var accepted = await operations.AdmitSettingsImportAsync(project, owner, id, key, etag, queue.Reader.Count < OperationStore.MaximumActiveOperations, ct);
            if (accepted.Created)
            {
                if (!queue.Writer.TryWrite(new(project, accepted.Snapshot.Id, owner, id, key!, etag!, demand))) throw new InvalidOperationException("设置导入队列槽位缺失");
                demand = null;
            }
            return accepted.Snapshot;
        }
        finally { if (demand is not null) await demand.DisposeAsync(); admission.Release(); }
    }
    public override Task StartAsync(CancellationToken ct) { accepting = true; return base.StartAsync(ct); }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in queue.Reader.ReadAllAsync())
        {
            await using var demand = work.Demand;
            try
            {
                if (stoppingToken.IsCancellationRequested) { await operations.CancelAsync(work.OperationId, work.Owner, false, "backend-stopping"); continue; }
                if (await operations.BeginSettingsImportAsync(work.OperationId) is null) continue;
                SettingsImportReceipt? receipt = null; ApiProblem? problem = null;
                try { receipt = await imports.CommitAsync(work.Project, work.Owner, work.ImportId, work.Key, work.ETag); }
                catch (OperationException error) { problem = ApiErrors.Create(error.Code, error.Message, work.OperationId, error.Details); }
                catch (StorageException error)
                {
                    var code = ApiErrors.Definitions.Any(x => x.Code == error.Code) ? error.Code : "storage.unavailable";
                    problem = ApiErrors.Create(code, "设置导入事务未提交", work.OperationId);
                }
                await operations.FinishSettingsImportAsync(work.OperationId, receipt, problem);
            }
            catch (Exception)
            {
                await admission.WaitAsync();
                try { accepting = false; queue.Writer.TryComplete(); }
                finally { admission.Release(); }
                while (queue.Reader.TryRead(out var pending)) await pending.Demand.DisposeAsync();
                return;
            }
        }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try { accepting = false; queue.Writer.TryComplete(); }
        finally { admission.Release(); }
        await base.StopAsync(ct);
    }
}
