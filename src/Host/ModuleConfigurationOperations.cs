using System.Threading.Channels;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host;

/// <summary>本地配置执行队列。应用阶段为不可分割区，取消延迟到应用及持久化结束。</summary>
public sealed class ModuleConfigurationOperations(OperationStore operations, ModuleRuntime runtime, ModuleCatalog catalog,
    ActivityBarrier? activities = null) : BackgroundService
{
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly Channel<(string OperationId, string ModuleId, bool Enabled)> queue = Channel.CreateBounded<(string, string, bool)>(
        new BoundedChannelOptions(OperationStore.MaximumActiveOperations) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private bool accepting;

    public Task<ModuleConfiguration> ConfigurationAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var module = FindModule(id);
        return Task.FromResult(operations.Configuration(id, module.EnabledByDefault));
    }

    public async Task<OperationSnapshot> SubmitAsync(string principal, string id, ModuleConfigurationRequest request,
        string? key, string? etag, CancellationToken ct = default)
    {
        var module = FindModule(id);
        // 核心模块（模块定义里声明 Essential）始终启用：停用请求以稳定错误码拒绝，不进入执行队列。
        if (module.Essential && !request.Enabled) throw new OperationException("module.essential", "核心模块始终启用，不能停用");
        await admission.WaitAsync(ct);
        try
        {
            if (!accepting) throw new OperationException("storage.unavailable", "操作执行器正在关闭或等待恢复");
            var result = await operations.AdmitConfigurationAsync(principal, id, module.EnabledByDefault, request, key, etag,
                queue.Reader.Count < OperationStore.MaximumActiveOperations, ct, runtime.ActiveOperations(id));
            if (result.Created && !queue.Writer.TryWrite((result.Snapshot.Id, id, request.Enabled)))
                throw new InvalidOperationException("已受理操作无法进入保留队列");
            return result.Snapshot;
        }
        finally { admission.Release(); }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // 恢复持久配置属于宿主初始化，不重新排队历史 Operation。
        foreach (var configuration in operations.SavedConfigurations())
            // 旧版本保存过的核心模块停用不再生效。
            if (catalog.Registrations.TryGetValue(configuration.Id, out var registration) && (configuration.Enabled || !registration.Definition.Essential))
                await runtime.SetEnabledAsync(configuration.Id, configuration.Enabled, cancellationToken);
        accepting = true;
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            try
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    await operations.CancelAsync(item.OperationId, "owner", true, "backend-stopping");
                    continue;
                }
                using var activity = await EnterActivityAsync(stoppingToken);
                if (await operations.BeginConfigurationAsync(item.OperationId) is null) continue;
                ApiProblem? problem = null;
                var interrupted = false;
                try { await runtime.SetEnabledAsync(item.ModuleId, item.Enabled); }
                catch (ModuleRuntimeException error) { problem = ApiErrors.Create(error.Code, error.Message, item.OperationId,
                    new Dictionary<string, JsonElement> { ["operationIds"] = JsonSerializer.SerializeToElement(error.OperationIds) }); }
                catch (Exception) { problem = ApiErrors.Create("operation.executionFailed", "模块配置应用中断", item.OperationId); interrupted = true; }
                await operations.FinishConfigurationAsync(item.OperationId, item.Enabled, problem, interrupted);
            }
            catch (Exception)
            {
                // 持久化失败后停止新受理；保留未完成记录，由下一次启动标记未知效果。
                await admission.WaitAsync();
                try { accepting = false; queue.Writer.TryComplete(); }
                finally { admission.Release(); }
                return;
            }
        }
    }

    private async Task<IDisposable?> EnterActivityAsync(CancellationToken ct)
    {
        if (activities is null) return null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return activities.EnterActivity("configuration", "module.configuration"); }
            catch (ActivityBarrierException error) when (error.Code == "update.maintenanceActive")
            { await Task.Delay(50, ct); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await admission.WaitAsync(cancellationToken);
        try { accepting = false; queue.Writer.TryComplete(); }
        finally { admission.Release(); }
        await base.StopAsync(cancellationToken);
    }

    private ModuleDefinition FindModule(string id) => catalog.Registrations.TryGetValue(id, out var module)
        ? module.Definition : throw new OperationException(ApiErrors.ModuleNotFound, "模块不存在");
}
