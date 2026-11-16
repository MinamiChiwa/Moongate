using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Diagnostics;

public sealed class DiagnosticExportOperations(OperationStore operations, DiagnosticStore diagnostics) : BackgroundService
{
    private sealed record Job(string Id, CancellationTokenSource Stop);
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(8) { SingleReader = true });
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private volatile bool accepting;
    public override Task StartAsync(CancellationToken ct) { accepting = true; return base.StartAsync(ct); }
    public async Task<OperationSnapshot> SubmitAsync(string principal, bool owner, DiagnosticExportRequest request, string? key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = JsonSerializer.Deserialize<DiagnosticExportRequest>(JsonSerializer.Serialize(request, ContractJson.Options), ContractJson.Options)!;
        await admission.WaitAsync(ct);
        try
        {
            if (!accepting) throw new OperationException("provider.unavailable", "诊断导出执行器正在关闭");
            bool available; lock (gate) available = jobs.Count < 8;
            var result = await operations.AdmitDiagnosticExportAsync(principal, owner, request, key, available, ct);
            if (result.Created)
            {
                var job = new Job(result.Snapshot.Id, new());
                lock (gate) jobs.Add(job.Id, job);
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("已受理诊断导出未能进入保留队列");
            }
            return result.Snapshot;
        }
        catch (StorageException error) when (error.Code is "storage.assetDamaged" or "storage.assetInvalid")
        { throw new OperationException(error.Code, error.Message); }
        finally { admission.Release(); }
    }
    public async Task<OperationSnapshot> CancelAsync(string id, string principal, bool owner, string? reason, CancellationToken ct)
    {
        var operation = operations.Get(id, principal, owner);
        if (operation.Kind != "diagnostics.export") throw new OperationException("resource.notFound", "诊断导出不存在");
        if (reason is { Length: > 256 } || reason?.Any(char.IsControl) == true) throw new OperationException("request.invalid", "取消原因无效");
        var cancelled = await operations.CancelAsync(id, principal, owner, reason, ct);
        lock (gate) if (jobs.TryGetValue(id, out var job)) job.Stop.Cancel();
        return cancelled;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            using var stopHost = stoppingToken.Register(() => job.Stop.Cancel());
            DiagnosticExportArtifact? artifact = null;
            try
            {
                var operation = await operations.BeginDiagnosticExportAsync(job.Id);
                if (operation is null) continue;
                job.Stop.Token.ThrowIfCancellationRequested();
                var plan = operation.Result!.Value.Deserialize<DiagnosticExportPlan>(ContractJson.Options)!;
                artifact = await diagnostics.ExportAsync(operation.Owner, plan, job.Stop.Token);
                if (!await operations.FinishDiagnosticExportAsync(job.Id, artifact)) await diagnostics.DiscardExportAsync(artifact);
            }
            catch (OperationCanceledException)
            {
                if (artifact is not null) await diagnostics.DiscardExportAsync(artifact);
                await operations.FinishDiagnosticExportAsync(job.Id, null, interrupted: stoppingToken.IsCancellationRequested,
                    cancelled: !stoppingToken.IsCancellationRequested);
            }
            catch (Exception error)
            {
                var code = error switch { OperationException e => e.Code, StorageException e when e.Code is "storage.assetDamaged" or "storage.assetInvalid" => e.Code,
                    _ => "storage.unavailable" };
                try { await operations.FinishDiagnosticExportAsync(job.Id, null, ApiErrors.Create(code, "诊断导出未完成", job.Id)); }
                catch { accepting = false; queue.Writer.TryComplete(); }
            }
            finally
            {
                try { await diagnostics.ReleaseSourcesAsync(job.Id); }
                catch { accepting = false; queue.Writer.TryComplete(); throw; }
                finally { lock (gate) jobs.Remove(job.Id); job.Stop.Dispose(); }
            }
        }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try { accepting = false; queue.Writer.TryComplete(); lock (gate) foreach (var job in jobs.Values) job.Stop.Cancel(); }
        finally { admission.Release(); }
        await base.StopAsync(ct);
    }
}
