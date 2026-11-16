using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Infrastructure;

namespace MabiTool.ObservationDebugTool;

public sealed record InventoryScenarioOptions(int PreparationSeconds = 600, int RunSeconds = 90, int SetupSeconds = 120,
    int CatalogSeconds = 150, int InventoryStartSeconds = 90, int VerificationSeconds = 900);

/// <summary>
/// 库存实采验收（mabiagent-inventory-module 5.3）：读取客户端名称目录后启动共享捕获和库存观察，等待重新登录产生的
/// 完整同步，再观察 90 秒增量；停止库存时核对共享捕获未被连带关闭，最后汇总各类别数量供操作者对照游戏核对。
/// </summary>
public sealed class InventoryScenario(IGameEnvironment environment, InventoryScenarioOptions? options = null) : IDebugScenario
{
    private readonly InventoryScenarioOptions settings = options ?? new();
    public static readonly IReadOnlyList<string> VerificationOptions = ["一致", "有差异", "未核对"];

    public ScenarioPlan Plan => new("inventory", "库存：全部类别数量、装备/乐器与名称", "mabiagent-inventory-module 5.3",
        "native-host-inventory-acceptance", "inventory-native-acceptance", settings.PreparationSeconds, settings.RunSeconds,
        [
            $"点击“开始”即开始观察网络。工具先读取客户端名称目录（约 1 分钟，不观察网络），再启动库存观察；准备阶段最长 {settings.PreparationSeconds / 60} 分钟。",
            "面板显示“请重新登录角色”后：返回角色选择画面，再选择同一角色进入游戏。完整库存只在登录时下发。",
            $"取得完整同步后进入 {settings.RunSeconds} 秒增量观察：使用或拆分一个可堆叠道具、装备或卸下一件装备、切换一次乐器；方便时在仓库存取一件物品。",
            "结束后工具停止库存观察并释放捕获，面板列出各类别数量、装备数和乐器清单。",
            "打开游戏内背包和仓库对照面板汇总，点击“一致”“有差异”或“未核对”。",
        ]);

    public async Task<ScenarioResult> RunAsync(ScenarioContext context)
    {
        var host = ObservationHost.Create(environment, context, storage: true);
        var preparationDeadline = context.DeadlineAfter(settings.PreparationSeconds);
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(context.Stopped);
        preparation.CancelAfter(TimeSpan.FromSeconds(settings.PreparationSeconds));
        string? failure = null, failureDetail = null, operationId = null, finalState = null, finalProblem = null;
        var stage = "启动中";
        var instruction = "正在启动 Host、绑定游戏窗口并读取客户端名称目录。";
        context.Show(() => new(stage, instruction, stage == "增量观察" ? null : context.Remaining(preparationDeadline)));
        InventoryCatalogSummary? catalog = null;
        InventoryCapabilities? capabilities = null;
        double? fullSyncMs = null, catalogMs = null;
        var revisions = new HashSet<string>(StringComparer.Ordinal);
        var invalidations = new Dictionary<string, long>(StringComparer.Ordinal);
        var loginSignals = new Dictionary<string, double>(StringComparer.Ordinal);
        long currentLosses = 0;
        int? pidBeforeStop = null, pidAfterStop = null, subscribersBeforeStop = null, subscribersAfterStop = null;
        string? captureStateAfterStop = null;
        InventoryWorkspace? workspace = null;
        List<InventoryItemView> items = [];
        InventoryCaptureDiagnostics? inventoryDiagnostics = null;
        List<MusicOwnedInstrument> instruments = [];
        MusicOwnedInstrumentPage? instrumentPage = null;
        try
        {
            using (var setup = CancellationTokenSource.CreateLinkedTokenSource(preparation.Token))
            {
                setup.CancelAfter(TimeSpan.FromSeconds(settings.SetupSeconds));
                await host.StartAsync(setup.Token);
            }
            var sdk = host.Client();
            var session = host.Game!.Session;
            host.Stage = "inventory.catalog";
            stage = "读取名称目录"; instruction = "正在读取客户端名称目录（只读本机文件，不观察网络）。请稍候。";
            string? catalogEtag = null;
            await ObservationHost.RetryAsync(() => sdk.GetInventoryCatalogAsync(preparation.Token, metadata => catalogEtag = metadata.ETag), preparation.Token);
            using (var catalogLimit = CancellationTokenSource.CreateLinkedTokenSource(preparation.Token))
            {
                catalogLimit.CancelAfter(TimeSpan.FromSeconds(settings.CatalogSeconds));
                var catalogKey = "debug-catalog-" + Guid.NewGuid().ToString("N");
                var refresh = await ObservationHost.RetryAsync(() => sdk.RefreshInventoryCatalogAsync(new(session.Id), catalogKey, catalogEtag!, catalogLimit.Token), catalogLimit.Token);
                var done = await host.WaitOperationAsync(() => sdk.GetOperationAsync(refresh.Id, catalogLimit.Token),
                    value => SqliteStore.IsTerminal(value.State.KnownValue), catalogLimit.Token);
                if (done.State.KnownValue != OperationState.Succeeded) throw new ProbeException(done.Problem?.Code ?? "inventory.catalogFailed");
            }
            catalog = await ObservationHost.RetryAsync(() => sdk.GetInventoryCatalogAsync(preparation.Token), preparation.Token);
            catalogMs = context.ElapsedMs; context.Mark("inventory.catalogReady");
            stage = "启动观察"; instruction = "正在启动共享捕获与库存观察。";
            await host.StartCaptureAsync(preparation.Token);
            context.Mark("capture.running");
            host.Stage = "inventory.start";
            var before = host.Observations!.Runtime().SubscriberCount;
            var inventoryKey = "debug-inventory-" + Guid.NewGuid().ToString("N");
            var started = await ObservationHost.RetryAsync(() => sdk.StartInventoryObservationAsync(new(session.Id), inventoryKey, preparation.Token), preparation.Token);
            operationId = started.Id;
            var startLimit = context.Clock.GetTimestamp() + settings.InventoryStartSeconds * context.Clock.TimestampFrequency;
            while (host.Observations.Runtime().SubscriberCount <= before)
            {
                await EnsureRunningAsync(sdk, operationId, preparation.Token);
                if (context.Clock.GetTimestamp() >= startLimit) throw new ProbeException("inventory.startTimedOut");
                await Task.Delay(200, preparation.Token);
            }
            capabilities = await ObservationHost.RetryAsync(() => sdk.GetInventoryCapabilitiesAsync(preparation.Token), preparation.Token);
            context.Mark("inventory.observing");
            host.Stage = "inventory.awaitingFull";
            stage = "请重新登录角色"; instruction = "返回角色选择画面，再选择同一角色进入游戏。等待完整库存同步，期间不必做其他操作。";
            var awaitingSince = context.ElapsedMs; var lastPackets = host.Shared.MaximumPacketsObserved;
            while (true)
            {
                var waiting = (await ObservationHost.RetryAsync(() => sdk.GetInventorySnapshotAsync(preparation.Token), preparation.Token)).Data;
                // 首次完整同步前库存快照没有工作区，失效原因只出现在 Host 受理的库存帧诊断中；面板与报告按帧诊断显示识别进度。
                var signals = new List<string>();
                foreach (var frame in host.Observations.InventoryDiagnostics().Frames)
                {
                    var signal = InventoryFrameTimeline.Signal(frame); signals.Add(signal);
                    if (loginSignals.TryAdd(signal, Math.Round(context.SinceStartMs(frame.HostMonotonicMs) - awaitingSince, 1)))
                        context.Mark("inventory.signal", signal);
                }
                foreach (var reason in waiting?.Reasons ?? [])
                    if (InventoryFrameTimeline.Reason(reason) is { } safe) { signals.Add(safe); loginSignals.TryAdd(safe, context.ElapsedMs - awaitingSince); }
                // 完整同步可能在本轮读取快照之前已到达：先采集帧诊断再判断是否已取得完整工作区，保证该场景记录自己的信号。
                if (waiting?.Current == true) break;
                var packets = host.Shared.MaximumPacketsObserved;
                instruction = AwaitingFullHint(signals.Distinct(StringComparer.Ordinal).ToArray(), packets is not null && packets != lastPackets, host.WorkerProcessId);
                lastPackets = packets;
                await EnsureRunningAsync(sdk, operationId, preparation.Token);
                await Task.Delay(500, preparation.Token);
            }
            fullSyncMs = context.ElapsedMs; context.Mark("inventory.fullSync");
            host.Stage = "inventory.run";
            stage = "增量观察";
            var runDeadline = context.DeadlineAfter(settings.RunSeconds);
            context.Show(() => new(stage, "已取得完整库存。请依次：使用或拆分一个可堆叠道具；装备或卸下一件装备；切换一次乐器；方便时在仓库存取一件物品。",
                context.Remaining(runDeadline), "倒计时结束后工具停止库存观察并汇总。"));
            while (context.Clock.GetTimestamp() < runDeadline)
            {
                context.Stopped.ThrowIfCancellationRequested();
                await EnsureRunningAsync(sdk, operationId, context.Stopped);
                var data = (await ObservationHost.RetryAsync(() => sdk.GetInventorySnapshotAsync(context.Stopped), context.Stopped)).Data;
                if (data is not null)
                {
                    revisions.Add(data.Revision.ToString());
                    if (!data.Current) { currentLosses++; foreach (var reason in data.Reasons) invalidations[reason] = invalidations.GetValueOrDefault(reason) + 1; }
                }
                await Task.Delay(1000, context.Stopped);
            }
            host.Stage = "inventory.collect";
            workspace = (await ObservationHost.RetryAsync(() => sdk.GetInventorySnapshotAsync(context.Stopped), context.Stopped)).Data;
            string? cursor = null;
            do
            {
                var query = new InventoryItemQuery(PageSize: 500, Cursor: cursor, CurrentOnly: true);
                var page = await ObservationHost.RetryAsync(() => sdk.QueryInventoryItemsAsync(query, context.Stopped), context.Stopped);
                items.AddRange(page.Items); cursor = page.NextCursor;
            } while (cursor is not null && items.Count < 20000);
            cursor = null;
            do
            {
                var query = new MusicOwnedInstrumentQuery(PageSize: 100, Cursor: cursor, CurrentOnly: true);
                instrumentPage = await ObservationHost.RetryAsync(() => sdk.QueryOwnedMusicInstrumentsAsync(query, context.Stopped), context.Stopped);
                instruments.AddRange(instrumentPage.Items); cursor = instrumentPage.NextCursor;
            } while (cursor is not null && instruments.Count < 2000);
            host.Stage = "inventory.stop";
            pidBeforeStop = host.WorkerProcessId; subscribersBeforeStop = host.Observations.Runtime().SubscriberCount;
            var stopping = operationId;
            await ObservationHost.RetryAsync(() => sdk.StopInventoryObservationAsync(stopping, context.Stopped), context.Stopped);
            var final = await host.WaitOperationAsync(() => sdk.GetInventoryObservationAsync(operationId, context.Stopped),
                value => SqliteStore.IsTerminal(value.State.KnownValue), context.Stopped);
            finalState = final.State.KnownValue?.ToString(); finalProblem = final.Problem?.Code; operationId = null;
            // 停止库存只释放库存兴趣；共享捕获由音乐需求继续持有，Worker 进程不变。
            pidAfterStop = host.WorkerProcessId; var runtime = host.Observations.Runtime();
            subscribersAfterStop = runtime.SubscriberCount; captureStateAfterStop = runtime.State;
            context.Mark("inventory.stopped");
        }
        catch (OperationCanceledException) when (context.Stopped.IsCancellationRequested) { failure = "probe.interrupted"; }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested && fullSyncMs is null)
        { failure = host.Stage == "inventory.awaitingFull" ? "inventory.fullSyncNotObserved" : "probe.preparationTimedOut"; }
        catch (Exception error) { failure = SafeCodes.Of(error); failureDetail = (error as MabiToolApiException)?.Problem?.Title; }
        finally
        {
            context.Show("正在停止", "正在释放库存观察、共享捕获和 Worker，请稍候。", null);
            if (operationId is not null && host.Observations is not null)
            {
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var sdk = host.Client();
                    var pending = operationId;
                    var state = await ObservationHost.RetryAsync(() => sdk.GetInventoryObservationAsync(pending, limit.Token), limit.Token);
                    if (!SqliteStore.IsTerminal(state.State.KnownValue))
                        await ObservationHost.RetryAsync(() => sdk.StopInventoryObservationAsync(pending, limit.Token), limit.Token);
                    var final = await host.WaitOperationAsync(() => sdk.GetInventoryObservationAsync(operationId, limit.Token),
                        value => SqliteStore.IsTerminal(value.State.KnownValue), limit.Token);
                    finalState = final.State.KnownValue?.ToString(); finalProblem = final.Problem?.Code;
                }
                catch (Exception error) { host.CleanupErrors.Add("inventory.stop:" + SafeCodes.Of(error)); }
            }
            await host.CleanupAsync(afterHostStop: () => inventoryDiagnostics = host.Observations?.InventoryDiagnostics());
        }
        var categories = items.GroupBy(value => (value.Item.Category, Container: value.Item.Container.KnownValue?.ToString() ?? "unknown"))
            .OrderBy(group => group.Key.Category, StringComparer.Ordinal).ThenBy(group => group.Key.Container, StringComparer.Ordinal)
            .Select(group => new CategoryCount(group.Key.Category, group.Key.Container, group.Count(),
                group.Aggregate(BigInteger.Zero, (sum, value) => sum + (value.Item.Quantity?.Value ?? 0)).ToString(CultureInfo.InvariantCulture),
                group.Count(value => value.Item.Quantity is null), group.Count(value => value.Item.Equipped),
                group.Count(value => value.Name.DisplayName is not null))).ToArray();
        var nameStates = items.GroupBy(value => value.Name.State.KnownValue?.ToString() ?? "unknown")
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var incomplete = workspace?.Coverage.Where(value => !value.Complete)
            .Select(value => new { value.Category, container = value.Container.KnownValue?.ToString() ?? "unknown", value.Reason }).ToArray();
        var stopKeptCapture = pidBeforeStop is not null && pidBeforeStop == pidAfterStop && captureStateAfterStop == "running" &&
            subscribersAfterStop == subscribersBeforeStop - 1;
        var detail = Summary(categories, instruments);
        string? answer = null;
        if (failure is null && workspace is not null)
        {
            context.Show("请核对", "打开游戏内背包与仓库，对照下方汇总（类别／容器：条目数、数量合计、已装备数）。", null);
            answer = await context.AskAsync("各类别数量、装备与乐器是否与游戏内一致？", detail, VerificationOptions, TimeSpan.FromSeconds(settings.VerificationSeconds));
        }
        var checks = new Dictionary<string, bool>
        {
            ["catalogLoaded"] = catalog?.Entries > 0,
            ["fullSyncObserved"] = fullSyncMs is not null,
            ["currentAtCollection"] = workspace?.Current == true,
            ["itemsObserved"] = items.Count > 0,
            ["inventoryStopKeptSharedCapture"] = stopKeptCapture,
            ["inventoryStoppedCleanly"] = finalState is "Cancelled" or "Succeeded",
            ["operatorConfirmed"] = answer == VerificationOptions[0],
            ["released"] = host.Released,
        };
        var accepted = failure is null && checks.Values.All(value => value);
        var report = JsonNodes.From(new
        {
            schemaVersion = 1, evidenceKind = Plan.EvidenceKind, checkedAtUtc = DateTimeOffset.UtcNow,
            gameBuild = host.Game?.Session.GameBuild, captureProfile = environment.CaptureProfile,
            phases = host.Phases, stageAtEnd = host.Stage, interrupted = context.Stopped.IsCancellationRequested, failure, failureDetail,
            catalog = catalog is null ? null : new { catalog.Id, catalog.IndexSha256, catalog.Entries, catalog.Excluded, catalogReadyMs = catalogMs,
                categories = catalog.Categories.Select(value => new { value.Category, value.Table, value.Supported, value.Entries, value.Reason }).ToArray() },
            capabilities = capabilities?.Capabilities.Select(value => new { value.Id, status = value.Status.KnownValue?.ToString(), value.Reason,
                categories = value.Categories, value.ClientAssemblySha256, value.ClientMetadataSha256 }).ToArray(),
            // loginSignals：各库存帧信号（失效原因或 inventory.frame.full/delta）首次出现的时点，相对开始等待，毫秒；含开始等待前已出现的信号（负值）。
            fullSync = new { observed = fullSyncMs is not null, atMs = fullSyncMs, loginSignals },
            // Host 受理的全部库存帧（种类、原因、条目数、相对开始的时点）；不含身份与条目内容。
            inventoryFrames = InventoryFrameTimeline.Project(inventoryDiagnostics, context), inventoryFrameCounts = inventoryDiagnostics?.Counts,
            // 身份只记录是否取得，不保存账号、角色 ID 或角色名。
            workspace = workspace is null ? null : new { identityObserved = workspace.Identity is not null, quality = workspace.Quality.KnownValue?.ToString(),
                workspace.Current, workspace.Reasons, coverageComplete = workspace.Coverage.Count(value => value.Complete), coverageIncomplete = incomplete,
                goldObserved = workspace.Gold is not null, source = workspace.Source },
            increments = new { revisionsObserved = revisions.Count, currentLossSamples = currentLosses, invalidations },
            items = new { total = items.Count, equipped = items.Count(value => value.Item.Equipped), nameStates, categories },
            instruments = new
            {
                total = instruments.Count, equipped = instruments.Count(value => value.Equipped),
                mapping = instruments.GroupBy(value => value.Mapping.KnownValue?.ToString() ?? "unknown").ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                current = instrumentPage?.Current, quality = instrumentPage?.Quality.KnownValue?.ToString(),
                entries = instruments.Take(200).Select(value => new { typeId = value.Type.TypeId.ToString(), value.Type.Category, name = value.Name.DisplayName,
                    container = value.Container.KnownValue?.ToString(), value.Equipped, quantity = value.Quantity?.ToString(),
                    mapping = value.Mapping.KnownValue?.ToString(), value.ClientInstrumentId }).ToArray(),
            },
            inventoryStop = new { finalState, finalProblem, pidBeforeStop, pidAfterStop, subscribersBeforeStop, subscribersAfterStop, captureStateAfterStop },
            operatorVerification = new { question = "各类别数量、装备与乐器是否与游戏内一致？", answer },
            checks, sharedObservation = host.Shared, memory = host.MemoryReport(), release = host.ReleaseReport(), totalElapsedMs = context.ElapsedMs,
        });
        report["acceptance"] = new JsonObject { ["accepted"] = accepted, ["released"] = host.Released };
        var outcome = failure switch
        {
            null => ScenarioResult.Completed,
            "probe.interrupted" => ScenarioResult.Cancelled,
            "probe.preparationTimedOut" or "inventory.fullSyncNotObserved" => ScenarioResult.TimedOut,
            _ => ScenarioResult.Failed
        };
        var failed = checks.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray();
        var summary = accepted ? $"库存验收通过：{items.Count} 条、{categories.Length} 个类别／容器，乐器 {instruments.Count}"
            : "结果：" + (failure ?? "未通过 " + string.Join("、", failed));
        return new(outcome, accepted, summary, report);
    }

    /// <summary>按库存失效原因说明 Worker 对登录数据的识别进度。</summary>
    internal static string AwaitingFullHint(IReadOnlyList<string> reasons, bool packetsMoving, int? workerProcessId)
    {
        var traffic = packetsMoving ? "网络数据持续到达" : "暂未看到新的网络数据";
        var worker = workerProcessId is { } pid ? $"Worker {pid}" : "Worker 未运行";
        var state = reasons.Contains("inventory.identityTimeout")
            ? "已检测到登录个人数据，但 30 秒内未收到身份关联包；请再重新登录一次，若仍出现此提示请记录角色是否加入公会。"
            : reasons.Contains("inventory.awaitingIdentity")
                ? "已检测到登录个人数据，正在等待身份关联包。"
                : reasons.Contains("inventory.streamGap")
                    ? "检测到游戏连接变化或抓包缺口（重新登录会出现此项），等待新的登录个人数据。"
                    : reasons.Contains("inventory.protocolInvalid")
                        ? "收到的库存数据无法按当前客户端布局解析；请保留本次报告。"
                        : "尚未检测到登录个人数据。请返回角色选择画面，再选择同一角色进入游戏。";
        return $"{state}（{traffic}，{worker}）";
    }

    private static async Task EnsureRunningAsync(MabiToolClient sdk, string operationId, CancellationToken ct)
    {
        var state = await ObservationHost.RetryAsync(() => sdk.GetInventoryObservationAsync(operationId, ct), ct);
        if (SqliteStore.IsTerminal(state.State.KnownValue)) throw new ProbeException(state.Problem?.Code ?? "inventory.observationEnded");
    }

    private static string Summary(IReadOnlyList<CategoryCount> categories, IReadOnlyList<MusicOwnedInstrument> instruments)
    {
        var text = new StringBuilder();
        foreach (var value in categories)
            text.Append(value.Category).Append('／').Append(value.Container).Append("：条目 ").Append(value.Entries)
                .Append("，数量 ").Append(value.Quantity).Append(value.UnknownQuantity > 0 ? $"（{value.UnknownQuantity} 条数量未知）" : "")
                .Append("，已装备 ").Append(value.Equipped).AppendLine();
        text.Append("乐器：").Append(instruments.Count).Append(" 件（已装备 ").Append(instruments.Count(value => value.Equipped)).Append("）");
        foreach (var value in instruments.Take(12)) text.Append("；").Append(value.Name.DisplayName ?? value.Type.TypeId.ToString());
        return text.ToString();
    }

    private sealed record CategoryCount(string Category, string Container, int Entries, string Quantity, int UnknownQuantity, int Equipped, int Named);
}
