using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Host.Files;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;
using MabiTool.Infrastructure.Workers;
using MabiTool.ModuleSdk;
using MabiTool.Modules.Inventory.Adapters;

namespace MabiTool.Host.Inventory;

public sealed class InventoryOperations(OperationStore operations, InventoryHistoryStore history,
    GameObservationService observation, GameControlContext controls, ModuleRuntime modules, ActivityBarrier barrier,
    LocalAuthService auth, FileSelectionRegistry selections, FileTransferStore files, PathService paths,
    SqliteStore database, SystemInfo system, TimeProvider clock, ILogger<InventoryOperations> logger, DiagnosticTrace? trace = null) : BackgroundService
{
    private sealed class Job(string id, AuthSession owner, string kind, GameSession? binding,
        IDisposable activity, IAsyncDisposable demand, Func<Job, CancellationToken, Task<object?>> action)
    {
        internal string Id { get; } = id;
        internal AuthSession Owner { get; } = owner;
        internal string Kind { get; } = kind;
        internal GameSession? Binding { get; } = binding;
        internal IDisposable Activity { get; } = activity;
        internal IAsyncDisposable Demand { get; } = demand;
        internal Func<Job, CancellationToken, Task<object?>> Action { get; } = action;
        internal CancellationTokenSource Stop { get; } = new();
        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Cancel()
        { try { Stop.Cancel(); } catch (ObjectDisposedException) { } }
        internal ulong SourceGeneration;
        internal UInt64String Generation;
        /// <summary>常驻库存同步：随常驻抓包由宿主自动开始与停止。</summary>
        internal bool Resident { get; init; }
    }
    /// <summary>常驻库存同步失败后的重试间隔。</summary>
    internal static TimeSpan ResidentRetry { get; set; } = TimeSpan.FromSeconds(10);
    private long residentRetryAt;
    private sealed record PreviewReceipt(string Owner, string Key, string Hash, InventoryImportPreview Preview);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly ConcurrentDictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PreviewReceipt> previews = new(StringComparer.Ordinal);
    private ulong generation = history.Workspace()?.Generation.Value ?? 0;
    private volatile bool accepting;

    public InventoryCapabilities Capabilities()
    {
        var configured = observation.InventoryProfile;
        var simulation = controls.Simulation;
        var source = simulation ? ReplaySource(controls.Bindings.Recent?.Session.GameBuild) : configured?.Source;
        var categories = simulation ? new[] { "Consumable", "Equipment", "EquipmentTool" } : configured?.Categories ?? [];
        var installed = observation.Runtime().Installed;
        return new(simulation, [
            new("inventory.full", simulation || configured?.FullInventory == true ? CapabilityStatus.Available : installed ? CapabilityStatus.Unknown : CapabilityStatus.Unavailable,
                source, simulation || configured?.FullInventory == true ? null : installed ? "inventory.layoutNotPrepared" : "inventory.sourceUnavailable", categories, configured?.AssemblySha256, configured?.MetadataSha256),
            new("inventory.incremental", simulation || configured?.IncrementalInventory == true ? CapabilityStatus.Available : installed ? CapabilityStatus.Unknown : CapabilityStatus.Unavailable,
                source, simulation || configured?.IncrementalInventory == true ? null : "inventory.buildUnsupported", categories, configured?.AssemblySha256, configured?.MetadataSha256),
            new("inventory.catalog", history.CatalogSummary().Id is not null ? CapabilityStatus.Available : installed ? catalogFailure is null ? CapabilityStatus.Unknown : CapabilityStatus.Unavailable : CapabilityStatus.Unavailable,
                source, history.CatalogSummary().Id is not null ? null : !installed ? "inventory.sourceUnavailable" : catalogRunning ? "inventory.catalogExtracting" : catalogFailure ?? "inventory.catalogNotLoaded",
                history.CatalogSummary().Categories.Select(value => value.Category).ToArray())
        ]);
    }
    internal (int Observing, int Other, int Previews, bool Accepting) DiagnosticState() =>
        (jobs.Values.Count(job => job.Kind == "inventory.observe"), jobs.Values.Count(job => job.Kind != "inventory.observe"), previews.Count, accepting);
    public static string ETag(string epoch, UInt64String revision) => $"\"inventory-{epoch}-{revision}\"";
    public static string CatalogETag(string epoch, UInt64String revision) => $"\"inventory-catalog-{epoch}-{revision}\"";
    public UInt64String Condition(string? etag, bool catalog = false)
    {
        if (etag is null) throw new OperationException("revision.required", "库存编辑需要 If-Match");
        var prefix = catalog ? "inventory-catalog-" : "inventory-";
        if (!Regex.IsMatch(etag, "\\A\"" + prefix + "[0-9a-f]{32}-(0|[1-9][0-9]{0,19})\"\\z"))
            throw new OperationException("request.invalid", "需要单个强库存 ETag");
        if (etag.Substring(prefix.Length + 1, 32) != system.InstanceId)
            throw new OperationException("inventory.planStale", "库存 ETag 属于此前宿主");
        try { return UInt64String.Parse(etag[(prefix.Length + 34)..^1]); }
        catch (Exception error) when (error is FormatException or OverflowException)
        { throw new OperationException("request.invalid", "库存修订超过 64 位范围"); }
    }

    public Task<OperationSnapshot> SubmitObservationAsync(AuthSession session, InventoryObservationRequest request, string? key, CancellationToken ct)
    {
        if (operations.InventoryRetry(session.ClientId, InventoryApi.StartObservation.Path, request, key) is { } retry) return Task.FromResult(retry);
        if (string.IsNullOrWhiteSpace(request.GameSessionId) || request.GameSessionId.Length > 128 || request.ReplayIntervalMs is < 10 or > 10000 ||
            request.ReplayScenarioId is not (null or "inventory-full-cycle")) throw new OperationException("request.invalid", "库存观察请求格式无效");
        if (!controls.Simulation && request.ReplayScenarioId is not null) throw new OperationException("inventory.sourceUnavailable", "回放场景需要模拟宿主");
        var binding = controls.Bindings.Current?.Session ?? throw new OperationException("input.bindingUnavailable", "请先选择游戏窗口");
        if (binding.Id != request.GameSessionId) throw new OperationException("input.bindingChanged", "库存请求与当前游戏绑定不同");
        return SubmitAsync(session, InventoryApi.StartObservation.Path, "inventory.observe", request, key, null, binding,
            (job, token) => ObserveAsync(job, request, token), ct);
    }
    /// <summary>
    /// 常驻库存同步：常驻共享抓包运行、绑定与抓包同一游戏会话、不在更新维护时，宿主以所有者身份保持一个库存观察，
    /// 作为共享抓包的兴趣方接收库存帧；条件不满足时停止，恢复后自动重新开始。已有手动观察或历史导入时不另开。
    /// </summary>
    private async Task ResidentTickAsync(CancellationToken ct)
    {
        if (!accepting || controls.Simulation) return;
        var binding = controls.Bindings.Recent?.Session;
        var capture = observation.Snapshot();
        var resident = jobs.Values.FirstOrDefault(job => job.Resident);
        var eligible = binding is not null && observation.Resident && capture.State == "running" && capture.GameSessionId == binding.Id &&
            barrier.Snapshot().MaintenanceOwner is null;
        if (resident is not null)
        {
            if (!eligible || resident.Binding?.Id != binding!.Id) resident.Cancel();
            return;
        }
        if (!eligible || jobs.Values.Any(job => job.Kind is "inventory.observe" or "inventory.import")) return;
        if (clock.GetTimestamp() < Interlocked.Read(ref residentRetryAt)) return;
        var request = new InventoryObservationRequest(binding!.Id);
        try
        {
            await SubmitAsync(auth.OwnerGrant().Session, InventoryApi.StartObservation.Path, "inventory.observe", request,
                "inventory-resident-" + Guid.NewGuid().ToString("N"), null, binding, (job, token) => ObserveAsync(job, request, token), ct, resident: true);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        { DelayResident(); }
    }
    private string? catalogBinding;
    private volatile string? catalogFailure;
    private volatile bool catalogRunning;
    /// <summary>
    /// 道具名称目录自动提取：常驻抓包绑定台服游戏后，每个游戏会话（进程启动一次）由宿主以所有者身份从该客户端提取一次名称目录，
    /// 索引指纹（<c>IndexSha256</c>）与已保存目录相同时不写入；客户端更新后游戏重启即是新会话，目录随之重建。
    /// 提取在独立计算 Worker 中进行，结束即退出；失败原因记入 <c>inventory.catalog</c> 能力，同一会话不自动重试。
    /// </summary>
    private async Task ResidentCatalogTickAsync(CancellationToken ct)
    {
        if (!accepting || controls.Simulation) return;
        var binding = controls.Bindings.Current?.Session;
        var capture = observation.Snapshot();
        if (binding is null || binding.Region != "TW" || binding.Id == catalogBinding || !observation.Resident || capture.State != "running" ||
            capture.GameSessionId != binding.Id || barrier.Snapshot().MaintenanceOwner is not null || jobs.Values.Any(job => job.Kind is "inventory.catalog.refresh" or "inventory.catalog.import"))
            return;
        catalogBinding = binding.Id; catalogFailure = null; catalogRunning = true;
        var request = new InventoryCatalogRefreshRequest(binding.Id);
        try
        {
            await SubmitAsync(auth.OwnerGrant().Session, InventoryApi.RefreshCatalog.Path, "inventory.catalog.refresh", request,
                "inventory-catalog-auto-" + Guid.NewGuid().ToString("N"), null, binding, async (job, token) =>
                {
                    try
                    {
                        var data = await observation.ReadInventoryCatalogAsync(job.Owner.ClientId, binding.Id, token);
                        token.ThrowIfCancellationRequested();
                        var saved = history.CatalogSummary();
                        if (saved.Id == data.Id && saved.IndexSha256 == data.IndexSha256) return saved;
                        if (!await operations.InventoryApplyingAsync(job.Id)) throw new OperationCanceledException(token);
                        await history.ReplaceCatalogAsync(data, null, CancellationToken.None);
                        return history.CatalogSummary();
                    }
                    catch (OperationException error) { catalogFailure = error.Code; throw; }
                    catch (OperationCanceledException) { catalogBinding = null; throw; }
                    catch (Exception) { catalogFailure = "inventory.sourceUnavailable"; throw; }
                    finally { catalogRunning = false; }
                }, ct, resident: true);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        { catalogRunning = false; catalogFailure = error is OperationException value ? value.Code : "inventory.sourceUnavailable"; }
    }
    private void DelayResident() =>
        Interlocked.Exchange(ref residentRetryAt, clock.GetTimestamp() + (long)(ResidentRetry.TotalSeconds * clock.TimestampFrequency));
    public Task<OperationSnapshot> SubmitCatalogRefreshAsync(AuthSession session, InventoryCatalogRefreshRequest request, string? key, string? etag, CancellationToken ct)
    {
        if (operations.InventoryRetry(session.ClientId, InventoryApi.RefreshCatalog.Path, request, key, etag) is { } retry) return Task.FromResult(retry);
        var revision = Condition(etag, true);
        if (string.IsNullOrWhiteSpace(request.GameSessionId) || controls.Bindings.Current?.Session.Id != request.GameSessionId)
            throw new OperationException("input.bindingChanged", "目录更新需要当前游戏绑定");
        return SubmitAsync(session, InventoryApi.RefreshCatalog.Path, "inventory.catalog.refresh", request, key, etag, controls.Bindings.Current?.Session,
            async (job, token) =>
            {
                var data = await observation.ReadInventoryCatalogAsync(job.Owner.ClientId, request.GameSessionId, token);
                token.ThrowIfCancellationRequested();
                if (!await operations.InventoryApplyingAsync(job.Id)) throw new OperationCanceledException(token);
                await history.ReplaceCatalogAsync(data, revision, CancellationToken.None);
                return history.CatalogSummary();
            }, ct);
    }
    public Task<OperationSnapshot> SubmitCatalogImportAsync(AuthSession session, InventoryCatalogImportRequest request, string? key, string? etag, CancellationToken ct)
    {
        if (operations.InventoryRetry(session.ClientId, InventoryApi.ImportCatalog.Path, request, key, etag) is { } retry) return Task.FromResult(retry);
        var revision = Condition(etag, true); _ = selections.Get(request.FileSelectionId, session);
        return SubmitAsync(session, InventoryApi.ImportCatalog.Path, "inventory.catalog.import", request, key, etag, null,
            async (job, token) =>
            {
                var data = await selections.ReadAsync(request.FileSelectionId, session, async (selected, stream) =>
                {
                    if (selected.SizeBytes?.Value > 32 * 1024 * 1024) throw new OperationException("storage.quotaExceeded", "名称目录超过 32 MiB");
                    return await JsonSerializer.DeserializeAsync<InventoryCatalogData>(stream, ContractJson.Options, token)
                        ?? throw new OperationException("inventory.catalogInvalid", "名称目录内容为空");
                }, token);
                token.ThrowIfCancellationRequested();
                if (!await operations.InventoryApplyingAsync(job.Id)) throw new OperationCanceledException(token);
                await history.ReplaceCatalogAsync(data, revision, CancellationToken.None);
                return history.CatalogSummary();
            }, ct);
    }
    public async Task<InventoryImportPreview> PreviewImportAsync(AuthSession session, InventoryImportRequest request, string? key, CancellationToken ct)
    {
        Key(key);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options)));
        await admission.WaitAsync(ct);
        try
        {
            foreach (var id in previews.Where(item => clock.GetUtcNow() - item.Value.Preview.CreatedAt >= TimeSpan.FromMinutes(15)).Select(item => item.Key).ToArray()) previews.TryRemove(id, out _);
            if (previews.Values.FirstOrDefault(item => item.Owner == session.ClientId && item.Key == key) is { } previous)
                return previous.Hash == hash ? previous.Preview : throw new OperationException("idempotency.conflict", "幂等键已用于其他库存导入预览");
            if (!accepting || previews.Count >= 16) throw new OperationException("resource.capacityExceeded", "库存导入预览达到上限");
            await using var demand = await modules.AcquireAsync("inventory", "inventory-import-preview", ct);
            using var activity = barrier.EnterActivity(session.ClientId, "inventory.import.preview");
            var selectedFiles = await selections.ReadAsync(request.FileSelectionId, session, async (selected, stream) =>
            {
                if (selected.SizeBytes?.Value > 20 * 1024 * 1024) throw new OperationException("storage.quotaExceeded", "库存导入超过 20 MiB");
                if (selected.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var package = await FilePackageReader.ReadAsync(stream, 20 * 1024 * 1024, ct);
                    return package.Select(file => new InventoryImportFile(file.Name, Utf8.GetString(Convert.FromBase64String(file.ContentBase64)))).ToArray();
                }
                using var content = new MemoryStream(); await stream.CopyToAsync(content, ct);
                return new[] { new InventoryImportFile(selected.Name, Utf8.GetString(content.ToArray())) };
            }, ct);
            var preview = history.PreviewImport(selectedFiles, request.AccountId);
            previews.TryAdd(preview.Id, new(session.ClientId, key!, hash, preview)); return preview;
        }
        catch (DecoderFallbackException) { throw new OperationException("inventory.importInvalid", "库存导入文件需要 UTF-8"); }
        finally { admission.Release(); }
    }
    public InventoryImportPreview ImportPreview(string id, AuthSession session)
    {
        lock (previews)
        {
            if (!previews.TryGetValue(id, out var value) || value.Owner != session.ClientId && !session.Owner)
                throw new OperationException("resource.notFound", "库存导入预览不存在");
            return history.GetPreview(id);
        }
    }
    public Task<OperationSnapshot> SubmitImportAsync(string id, AuthSession session, InventoryImportCommitRequest request, string? key, string? etag, CancellationToken ct)
    {
        var route = InventoryApi.CommitImport.Path.Replace("{id}", id);
        if (operations.InventoryRetry(session.ClientId, route, request, key, etag) is { } retry) return Task.FromResult(retry);
        var revision = Condition(etag);
        _ = ImportPreview(id, session);
        return SubmitAsync(session, route, "inventory.import", request, key, etag, null, async (job, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!await operations.InventoryApplyingAsync(job.Id)) throw new OperationCanceledException(token);
            var committed = await history.CommitImportAsync(id, request, revision, CancellationToken.None);
            return new InventoryImportReceipt(id, committed.Revision, committed.Items, committed.Associations, committed.Accounts.Count);
        }, ct);
    }
    public Task<OperationSnapshot> SubmitExportAsync(AuthSession session, InventoryExportRequest request, string? key, CancellationToken ct) =>
        SubmitAsync(session, InventoryApi.Export.Path, "inventory.export", request, key, null, null,
            async (job, token) => await ExportAsync(job, request, token), ct);

    private async Task<OperationSnapshot> SubmitAsync(AuthSession session, string route, string kind, object request, string? key, string? condition,
        GameSession? binding, Func<Job, CancellationToken, Task<object?>> action, CancellationToken ct, bool resident = false)
    {
        await admission.WaitAsync(ct);
        try
        {
            if (operations.InventoryRetry(session.ClientId, route, request, key, condition) is { } retry) return retry;
            if (!accepting) throw new OperationException("inventory.sourceUnavailable", "库存执行器正在关闭");
            var residentJob = jobs.Values.FirstOrDefault(job => job.Resident);
            // resident 为真：宿主自动发起（常驻库存同步、名称目录自动提取），不登记更新活动；只有常驻库存同步标为 Resident。
            // 常驻同步已在接收同一绑定的库存帧：手动开始直接返回该观察，不再报占用。
            if (!resident && kind == "inventory.observe" && residentJob is not null && !residentJob.Stop.IsCancellationRequested &&
                residentJob.Binding?.Id == binding?.Id)
                return operations.Get(residentJob.Id, residentJob.Owner.ClientId, true);
            // 历史导入与库存观察互斥：常驻同步先停止，导入结束后由常驻循环重新开始。
            if (kind == "inventory.import" && residentJob is not null)
            { residentJob.Cancel(); await residentJob.Done.Task.WaitAsync(ct); }
            if (kind == "inventory.observe" && jobs.Values.Any(job => job.Kind is "inventory.observe" or "inventory.import") ||
                kind == "inventory.import" && jobs.Values.Any(job => job.Kind == "inventory.observe"))
                throw new OperationException("resource.busy", "库存当前有观察任务，请先停止再导入历史");
            IAsyncDisposable? demand = await modules.AcquireAsync("inventory", "inventory-admission-" + Guid.NewGuid().ToString("N"), ct);
            IDisposable? activity = null; OperationAdmission? accepted = null;
            try
            {
                activity = !resident ? barrier.EnterActivity(session.ClientId, kind)
                    : barrier.Snapshot().MaintenanceOwner is null ? new MabiTool.Host.Combat.ResidentActivity()
                    : throw new OperationException("update.maintenance", "更新维护进行中，结束后自动恢复库存同步");
                accepted = await operations.AdmitInventoryAsync(session.ClientId, route, kind, request, key, jobs.Count < 16, condition, ct);
                if (!accepted.Created) return accepted.Snapshot;
                var job = new Job(accepted.Snapshot.Id, session, kind, binding, activity, demand, action) { Resident = resident && kind == "inventory.observe" };
                jobs[job.Id] = job; activity = null; demand = null;
                _ = Task.Run(() => RunAsync(job), CancellationToken.None);
                return accepted.Snapshot;
            }
            catch
            {
                if (accepted?.Created == true && !jobs.ContainsKey(accepted.Snapshot.Id))
                    await operations.FinishInventoryAsync(accepted.Snapshot.Id, problem: ApiErrors.Create("inventory.sourceUnavailable", "库存任务未能排队", accepted.Snapshot.Id));
                throw;
            }
            finally { activity?.Dispose(); if (demand is not null) await demand.DisposeAsync(); }
        }
        finally { admission.Release(); }
    }
    private async Task RunAsync(Job job)
    {
        object? result = null;
        FileExportArtifact? export = null;
        ApiProblem? problem = null;
        var cancelled = false;
        var interrupted = false;
        Exception? failure = null;
        try
        {
            if (await operations.BeginInventoryAsync(job.Id) is null)
                cancelled = true;
            else
            {
                result = await job.Action(job, job.Stop.Token);
                export = result as FileExportArtifact;
            }
        }
        catch (OperationCanceledException)
        { cancelled = true; }
        catch (Exception error)
        {
            var code = error switch { OperationException value => value.Code, WorkerException => "inventory.sourceUnavailable", _ => "inventory.sourceUnavailable" };
            if (!ApiErrors.Definitions.Any(value => value.Code == code)) code = "inventory.sourceUnavailable";
            problem = ApiErrors.Create(code, "库存任务未完成", job.Id);
            interrupted = job.Kind == "inventory.observe" && job.SourceGeneration != 0;
            if (job.Resident) DelayResident();
        }
        try
        {
            try
            {
                if (job.Kind == "inventory.observe") await history.InvalidateAsync("inventory.observationStopped");
            }
            finally
            {
                try { await job.Demand.DisposeAsync(); }
                finally { job.Activity.Dispose(); }
            }
        }
        catch (Exception error)
        {
            accepting = false;
            failure = error;
            problem = ApiErrors.Create("inventory.sourceUnavailable", "库存持久化或资源释放未完成", job.Id);
            interrupted = true;
        }
        try
        {
            if (!await operations.FinishInventoryAsync(job.Id, result, problem, interrupted, cancelled) && export is not null)
                await files.DiscardExportAsync(export);
        }
        catch (Exception error) { accepting = false; failure ??= error; }
        finally
        {
            jobs.TryRemove(job.Id, out _); job.Stop.Dispose();
            if (failure is null) job.Done.TrySetResult();
            else
            {
                logger.LogError("库存操作 {OperationId} 的持久化或释放失败，已停止受理新任务", job.Id);
                job.Done.TrySetException(failure);
                _ = job.Done.Task.Exception;
            }
        }
    }
    /// <summary>完整同步帧写入并成为当前工作区时记录 info 事件，只含条目数与代次。</summary>
    private void TraceSync(InventoryFrame frame, InventoryWorkspace? value)
    {
        if (frame.Kind.KnownValue == InventoryFrameKind.Full && value?.Current == true)
            trace?.Emit(DiagnosticEvents.InventorySynced, new DiagnosticFields().Count("items", frame.Items.Count).Count("generation", frame.Generation.Value));
    }
    private async Task<object?> ObserveAsync(Job job, InventoryObservationRequest request, CancellationToken ct)
    {
        await history.InvalidateAsync("inventory.awaitingFull", ct);
        CaptureInventorySubscription? subscription = null;
        try
        {
            await operations.UpdateInventoryObservationAsync(job.Id, Status(job, null));
            if (controls.Simulation)
            {
                foreach (var frame in ReplayFrames(job.Binding!))
                {
                    ct.ThrowIfCancellationRequested();
                    var value = await history.ApplyFrameAsync(Normalize(job, frame), ct);
                    TraceSync(frame, value);
                    await operations.UpdateInventoryObservationAsync(job.Id, Status(job, value), ct);
                    await Task.Delay(TimeSpan.FromMilliseconds(request.ReplayIntervalMs), clock, ct);
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            else
            {
                subscription = await observation.OpenInventoryAsync(job.Owner.ClientId, job.Id, request, ct, job.Resident);
                await foreach (var frame in subscription.ReadAsync(ct))
                {
                    if (!GameObservationService.SameGameSession(controls.Bindings.Current?.Session, job.Binding)) throw new OperationException("input.bindingChanged", "库存观察的游戏会话已变化");
                    var value = await ApplyAsync(job, frame, ct);
                    TraceSync(frame, value);
                    await operations.UpdateInventoryObservationAsync(job.Id, Status(job, value), ct);
                }
            }
            return Status(job, history.Workspace());
        }
        finally { if (subscription is not null) await subscription.DisposeAsync(); }
    }
    /// <summary>
    /// 读模型拒绝的帧记录 warning 诊断（问题码与校验分支）。帧校验拒绝（带校验分支，读模型已按原因使当前工作区失效）只丢弃该帧，
    /// 观察继续，下一次完整同步恢复；其余失败（持久化、预算）照常结束本次观察。
    /// </summary>
    private async Task<InventoryWorkspace?> ApplyAsync(Job job, InventoryFrame frame, CancellationToken ct)
    {
        try { return await history.ApplyFrameAsync(Normalize(job, frame), ct); }
        catch (OperationException error)
        {
            var branch = error.Data[InventoryHistoryStore.RejectionBranch] as string;
            trace?.Emit(DiagnosticEvents.InventoryFrameRejected, new DiagnosticFields().Value("kind", frame.Kind.KnownValue?.ToString().ToLowerInvariant())
                .Value("code", error.Code).Value("branch", branch)
                .Count("items", frame.Items?.Count ?? 0).Count("changes", frame.Changes?.Count ?? 0));
            if (branch is null) throw;
            return history.Workspace();
        }
    }
    private InventoryFrame Normalize(Job job, InventoryFrame frame)
    {
        if (job.Generation.Value == 0 || job.SourceGeneration != frame.Generation.Value)
        {
            job.SourceGeneration = frame.Generation.Value; job.Generation = new(checked(++generation));
        }
        return frame with { Generation = job.Generation };
    }
    private InventoryObservationStatus Status(Job job, InventoryWorkspace? value) => new(job.Id, job.Owner.ClientId,
        job.Binding!.Id, value?.Current == true ? "observing" : "awaiting-full", value?.Revision ?? history.Revision,
        job.Generation, operations.Get(job.Id, job.Owner.ClientId, job.Owner.Owner).Timestamps.StartedAt ?? clock.GetUtcNow(),
        value?.LastObservedAt, value?.Quality ?? ObservationQuality.Unknown, value?.Reasons ?? ["inventory.awaitingFull"]);
    private async Task<FileExportArtifact> ExportAsync(Job job, InventoryExportRequest request, CancellationToken ct)
    {
        var content = history.Export(request);
        if (content.Sum(file => (long)Utf8.GetByteCount(file.Content)) > 20 * 1024 * 1024)
            throw new OperationException("storage.quotaExceeded", "库存导出超过 20 MiB");
        var directory = paths.Resolve("temp", "inventory-exports"); Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N"); var path = Path.Combine(directory, id + ".zip");
        await using var tracked = await database.TemporaryFiles.TrackAsync("inventory", path, ct);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var file in content)
                {
                    ct.ThrowIfCancellationRequested();
                    await using var entry = archive.CreateEntry(FileNames.Validate(file.Name), CompressionLevel.Fastest).Open();
                    await entry.WriteAsync(Utf8.GetBytes(file.Content), ct);
                }
            await output.FlushAsync(ct); output.Position = 0;
            ct.ThrowIfCancellationRequested();
            return await files.PublishExportAsync(job.Owner.ClientId, new(id, "inventory-export.zip", [], ["inventory"], clock.GetUtcNow(), job.Id), output, ct);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    public async Task<OperationSnapshot> CancelAsync(string id, AuthSession session, string? reason, CancellationToken ct)
    {
        var operation = operations.Get(id, session.ClientId, session.Owner);
        if (!operation.Kind.StartsWith("inventory.", StringComparison.Ordinal)) throw new OperationException("resource.notFound", "库存操作不存在");
        var result = await operations.CancelAsync(id, session.ClientId, session.Owner, reason, ct);
        if (jobs.TryGetValue(id, out var job))
        {
            if (result.Cancellation.State.KnownValue != CancellationState.Deferred) job.Cancel();
            await job.Done.Task.WaitAsync(ct); return operations.Get(id, session.ClientId, session.Owner);
        }
        return result;
    }
    public override Task StartAsync(CancellationToken ct) { accepting = true; return base.StartAsync(ct); }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200), clock);
        while (await timer.WaitForNextTickAsync(ct))
        {
            foreach (var job in jobs.Values)
            {
                var permission = job.Kind == "inventory.observe" ? ApiPermissions.InventoryControl : ApiPermissions.InventoryEdit;
                if (!auth.HasPermission(job.Owner.ClientId, permission) || job.Binding is not null && !GameObservationService.SameGameSession(controls.Bindings.Current?.Session, job.Binding))
                    job.Cancel();
            }
            await ResidentTickAsync(ct);
            await ResidentCatalogTickAsync(ct);
        }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try { accepting = false; foreach (var job in jobs.Values) job.Cancel(); }
        finally { admission.Release(); }
        await Task.WhenAll(jobs.Values.Select(job => job.Done.Task)).WaitAsync(ct);
        await base.StopAsync(ct);
    }
    private static void Key(string? key)
    {
        if (key is null) throw new OperationException("idempotency.required", "库存导入预览需要 Idempotency-Key");
        if (!Regex.IsMatch(key, @"\A[A-Za-z0-9._-]{8,128}\z")) throw new OperationException("request.invalid", "库存幂等键无效");
    }
    private static ObservationSource ReplaySource(string? build) => new("mabiagent-inventory-replay", "1.0.0", "TW", build);
    private IEnumerable<InventoryFrame> ReplayFrames(GameSession binding)
    {
        var identity = new InventoryIdentity("account-replay", "character-replay", "回放角色", "回放角色");
        InventoryItem Item(string category, string key, UInt64String? instance, ulong quantity) => new(category, InventoryContainer.Backpack,
            key, new(1001), instance, new(quantity), JsonSerializer.SerializeToElement(new { fixture = "inventory-full-cycle" }), category);
        var items = new[] { Item("Consumable", "stack-1001", null, 9007199254740993), Item("Equipment", "instance-1", new(9007199254740995), 1),
            Item("EquipmentTool", "instance-2", new(18446744073709551000), 1) };
        var coverage = items.SelectMany(item => Enum.GetValues<InventoryContainer>().Select(container => new InventoryCoverage(item.Category, container, true))).ToArray();
        InventoryFrame Frame(ulong gen, ulong sequence, InventoryFrameKind kind, IReadOnlyList<InventoryItem> full, IReadOnlyList<InventoryDelta> changes, string? reason = null) =>
            new(1, ReplaySource(binding.GameBuild), binding.Id, new(gen), new(sequence), clock.GetUtcNow(),
                clock.GetTimestamp() * (1000d / clock.TimestampFrequency), kind, identity, full, changes,
                kind == InventoryFrameKind.Full ? coverage : [], new(9007199254740997), reason, BindingRevision: binding.BindingRevision);
        yield return Frame(1, 1, InventoryFrameKind.Full, items, []);
        yield return Frame(1, 2, InventoryFrameKind.Delta, [], [new(InventoryDeltaKind.Upsert, items[0] with { Quantity = new(9007199254740994) })]);
        yield return Frame(1, 3, InventoryFrameKind.Invalidated, [], [], "inventory.replayGap");
        yield return Frame(2, 1, InventoryFrameKind.Full, items, []);
    }
}
