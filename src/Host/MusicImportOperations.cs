using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Modules.MusicLibrary.Persistence;

namespace MabiTool.Host;

public sealed class MusicImportOperations(OperationStore operations, MusicImportStore imports, ModuleRuntime runtime) : BackgroundService
{
    private sealed record Work(string OperationId, string Owner, string ImportId, string Key, string ETag, IAsyncDisposable Demand);
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(OperationStore.MaximumActiveOperations) { SingleReader = true });
    private bool accepting;

    public async Task<OperationSnapshot> SubmitAsync(string owner, string id, string? key, string? etag, CancellationToken ct = default)
    {
        await admission.WaitAsync(ct);
        IAsyncDisposable? demand = null;
        try
        {
            if (operations.ImportRetry(owner, id, key, etag) is { } retry) return retry;
            if (!accepting) throw new OperationException("storage.unavailable", "导入执行器正在关闭");
            imports.ValidateCommit(owner, id, etag);
            demand = await runtime.AcquireAsync("music-library", "music-import", ct);
            var accepted = await operations.AdmitImportAsync(owner, id, key, etag, queue.Reader.Count < OperationStore.MaximumActiveOperations, ct);
            if (accepted.Created)
            {
                if (!queue.Writer.TryWrite(new(accepted.Snapshot.Id, owner, id, key!, etag!, demand))) throw new InvalidOperationException("已受理导入缺少队列槽位");
                demand = null;
            }
            return accepted.Snapshot;
        }
        finally { if (demand is not null) await demand.DisposeAsync(); admission.Release(); }
    }
    public override Task StartAsync(CancellationToken ct) { accepting = true; return base.StartAsync(ct); }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            await using var demand = job.Demand;
            try
            {
                if (stoppingToken.IsCancellationRequested) { await operations.CancelAsync(job.OperationId, job.Owner, false, "backend-stopping"); continue; }
                if (await operations.BeginImportAsync(job.OperationId) is null) continue;
                MusicImportReceipt? receipt = null; ApiProblem? problem = null;
                try { receipt = await imports.CommitAsync(job.Owner, job.ImportId, job.Key, job.ETag); }
                catch (OperationException error) { problem = ApiErrors.Create(error.Code, error.Message, job.OperationId, error.Details); }
                catch (StorageException) { problem = ApiErrors.Create("storage.unavailable", "导入事务未提交", job.OperationId); }
                await operations.FinishImportAsync(job.OperationId, receipt, problem);
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
