using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Storage;

public sealed class StorageBackupOperations(OperationStore operations, SqliteStore database, AssetStore assets,
    DataBackupStore backups, StorageDataCatalog data, LocalAuthService auth, TimeProvider clock) : BackgroundService
{
    private sealed record Job(string Id, DataBackupPurpose Purpose, CancellationTokenSource Stop, IDisposable Activity);
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(1) { SingleReader = true });
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly object gate = new();
    private Job? current;
    private volatile bool accepting;
    public override Task StartAsync(CancellationToken ct) { accepting = true; return base.StartAsync(ct); }

    public async Task<OperationSnapshot> SubmitAsync(string owner, string? key, DataBackupPurpose purpose = DataBackupPurpose.Manual, CancellationToken ct = default)
    {
        if (purpose is not (DataBackupPurpose.Automatic or DataBackupPurpose.Manual)) throw new ArgumentOutOfRangeException(nameof(purpose));
        var request = JsonSerializer.SerializeToElement(new { purpose = purpose.ToString() }, ContractJson.Options);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options)));
        await admission.WaitAsync(ct);
        try
        {
            if (operations.StorageRetry(owner, "storage.backup", "data", hash, key) is { } retry) return retry;
            if (!accepting) throw new OperationException("storage.unavailable", "备份执行器正在关闭");
            using var activity = database.Activities.EnterActivity("storage", "storage.backupAdmission");
            var admitted = await operations.AdmitStorageAsync(owner, "storage.backup", "data", hash, key, request, current is null, ct);
            if (admitted.Created)
            {
                var job = new Job(admitted.Snapshot.Id, purpose, new(), database.Activities.EnterActivity("storage", "storage.backup"));
                lock (gate) current = job;
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("已受理备份未进入有界队列");
            }
            return admitted.Snapshot;
        }
        finally { admission.Release(); }
    }

    public async Task<OperationSnapshot> CancelAsync(string id, AuthSession session, string? reason, CancellationToken ct)
    {
        var result = await operations.CancelAsync(id, session.ClientId, session.Owner, reason, ct);
        lock (gate) if (current?.Id == id) current.Stop.Cancel();
        return result;
    }

    public async Task<bool> AutomaticBackupNeededAsync(CancellationToken ct = default)
    {
        var latest = backups.Records().FirstOrDefault(x => x.State == "complete" && x.Purpose is DataBackupPurpose.Automatic or DataBackupPurpose.Manual);
        if (latest is not null && clock.GetUtcNow() - latest.Snapshot.CreatedAt < TimeSpan.FromDays(1)) return false;
        var state = await data.CaptureAsync(ct);
        if (state.Counts.All(x => x.Records.Value == 0) && latest is null) return false;
        return latest is null || (await data.CaptureBackupAsync(latest.Snapshot, ct)).Digest != state.Digest;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            using var hostStop = stoppingToken.Register(job.Stop.Cancel);
            StorageBackupSummary? backup = null; ApiProblem? problem = null; var cancelled = false;
            try
            {
                var operation = await operations.BeginStorageAsync(job.Id, "copying");
                if (operation is null) { cancelled = true; continue; }
                if (!auth.HasPermission(operation.Owner, ApiPermissions.StorageManage)) throw new OperationException("permission.denied", "备份授权已撤销");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(job.Stop.Token); deadline.CancelAfter(TimeSpan.FromMinutes(15));
                var snapshot = await database.CreateOrdinaryDataSnapshotAsync(assets, job.Purpose, deadline.Token);
                backup = DataBackupStore.Summary(backups.Records().Single(x => x.Snapshot.SnapshotId == snapshot.SnapshotId));
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception error)
            {
                var code = error switch { StorageException e => e.Code, OperationException e => e.Code, ActivityBarrierException e => e.Code, _ => "storage.unavailable" };
                if (!ApiErrors.Definitions.Any(x => x.Code == code)) code = "storage.unavailable";
                problem = ApiErrors.Create(code, "备份未完成", job.Id);
            }
            finally
            {
                try { await operations.FinishStorageAsync(job.Id, backup is null ? null : JsonSerializer.SerializeToElement(backup, ContractJson.Options), problem, cancelled, backup: backup); }
                finally { lock (gate) if (current == job) current = null; job.Stop.Dispose(); job.Activity.Dispose(); }
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
}

internal sealed class StorageAutomaticBackupLifetime(StorageBackupOperations backups, SqliteStore database,
    TimeProvider clock, ILogger<StorageAutomaticBackupLifetime> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (database.Activities.Snapshot() is { MaintenanceOwner: not null } or { Activities.Count: > 0 }) continue;
            try
            {
                if (await backups.AutomaticBackupNeededAsync(ct))
                    await backups.SubmitAsync("owner", "automatic-backup-" + clock.GetUtcNow().ToString("yyyyMMdd"), DataBackupPurpose.Automatic, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) when (error is OperationException or StorageException or ActivityBarrierException or IOException)
            { logger.LogWarning("{Code}", error is StorageException storage ? storage.Code : error is OperationException operation ? operation.Code : "storage.unavailable"); }
        }
    }
}
