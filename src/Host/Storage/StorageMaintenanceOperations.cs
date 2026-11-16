using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Storage;

public sealed class StorageMaintenanceOperations(OperationStore operations, SqliteStore database,
    StorageMaintenancePlanStore plans, StorageMaintenanceCoordinator coordinator, LocalAuthService auth,
    IHostApplicationLifetime host, MaintenanceShutdownContext shutdown) : BackgroundService
{
    private sealed class Job(string id, StorageMaintenancePlanRecord preview, string key, string hash)
    {
        public string Id { get; } = id;
        public StorageMaintenancePlanRecord Preview { get; } = preview;
        public string Key { get; } = key;
        public string Hash { get; } = hash;
        public CancellationTokenSource Stop { get; } = new();
        public ActivityBarrier.MaintenanceLease? Maintenance { get; set; }
        public StorageMaintenanceTicket? Ticket { get; set; }
        public OperationSnapshot? Cancellation { get; set; }
    }
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(1) { SingleReader = true });
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly object gate = new();
    private Job? current;
    private volatile bool accepting;
    public override Task StartAsync(CancellationToken ct) { accepting = true; return base.StartAsync(ct); }

    public async Task<OperationSnapshot> SubmitAsync(string id, string owner, string kind, string? key, string? etag, CancellationToken ct)
    {
        var request = JsonSerializer.SerializeToElement(new { planId = id, kind }, ContractJson.Options);
        var hash = StorageMaintenancePlanStore.Hash(request); var operationKind = "storage." + kind;
        await admission.WaitAsync(ct);
        try
        {
            if (operations.StorageRetry(owner, operationKind, id, hash, key) is { } retry) return retry;
            if (!accepting) throw new OperationException("storage.unavailable", "数据维护执行器正在关闭");
            using var activity = database.Activities.EnterActivity("storage", "storage.maintenanceAdmission");
            var preview = await plans.CheckAsync(id, owner, kind, etag, ct);
            var result = await operations.AdmitStorageAsync(owner, operationKind, id, hash, key, request, current is null, ct);
            if (result.Created)
            {
                var job = new Job(result.Snapshot.Id, preview, key!, hash); lock (gate) current = job;
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("已受理数据维护未进入有界队列");
            }
            return result.Snapshot;
        }
        finally { admission.Release(); }
    }
    public async Task<OperationSnapshot> CancelAsync(string id, AuthSession session, string? reason, CancellationToken ct)
    {
        Job? job; lock (gate) job = current?.Id == id ? current : null;
        var cancelled = await operations.CancelStorageAsync(id, session.ClientId, session.Owner, reason, job?.Maintenance, ct);
        lock (gate)
        {
            if (job is not null && current == job)
            {
                job.Cancellation = cancelled; job.Ticket?.UpdateOperation(cancelled);
                if (cancelled.Cancellation.State.KnownValue != CancellationState.Deferred) job.Stop.Cancel();
            }
        }
        return cancelled;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            using var hostStop = stoppingToken.Register(job.Stop.Cancel);
            ApiProblem? problem = null; var cancelled = false; var handedOff = false;
            try
            {
                if (await operations.BeginStorageAsync(job.Id, "waitingForIdle") is null) { cancelled = true; continue; }
                while (job.Maintenance is null)
                {
                    job.Stop.Token.ThrowIfCancellationRequested();
                    if (!auth.HasPermission(job.Preview.Plan.Owner, ApiPermissions.StorageManage)) throw new OperationException("permission.denied", "数据维护授权已撤销");
                    var maintenance = database.Activities.TryEnterMaintenance(job.Id);
                    if (maintenance is not null) lock (gate) job.Maintenance = maintenance;
                    else await Task.Delay(50, job.Stop.Token);
                }
                if (await operations.BeginStorageAsync(job.Id, "preparing", job.Maintenance) is null) { cancelled = true; continue; }
                var ticket = await coordinator.PrepareAsync(job.Preview, job.Id, job.Key, job.Hash, job.Maintenance, job.Stop.Token);
                lock (gate)
                {
                    job.Ticket = ticket;
                    if (job.Cancellation is { } latest && latest.Cancellation.State.KnownValue == CancellationState.Deferred) ticket.UpdateOperation(latest);
                }
                handedOff = true; accepting = false;
                shutdown.Publish(job.Maintenance);
                host.StopApplication();
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception error)
            {
                var code = error switch { StorageException e => e.Code, OperationException e => e.Code, ActivityBarrierException e => e.Code, _ => "storage.unavailable" };
                if (!ApiErrors.Definitions.Any(x => x.Code == code)) code = "storage.unavailable";
                problem = ApiErrors.Create(code, "数据维护在切换前结束", job.Id);
            }
            finally
            {
                if (!handedOff)
                {
                    try { await operations.FinishStorageAsync(job.Id, null, problem, cancelled, job.Maintenance); }
                    catch (ActivityBarrierException) when (stoppingToken.IsCancellationRequested) { }
                    finally { lock (gate) if (current == job) current = null; job.Maintenance?.Dispose(); job.Stop.Dispose(); }
                }
            }
        }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try { accepting = false; queue.Writer.TryComplete(); lock (gate) current?.Stop.Cancel(); }
        finally { admission.Release(); }
        await base.StopAsync(ct);
    }
    public override void Dispose()
    {
        lock (gate) { current?.Ticket?.Dispose(); current?.Maintenance?.Dispose(); current?.Stop.Dispose(); current = null; }
        base.Dispose();
    }
}
