using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using MabiTool.Providers.GitHubReleases;

namespace MabiTool.Host.Updates;

/// <summary>检查与用户下载共享队列，只消费本次进程受理的任务；日历调度只发起检查。</summary>
public sealed class UpdateCheckOperations(OperationStore operations, UpdatePolicyStore policy,
    IUpdateCheckService checker, TimeProvider clock, IUpdateDownloadService? downloader = null,
    IUpdateInstallService? installer = null, ResourceLeaseCoordinator? leases = null, LocalAuthService? auth = null) : BackgroundService
{
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly SemaphoreSlim scheduleChanged = new(0, 1);
    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(1) { SingleReader = true });
    private bool accepting;
    private string? activeId;
    private string? handedOffId;

    public async Task<OperationSnapshot> SubmitAsync(string principal, string? key, CancellationToken token = default)
    {
        await admission.WaitAsync(token);
        try { return await AdmitAsync(principal, key, token); }
        finally { admission.Release(); }
    }

    public async Task<OperationSnapshot> SubmitDownloadAsync(string principal, string planId, string? key, string? ifMatch,
        CancellationToken token = default)
        => await SubmitPlanAsync(principal, planId, key, ifMatch, UpdateAction.Download, token);

    public async Task<OperationSnapshot> SubmitApplyAsync(string principal, string planId, string? key, string? ifMatch,
        CancellationToken token = default)
        => await SubmitPlanAsync(principal, planId, key, ifMatch, UpdateAction.Apply, token);

    private async Task<OperationSnapshot> SubmitPlanAsync(string principal, string planId, string? key, string? ifMatch,
        UpdateAction action, CancellationToken token)
    {
        if (downloader is null) throw new UpdateSourceException("update.sourceNotConfigured", []);
        // 先读取原始计划供持久幂等裁决；签名和期限在真正执行时重验。
        var plan = await downloader.ReadPlanAsync(planId, token);
        await admission.WaitAsync(token);
        try
        {
            if (!accepting) throw new OperationException("storage.unavailable", "更新执行器正在关闭或等待恢复");
            var result = await operations.AdmitUpdateAsync(principal, action, plan, key, ifMatch,
                queueAvailable: queue.Reader.Count == 0, token: token);
            Enqueue(result);
            return result.Snapshot;
        }
        finally { admission.Release(); }
    }

    private async Task<OperationSnapshot> AdmitAsync(string principal, string? key, CancellationToken token)
    {
        if (!accepting) throw new OperationException("storage.unavailable", "更新执行器正在关闭或等待恢复");
        var result = await operations.AdmitUpdateAsync(principal, UpdateAction.Check, null, key,
            queueAvailable: queue.Reader.Count == 0, token: token);
        Enqueue(result);
        return result.Snapshot;
    }

    private void Enqueue(OperationAdmission result)
    {
        if (result.Created)
        {
            activeId = result.Snapshot.Id;
            if (!queue.Writer.TryWrite(activeId)) throw new InvalidOperationException("已受理更新无法进入保留队列");
        }
    }

    public override Task StartAsync(CancellationToken token)
    {
        accepting = true;
        policy.Changed += SignalSchedule;
        return base.StartAsync(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            var execution = RunQueueAsync(lifetime.Token);
            var schedule = RunScheduleAsync(lifetime.Token);
            await Task.WhenAny(execution, schedule);
            await CloseAdmissionAsync();
            lifetime.Cancel();
            await Task.WhenAll(execution, schedule);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // 持久写入失败后停止受理，重启由统一恢复逻辑标记中断。
            await CloseAdmissionAsync();
        }
    }

    private async Task RunQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (await operations.BeginUpdateAsync(id, stoppingToken) is null) continue;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                using var monitorStop = new CancellationTokenSource();
                var monitor = MonitorCancellationAsync(id, cancellation, monitorStop.Token);
                JsonElement? result = null;
                ApiProblem? problem = null;
                var transferred = false;
                try
                {
                    var work = operations.ReadUpdateWork(id);
                    if (work.Action == UpdateAction.Check)
                        result = JsonSerializer.SerializeToElement(await checker.CheckAsync(cancellation.Token), ContractJson.Options);
                    else if (work.Action == UpdateAction.Download && downloader is not null && work.Plan is not null)
                    {
                        var downloaded = await downloader.DownloadAsync(work.Plan, async (phase, status, ct) =>
                        {
                            if (!await operations.ReportUpdateDownloadAsync(id, phase, JsonSerializer.SerializeToElement(status, ContractJson.Options), ct))
                            { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                        }, cancellation.Token);
                        result = JsonSerializer.SerializeToElement(downloaded, ContractJson.Options);
                    }
                    else if (work.Action == UpdateAction.Apply && installer is not null && leases is not null && work.Plan is not null)
                    {
                        while (true)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            var owner = operations.Get(id, "system-updates", true).Owner;
                            if (auth is not null && !auth.HasPermission(owner, ApiPermissions.UpdatesManage))
                                throw new OperationException("permission.denied", "安装授权已撤销");
                            if (await operations.TryBeginUpdateMaintenanceAsync(id, leases, cancellation.Token)) break;
                            await Task.Delay(50, cancellation.Token);
                        }
                        transferred = await installer.TransferAsync(id, work.Plan, operations.RequireUpdateMaintenance(id), cancellation.Token);
                        if (!transferred) throw new UpdatePlanException("update.installationFailed");
                    }
                    else throw new InvalidOperationException("更新动作没有执行器");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    var cancelled = await operations.CancelAsync(id, "system-updates", true, stoppingToken.IsCancellationRequested ? "backend-stopping" : "update-cancelled");
                    if (cancelled.Cancellation.State.KnownValue == CancellationState.Deferred)
                        problem = ApiErrors.Create("update.installationFailed", "安装移交已取消", id);
                }
                catch (Exception error) when (error is UpdateSourceException or TrustValidationException or UpdatePlanException or FileTreeException or AssetDownloadException or ActivityBarrierException or OperationException or IOException or UnauthorizedAccessException)
                {
                    var code = error switch
                    {
                        UpdateSourceException source => source.Code, TrustValidationException trust => trust.Code,
                        UpdatePlanException plan => plan.Code, FileTreeException file => file.Code,
                        AssetDownloadException asset => asset.Code, ActivityBarrierException activity => activity.Code,
                        OperationException operation => operation.Code,
                        _ => "update.storageUnavailable"
                    };
                    if (!ApiErrors.Definitions.Any(item => item.Code == code)) code = "update.installationFailed";
                    problem = ApiErrors.Create(code, "更新执行失败", id);
                }
                finally
                {
                    monitorStop.Cancel();
                    await monitor;
                }
                if (transferred)
                {
                    handedOffId = id; installer!.RequestShutdown(); return;
                }
                await operations.FinishUpdateAsync(id, result, problem);
            }
            catch
            {
                await CloseAdmissionAsync();
                throw;
            }
            finally
            {
                await admission.WaitAsync();
                try { if (activeId == id) activeId = null; }
                finally { admission.Release(); }
                SignalSchedule();
            }
        }
    }

    private async Task MonitorCancellationAsync(string id, CancellationTokenSource cancellation, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(token))
                if (operations.Get(id, "system-updates", true).State.KnownValue == OperationState.Cancelled)
                { cancellation.Cancel(); return; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { cancellation.Cancel(); throw; }
    }

    internal async Task<bool> RunScheduledCheckAsync(double jitter, CancellationToken token = default)
    {
        if (!checker.IsConfigured) return false;
        await admission.WaitAsync(token);
        try
        {
            if (!accepting || activeId is not null) return false;
            if (!await policy.TryClaimCheckAsync(jitter, token)) return false;
            await AdmitAsync("system-updates", "automatic-" + Guid.NewGuid().ToString("N"), token);
            return true;
        }
        catch (ActivityBarrierException) { return false; }
        catch (OperationException error) when (error.Code is "resource.busy" or "resource.capacityExceeded") { return false; }
        finally { admission.Release(); }
    }

    private async Task RunScheduleAsync(CancellationToken token)
    {
        if (!checker.IsConfigured) { await Task.Delay(Timeout.InfiniteTimeSpan, token); return; }
        while (!token.IsCancellationRequested)
        {
            await RunScheduledCheckAsync(Random.Shared.NextDouble(), token);
            var settings = policy.Get();
            var next = policy.Schedule().NextAttemptAt;
            var waiting = settings.Policy.AutomaticChecks && next is { } due ? due - clock.GetUtcNow() : Timeout.InfiniteTimeSpan;
            // 已到期但活动或维护阻挡时有界重试；未启用检查时只等待策略通知。
            if (waiting != Timeout.InfiniteTimeSpan && waiting < TimeSpan.FromSeconds(30)) waiting = TimeSpan.FromSeconds(30);
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
            var timer = Task.Delay(waiting, clock, wake.Token);
            var changed = scheduleChanged.WaitAsync(wake.Token);
            await Task.WhenAny(timer, changed);
            wake.Cancel();
            try { await Task.WhenAll(timer, changed); }
            catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
        }
    }

    private void SignalSchedule()
    {
        try { scheduleChanged.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task CloseAdmissionAsync()
    {
        await admission.WaitAsync();
        try { accepting = false; queue.Writer.TryComplete(); }
        finally { admission.Release(); }
    }

    public override async Task StopAsync(CancellationToken token)
    {
        policy.Changed -= SignalSchedule;
        await admission.WaitAsync(token);
        try { accepting = false; queue.Writer.TryComplete(); }
        finally { admission.Release(); }
        await base.StopAsync(token);
        if (ExecuteTask?.IsCompleted == true && activeId is { } id && handedOffId != id)
            await operations.CancelAsync(id, "system-updates", true, "backend-stopping", token);
    }
}
