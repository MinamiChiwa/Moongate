using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Storage;

public sealed class StorageModuleDeletionOperations(OperationStore operations, SqliteStore database,
    StorageModuleDataStore plans, LocalAuthService auth, TimeProvider clock) : BackgroundService
{
    private sealed class Job(string id, StorageModuleDeletionRecord preview)
    {
        public string Id { get; } = id;
        public StorageModuleDeletionRecord Preview { get; } = preview;
        public CancellationTokenSource Stop { get; } = new();
        public ActivityBarrier.MaintenanceLease? Maintenance { get; set; }
    }
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(1) { SingleReader = true });
    private readonly SemaphoreSlim admission = new(1, 1);
    private Job? current;
    private bool accepting;
    public override Task StartAsync(CancellationToken ct) { accepting = true; return base.StartAsync(ct); }

    public async Task<OperationSnapshot> SubmitAsync(string id, string owner, string? key, string? etag, CancellationToken ct)
    {
        var request = JsonSerializer.SerializeToElement(new { planId = id }, ContractJson.Options);
        var hash = StorageMaintenancePlanStore.Hash(request);
        await admission.WaitAsync(ct);
        try
        {
            if (operations.StorageRetry(owner, "storage.moduleDelete", id, hash, key) is { } retry) return retry;
            if (!accepting) throw new OperationException("storage.unavailable", "模块数据删除执行器正在关闭");
            using var activity = database.Activities.EnterActivity("storage", "storage.moduleDeletionAdmission");
            var preview = await plans.CheckAsync(id, owner, etag, ct);
            var result = await operations.AdmitStorageAsync(owner, "storage.moduleDelete", id, hash, key, request, current is null, ct);
            if (result.Created)
            {
                current = new(result.Snapshot.Id, preview);
                if (!queue.Writer.TryWrite(current)) throw new InvalidOperationException("已受理模块删除未进入有界队列");
            }
            return result.Snapshot;
        }
        finally { admission.Release(); }
    }

    public async Task<OperationSnapshot> CancelAsync(string id, AuthSession session, string? reason, CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try
        {
            var job = current?.Id == id ? current : null;
            var result = await operations.CancelStorageAsync(id, session.ClientId, session.Owner, reason, job?.Maintenance, ct);
            if (result.Cancellation.State.KnownValue != CancellationState.Deferred) job?.Stop.Cancel();
            return result;
        }
        finally { admission.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            using var hostStop = stoppingToken.Register(job.Stop.Cancel);
            ApiProblem? problem = null; var cancelled = false; var committed = false;
            try
            {
                if (await operations.BeginStorageAsync(job.Id, "waitingForIdle") is null) { cancelled = true; continue; }
                while (job.Maintenance is null)
                {
                    job.Stop.Token.ThrowIfCancellationRequested();
                    if (!auth.HasPermission(job.Preview.Plan.Owner, ApiPermissions.StorageManage))
                        throw new OperationException("permission.denied", "模块数据删除授权已撤销");
                    if (job.Preview.Plan.ExpiresAt <= clock.GetUtcNow())
                        throw new OperationException("storage.planExpired", "模块删除预览已到期");
                    await admission.WaitAsync(job.Stop.Token);
                    try { job.Maintenance = database.Activities.TryEnterMaintenance(job.Id); }
                    finally { admission.Release(); }
                    if (job.Maintenance is null) await Task.Delay(50, job.Stop.Token);
                }
                await plans.VerifyCurrentAsync(job.Preview, job.Stop.Token);
                if (await operations.BeginStorageAsync(job.Id, "applying", job.Maintenance) is null) { cancelled = true; continue; }
                await plans.ApplyAsync(job.Preview, job.Id, job.Maintenance);
                committed = true;
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception error)
            {
                var code = error switch { StorageException e => e.Code, OperationException e => e.Code, ActivityBarrierException e => e.Code, _ => "storage.unavailable" };
                if (!ApiErrors.Definitions.Any(x => x.Code == code)) code = "storage.unavailable";
                problem = ApiErrors.Create(code, "模块数据删除未完成", job.Id);
            }
            finally
            {
                await admission.WaitAsync();
                try
                {
                    if (!committed) await operations.FinishStorageAsync(job.Id, null, problem, cancelled, job.Maintenance);
                }
                finally
                {
                    if (current == job) current = null;
                    job.Maintenance?.Dispose(); job.Stop.Dispose(); admission.Release();
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try { accepting = false; queue.Writer.TryComplete(); current?.Stop.Cancel(); }
        finally { admission.Release(); }
        await base.StopAsync(ct);
    }
}
