using System.Text.Json;
using System.Runtime.Versioning;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwObservation.Combat;

namespace MabiTool.Host.Game;

public sealed partial class GameObservationService
{
    private readonly Dictionary<string, CaptureInventorySubscription> inventoryDemands = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim catalogJobs = new(1, 1);
    private ObservationInventoryProfile? inventoryProfile;
    private int TotalDemands => demands.Count + combatDemands.Count + inventoryDemands.Count;
    internal ObservationInventoryProfile? InventoryProfile { get { lock (gate) return inventoryProfile; } }

    /// <param name="resident">常驻库存订阅：作为共享抓包的兴趣方随常驻捕获保持，不登记更新活动；维护期间拒绝。</param>
    internal async Task<CaptureInventorySubscription> OpenInventoryAsync(string principal, string observationId,
        InventoryObservationRequest request, CancellationToken ct, bool resident = false)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("inventory.sourceUnavailable", "库存观察组件需要 Windows");
        Authorize(principal);
        InventoryPermission(principal, ApiPermissions.InventoryControl);
        CaptureInventorySubscription? subscription = null;
        await lifecycle.WaitAsync(ct);
        try
        {
            GameSession binding; WorkerSession session; WorkerCall current;
            lock (gate)
            {
                if (worker is null || call is null || state is not ("running" or "starting"))
                    throw new OperationException("observation.channelUnavailable", "请先选择网卡并启动共享游戏观察");
                binding = game ?? throw new OperationException("input.bindingUnavailable", "共享捕获没有游戏绑定");
                if (binding.Id != request.GameSessionId || controls.Bindings.Current?.Session != binding)
                    throw new OperationException("input.bindingChanged", "共享观察绑定已变化");
                if (TotalDemands >= 8 || inventoryDemands.ContainsKey(observationId))
                    throw new OperationException("resource.capacityExceeded", "共享库存观察需求达到上限");
                session = worker; current = call;
                if (!resident) HoldActivity(principal);
                else if (barrier.Snapshot().MaintenanceOwner is not null)
                    throw new OperationException("update.maintenance", "更新维护进行中，结束后自动恢复库存同步");
            }
            if (inventoryProfile is null)
            {
                ct.ThrowIfCancellationRequested();
                var client = new InventoryClientRequest(binding.Id, binding.ProcessId, binding.ProcessStartTime, binding.Region);
                var result = await session.CommandAsync(current, id => new { commandId = id, method = "inventory.prepare", arguments = client }, payload =>
                {
                    if (payload.EnumerateObject().Count() != 2 ||
                        !payload.TryGetProperty("prepared", out _) && !payload.TryGetProperty("code", out _))
                        throw new WorkerException("worker.commandInvalid");
                }, CancellationToken.None, TimeSpan.FromMinutes(1));
                if (result.TryGetProperty("code", out var code))
                    throw new OperationException(code.GetString() is "input.bindingChanged" ? "input.bindingChanged" : "inventory.buildUnsupported", "当前客户端库存布局未完成准备");
                var prepared = result.GetProperty("prepared").Deserialize<InventoryClientPrepared>(ContractJson.Options)
                    ?? throw new WorkerException("worker.commandInvalid");
                ValidateInventoryProfile(prepared.Profile, binding);
                lock (gate) inventoryProfile = prepared.Profile;
            }
            ct.ThrowIfCancellationRequested();
            Authorize(principal); InventoryPermission(principal, ApiPermissions.InventoryControl);
            if (controls.Bindings.Current?.Session != binding) throw new OperationException("input.bindingChanged", "库存准备期间绑定已变化");
            lock (gate)
            {
                var interestId = appliedInterests.Inventory?.Id ?? Guid.NewGuid().ToString("N");
                subscription = new(this, principal, observationId, interestId, request, inventoryProfile!, resident);
                inventoryDemands.Add(observationId, subscription); revision++;
            }
            TraceSubscription("inventory", "added");
            await ConfigureInterestsAsync(ct); Notify(); return subscription;
        }
        catch
        {
            if (subscription is not null) await ReleaseInventoryCoreAsync(subscription, null);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    internal async Task ReleaseInventoryAsync(CaptureInventorySubscription subscription)
    {
        await lifecycle.WaitAsync();
        try { await ReleaseInventoryCoreAsync(subscription, null); }
        finally { lifecycle.Release(); }
    }
    private async Task ReleaseInventoryCoreAsync(CaptureInventorySubscription subscription, Exception? failure)
    {
        lock (gate)
        {
            if (!inventoryDemands.ContainsKey(subscription.Id)) { subscription.Complete(failure); return; }
            subscription.Closing = true;
        }
        if (worker is not null && state is "running" or "starting")
            try { await ConfigureInterestsAsync(CancellationToken.None); }
            catch (Exception error) when (error is WorkerException or OperationException or OperationCanceledException)
            { failure ??= new OperationException("inventory.observationGap", "库存兴趣停止交接未完成"); OnFault(captureId!, "observation.captureFailed"); }
        lock (gate) { inventoryDemands.Remove(subscription.Id); revision++; }
        TraceSubscription("inventory", "removed");
        subscription.Complete(failure);
        if (TotalDemands == 0 && worker is not null && (!resident || failure is not null)) await StopCoreAsync(failure is null ? null : "observation.captureFailed");
        else ReleaseIdleActivity();
        Notify();
    }
    private async Task ReleaseUnauthorizedInventoryAsync()
    {
        CaptureInventorySubscription[] revoked;
        lock (gate) revoked = inventoryDemands.Values.Where(value => !auth.HasPermission(value.Principal, ApiPermissions.GameObserve) ||
            !auth.HasPermission(value.Principal, ApiPermissions.InventoryControl)).ToArray();
        foreach (var value in revoked) await ReleaseInventoryCoreAsync(value, new OperationException("permission.denied", "库存观察授权已撤销"));
    }
    [SupportedOSPlatform("windows")]
    private async Task<bool> AcceptInventoryEventAsync(WorkerFrame frame, CancellationToken ct)
    {
        InventoryCaptureReport? report;
        if (frame.Payload.TryGetProperty("inventoryAsset", out var assetValue))
        {
            if (frame.Payload.EnumerateObject().Count() != 1) throw new WorkerException("worker.frameInvalid");
            var asset = assetValue.Deserialize<InventoryCaptureArtifact>(ContractJson.Options) ?? throw new WorkerException("worker.frameInvalid");
            report = await InventoryWorkerAssets.ReadAsync<InventoryCaptureReport>(Configuration().AssetRoot,
                asset.FileName, asset.Bytes, asset.Sha256, ct);
        }
        else if (frame.Payload.TryGetProperty("inventory", out var payload))
        {
            if (frame.Payload.EnumerateObject().Count() != 1) throw new WorkerException("worker.frameInvalid");
            report = payload.Deserialize<InventoryCaptureReport>(ContractJson.Options);
        }
        else return false;
        if (report is null || report.SchemaVersion != 1 || !WorkerProtocol.IsId(report.InterestId) || report.Frame is not { } data ||
            data.SchemaVersion != 1 || data.Generation.Value == 0 || data.Sequence.Value == 0 || data.Source is null ||
            data.Kind.KnownValue is null || data.Items is null || data.Changes is null || data.Coverage is null || data.Items.Count > 10000 || data.Changes.Count > 10000 ||
            !double.IsFinite(data.MonotonicMs) || data.MonotonicMs < 0 || data.MonotonicMs > Mono() + 200 || Mono() - data.MonotonicMs > 2000 ||
            data.ObservedAt == default || data.ObservedAt.Offset != TimeSpan.Zero)
            throw new WorkerException("worker.frameInvalid");
        CaptureInventorySubscription[] targets;
        lock (gate)
        {
            targets = inventoryDemands.Values.Where(value => value.InterestId == report.InterestId && !value.Closing).ToArray();
            if (targets.Length == 0) return true;
            if (data.GameSessionId != game?.Id || data.BindingRevision?.ToString() != frame.BindingRevision || data.Source != targets[0].Profile.Source)
                throw new WorkerException("worker.frameInvalid");
            RecordInventoryFrame(data);
        }
        if (data.Kind.KnownValue != InventoryFrameKind.Delta)
        {
            CapturePrearmCounters? prearm;
            lock (gate) prearm = captureCounters?.Prearm;
            var fields = new DiagnosticFields().Value("kind", data.Kind.KnownValue?.ToString().ToLowerInvariant()).Value("reason", data.Reason)
                .Count("items", data.Items.Count).Flag("identity", data.Identity is not null);
            if (prearm is not null)
                fields.Count("prearmBuffered", prearm.Buffered.Value).Count("prearmDelivered", prearm.Delivered.Value).Count("prearmExpired", prearm.Expired.Value);
            trace?.Emit(DiagnosticEvents.InventoryFrameState, fields);
        }
        if (trace?.IsEnabled(DiagnosticEvents.InventoryFrame) == true)
            trace.Emit(DiagnosticEvents.InventoryFrame, new DiagnosticFields().Value("kind", data.Kind.KnownValue?.ToString().ToLowerInvariant())
                .Value("reason", data.Reason).Count("items", data.Items.Count).Count("changes", data.Changes.Count));
        foreach (var target in targets) target.Publish(report);
        return true;
    }
    /// <summary>
    /// 进程内诊断：Worker 已校验的库存帧种类、原因与条目数（不含身份与条目内容）。首次完整同步之前，库存读模型没有当前
    /// 工作区，失效原因不进入快照；实机工具据此判断登录数据、身份关联与完整同步各自是否出现。
    /// </summary>
    public InventoryCaptureDiagnostics InventoryDiagnostics()
    {
        lock (gate) return new(inventoryFrames.ToArray(), inventoryFramesTruncated, new Dictionary<string, long>(inventoryFrameCounts, StringComparer.Ordinal));
    }
    private readonly List<InventoryFrameDiagnostic> inventoryFrames = [];
    private readonly Dictionary<string, long> inventoryFrameCounts = new(StringComparer.Ordinal);
    private long inventoryFramesTruncated;
    private InventoryFrameDiagnostic? lastInventoryFrame;
    /// <summary>与战斗诊断相同，在新捕获开始时清零；捕获停止后保留上一轮读数供调试工具读取。</summary>
    private void ResetInventoryDiagnostics()
    {
        lock (gate) { inventoryFrames.Clear(); inventoryFrameCounts.Clear(); inventoryFramesTruncated = 0; lastInventoryFrame = null; }
    }
    private void RecordInventoryFrame(InventoryFrame data)
    {
        var kind = data.Kind.KnownValue?.ToString() ?? "unknown";
        var key = data.Reason is { } reason ? kind + ":" + reason : kind;
        if (!inventoryFrameCounts.ContainsKey(key) && inventoryFrameCounts.Count >= 32) key = "other";
        inventoryFrameCounts[key] = inventoryFrameCounts.GetValueOrDefault(key) + 1;
        lastInventoryFrame = new(kind, data.Reason, data.Items.Count, data.Changes.Count, data.Identity is not null,
            data.Generation.Value, data.Sequence.Value, data.MonotonicMs, Mono());
        if (inventoryFrames.Count >= 256) { inventoryFramesTruncated++; return; }
        inventoryFrames.Add(lastInventoryFrame);
    }
}

/// <param name="Kind">Full、Delta 或 Invalidated。</param>
/// <param name="Reason">Invalidated 帧的原因码，例如 inventory.awaitingIdentity、inventory.identityTimeout。</param>
/// <param name="IdentityPresent">帧是否携带已关联的账户/角色身份；身份值本身不记录。</param>
/// <param name="WorkerMonotonicMs">Worker 帧时点；HostMonotonicMs 为 Host 受理时点，两者同一系统单调时钟。</param>
public sealed record InventoryFrameDiagnostic(string Kind, string? Reason, int Items, int Changes, bool IdentityPresent,
    ulong Generation, ulong Sequence, double WorkerMonotonicMs, double HostMonotonicMs);
public sealed record InventoryCaptureDiagnostics(IReadOnlyList<InventoryFrameDiagnostic> Frames, long FramesTruncated,
    IReadOnlyDictionary<string, long> Counts);

public sealed partial class GameObservationService
{

    internal async Task<InventoryCatalogData> ReadInventoryCatalogAsync(string principal, string gameSessionId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new OperationException("inventory.sourceUnavailable", "客户端目录读取需要 Windows");
        InventoryPermission(principal, ApiPermissions.InventoryEdit);
        var binding = controls.Bindings.Current?.Session ?? throw new OperationException("input.bindingUnavailable", "请先选择游戏窗口");
        if (binding.Id != gameSessionId || binding.Region != "TW") throw new OperationException("input.bindingChanged", "目录读取需要当前台服游戏绑定");
        if (!await catalogJobs.WaitAsync(0, ct)) throw new OperationException("resource.busy", "客户端目录读取正在运行");
        string? root = null;
        try
        {
            var configured = Configuration();
            root = Path.Combine(configured.AssetRoot, "inventory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); LocalDiscoverySecurity.RejectLinks(root);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            await using var session = await WorkerSession.StartAsync(configured with { AssetRoot = root }, _ => { }, deadline.Token);
            var request = new InventoryClientRequest(binding.Id, binding.ProcessId, binding.ProcessStartTime, binding.Region);
            var work = await session.BeginAsync(new("inventory.catalog", JsonSerializer.SerializeToElement(request, ContractJson.Options),
                Guid.NewGuid().ToString("N"), binding.BindingRevision.ToString(), "0"), deadline.Token);
            var drain = DrainAsync(work, deadline.Token);
            var result = await work.Completion.WaitAsync(deadline.Token); await drain;
            if (result.Kind != "result") throw Failure(result);
            if (result.Payload.EnumerateObject().Count() != 1 || !result.Payload.TryGetProperty("catalogAsset", out var value))
                throw new WorkerException("worker.frameInvalid");
            var asset = value.Deserialize<InventoryCatalogArtifact>(ContractJson.Options) ?? throw new WorkerException("worker.frameInvalid");
            var data = await InventoryWorkerAssets.ReadAsync<InventoryCatalogData>(root, asset.FileName, asset.Bytes, asset.Sha256, deadline.Token);
            InventoryPermission(principal, ApiPermissions.InventoryEdit);
            if (controls.Bindings.Current?.Session != binding) throw new OperationException("input.bindingChanged", "目录读取期间游戏绑定已变化");
            return data;
        }
        catch (WorkerException error) { throw new OperationException("inventory.sourceUnavailable", error.Code); }
        finally { if (root is not null) InventoryWorkerAssets.CleanDirectory(root, true); catalogJobs.Release(); }
    }
    private void InventoryPermission(string principal, string permission)
    { if (!auth.HasPermission(principal, permission)) throw new OperationException("permission.denied", "缺少库存授权：" + permission); }
    private static void ValidateInventoryProfile(ObservationInventoryProfile value, GameSession binding)
    {
        static bool Hash(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);
        if (value is null || value.Id is null or { Length: > 64 } || value.Source is null || value.Source.Region != "TW" ||
            value.Source.Provider != "mabiagent-inventory" || value.Source.ProviderVersion != "1.0.0" || !value.FullInventory ||
            !Hash(value.AssemblySha256) || !Hash(value.MetadataSha256) || value.Categories is not { Count: > 0 and <= 128 } ||
            value.Categories.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 128) || value.Categories.Distinct(StringComparer.Ordinal).Count() != value.Categories.Count ||
            value.Source.GameBuild != TwClientFingerprint.GameBuildForInventoryLayout(value.AssemblySha256, value.MetadataSha256) ||
            value.Source.GameBuild != binding.GameBuild)
            throw new OperationException("inventory.buildUnsupported", "库存布局与当前绑定构建不匹配");
    }
}
