using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Providers.TwCli;

namespace MabiTool.Host.Life;

// 总队列的模型与编辑：待执行与正在执行的任务按顺序放在 active，已结束的记录放在 history（有上限）。
// 所有修改在 gate 内完成并递增队列修订、保存快照；执行由 LifeService.Runner 的后台循环进行。
public sealed partial class LifeService
{
    public const int QueueCapacity = 100, HistoryCapacity = 30, ResultsPerTask = 8;
    private static readonly TimeSpan ReceiptLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumReceipts = 256;

    private sealed class QueueTask
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string Target { get; init; }
        public string? Facility { get; init; }
        public int? Variant { get; init; }
        public int Count;
        public int Done;
        public LifeTaskState State = LifeTaskState.Pending;
        public bool Immediate { get; init; }
        public string? LastCode, LastDetail;
        public bool StateUnknown;
        public DateTimeOffset AddedAt { get; init; }
        public DateTimeOffset? StartedAt, EndedAt;
        public List<LifeRoundResult> Results { get; } = [];
        public int RoundNumber;
        /// <summary>最近一次执行时目录写明的单次花费（旧版单项接口的 CostText），不持久保存。</summary>
        public string? CostText;

        /// <summary>
        /// 剩余要发出的命令次数（花费上限与确认按它计算）：采集按剩余数量 ÷ 每次目标 10 个向上取整（实得多于 10 个时实际次数更少），
        /// 加工每项一次，制作与领取一次。
        /// </summary>
        public int RemainingCalls => Kind switch
        {
            "craft" or "collect" => Done >= Count ? 0 : 1,
            "gather" => GatherCalls(Count - Done),
            _ => Math.Max(0, Count - Done)
        };

        public LifeTask Contract()
        {
            LifeRoundResult[] results;
            lock (Results) results = [.. Results];
            return new(Id, Kind, Target, Facility, Variant, Count, Done, State, Immediate, LastCode, LastDetail, StateUnknown, AddedAt, StartedAt, EndedAt, results);
        }

        public static QueueTask From(LifeTask task)
        {
            var value = new QueueTask
            {
                Id = task.Id, Kind = task.Kind, Target = task.Target, Facility = task.Facility, Variant = task.Variant, Immediate = task.Immediate, AddedAt = task.AddedAt,
                Count = task.Count, Done = task.Done, State = task.State.KnownValue ?? LifeTaskState.Pending, LastCode = task.LastCode, LastDetail = task.LastDetail,
                StateUnknown = task.StateUnknown, StartedAt = task.StartedAt, EndedAt = task.EndedAt
            };
            value.Results.AddRange(task.Results.TakeLast(ResultsPerTask));
            value.RoundNumber = task.Results.Count == 0 ? 0 : task.Results.Max(x => x.Round);
            return value;
        }
    }

    private sealed record Receipt(string Hash, long Created);

    private readonly List<QueueTask> active = [];
    private readonly List<QueueTask> history = [];
    private readonly Dictionary<(string Principal, string Key), Receipt> receipts = [];
    private LifeQueueState queueState = LifeQueueState.Idle;
    private string? pauseCode, pauseDetail, pauseTaskId;
    private int? approvedCost;
    private int estimatedSpent;
    private string? costCurrency;
    private ulong queueRevision;
    private LifeAction? lastAction;

    // ───────────────────────── 快照与持久化 ─────────────────────────

    // 调用方持有 gate。
    private LifeTaskQueue QueueLocked(TwCliCatalog? catalog)
    {
        var current = Executing();
        var code = runner is { PauseRequested: true } ? "life.pauseRequested" : pauseCode;
        var currency = costCurrency ?? Kinds.Select(kind => UnitCost(catalog, kind)?.Currency).FirstOrDefault(x => x is not null);
        return new(new(queueRevision), queueState, code, pauseDetail, pauseTaskId, current?.Id, approvedCost, runner is null ? null : estimatedSpent, currency, QueueCapacity,
            [.. history.Select(x => x.Contract()), .. active.Select(x => x.Contract())]);
    }

    // 旧版单项接口的“当前动作”：正在执行的任务，没有时为最近一次结束或暂停的任务。调用方持有 gate。
    private LifeAction? ActionLocked() => Executing() is { } task ? Describe(task, LifeActionPhase.Running) : lastAction;

    // 正在执行的任务：一轮进行中为当前任务，轮与轮之间为下一项要执行的任务。调用方持有 gate。
    private QueueTask? Executing() => runner is not { } run ? null
        : run.Current ?? run.Only ?? active.FirstOrDefault(x => !x.Immediate && x.State is LifeTaskState.Pending or LifeTaskState.Running);

    /// <summary>采集剩余 <paramref name="remaining"/> 个时估算的命令次数（每次以 10 个为目标）。</summary>
    private static int GatherCalls(int remaining) => remaining <= 0 ? 0 : (int)(((long)remaining + GatherPerCall - 1) / GatherPerCall);

    // 旧版单项接口的字段按“轮”描述：采集每轮 10 个，由目标数量与已采集数量换算（完成时已完成轮数等于总轮数）。
    private static LifeAction Describe(QueueTask task, LifeActionPhase phase)
    {
        var (rounds, done) = task.Kind switch
        {
            "craft" or "collect" => (1, task.Done >= task.Count ? 1 : 0),
            "gather" => (GatherCalls(task.Count), task.Done >= task.Count ? GatherCalls(task.Count) : task.Done / GatherPerCall),
            _ => (task.Count, task.Done)
        };
        LifeRoundResult[] results;
        lock (task.Results) results = [.. task.Results];
        return new(task.Id, task.Kind, task.Target, task.Facility, rounds, done, phase,
            task.LastCode, task.LastDetail, task.StateUnknown, task.StartedAt ?? task.AddedAt, phase == LifeActionPhase.Running ? null : task.EndedAt ?? task.StartedAt ?? task.AddedAt,
            task.CostText, results);
    }

    // 调用方持有 gate：递增修订并保存快照。
    private void Changed()
    {
        queueRevision = checked(queueRevision + 1);
        revision = checked(revision + 1);
        store.Save(new(LifeQueueStore.FormatVersion, [.. active.Select(x => x.Contract())], [.. history.Select(x => x.Contract())], pauseCode, pauseDetail, pauseTaskId));
    }

    /// <summary>
    /// 宿主启动时读回保存的队列：待执行任务保留；宿主退出时正在执行的任务改为待执行并标记结果未知；立即执行的单项转为已停止的记录。
    /// 有待执行任务时队列为暂停（工具重启后暂停），不自动执行。
    /// </summary>
    private void Restore(LifeQueueRecord? record)
    {
        if (record is null) return;
        history.AddRange(record.History.TakeLast(HistoryCapacity).Select(QueueTask.From));
        foreach (var saved in record.Tasks)
        {
            var task = QueueTask.From(saved);
            var interrupted = task.State == LifeTaskState.Running;
            if (interrupted) { task.StateUnknown = true; task.LastCode = "life.hostRestarted"; task.LastDetail = null; }
            if (task.Immediate)
            {
                task.State = LifeTaskState.Stopped;
                task.EndedAt ??= clock.GetUtcNow();
                history.Add(task);
                continue;
            }
            task.State = LifeTaskState.Pending;
            active.Add(task);
        }
        while (history.Count > HistoryCapacity) history.RemoveAt(0);
        if (active.Count > 0)
        {
            queueState = LifeQueueState.Paused;
            pauseCode = "life.hostRestarted";
            pauseDetail = null;
            pauseTaskId = active[0].Id;
        }
        queueRevision = 1;
    }

    private void Prune()
    {
        foreach (var pair in receipts.Where(x => clock.GetElapsedTime(x.Value.Created) >= ReceiptLifetime).ToArray()) receipts.Remove(pair.Key);
    }

    /// <summary>幂等：同一主体与键重复提交时，内容相同返回真（调用方直接返回当前快照），不同则拒绝。调用方持有 gate。</summary>
    private bool Repeated(string principal, string? key, object request, out string hash)
    {
        if (key is null) throw new OperationException("idempotency.required", "请求需要 Idempotency-Key");
        hash = Hash(request);
        Prune();
        if (!receipts.TryGetValue((principal, key), out var previous)) return false;
        return previous.Hash == hash ? true : throw new OperationException("idempotency.conflict", "幂等键已用于其他请求");
    }

    private static string Hash(object request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, request.GetType(), ContractJson.Options)));

    private void Remember(string principal, string key, string hash)
    {
        // 回执有上限：满了时移除最早的一条（回执只用来识别十分钟内的重复提交）。
        if (receipts.Count >= MaximumReceipts) receipts.Remove(receipts.MinBy(x => x.Value.Created).Key);
        receipts[(principal, key)] = new(hash, clock.GetTimestamp());
    }

    // ───────────────────────── 编辑 ─────────────────────────

    private static void ValidateTask(string kind, string? name, string? facility, int count, int? variant)
    {
        var maximum = kind switch { "gather" => MaximumGatherCount, "alter" => MaximumRounds, "craft" => MaximumCraftCount, _ => 1 };
        if (!Kinds.Contains(kind, StringComparer.Ordinal) || count < 1 || count > maximum ||
            (kind != "collect" && string.IsNullOrEmpty(name)) || (kind == "collect" && string.IsNullOrWhiteSpace(facility)) ||
            name?.Length > 128 || facility?.Length > 128 || name?.Any(char.IsControl) == true || facility?.Any(char.IsControl) == true ||
            (variant is not null && (kind != "craft" || variant < 0)))
            throw new OperationException("request.invalid", "任务内容无效");
    }

    private QueueTask Create(string kind, string? name, string? facility, int count, int? variant, bool immediate)
    {
        var (clientState, clientReason, tables) = client.Current();
        if (kind == "gather" && (knownFishing.Contains(name!) || LifeClassifier.Gather(clientState, clientReason, tables, name!).Fishing))
            throw new OperationException("life.fishingNotSupported", "钓鱼请使用专注钓鱼");
        var place = kind switch
        {
            "alter" => facility ?? LifeClassifier.Alter(clientState, clientReason, tables, name!).Group,
            "craft" => facility ?? LifeClassifier.Craft(clientState, clientReason, tables, name!).Group,
            "collect" => facility!.Trim(),
            _ => facility
        };
        return new QueueTask
        {
            Id = Guid.NewGuid().ToString("N"), Kind = kind, Target = kind == "collect" ? facility!.Trim() : name!, Facility = place, Variant = variant,
            Count = kind == "collect" ? 1 : count, Immediate = immediate, AddedAt = clock.GetUtcNow()
        };
    }

    /// <summary>
    /// 加入一项任务（幂等）。钓鱼类采集物拒绝；待执行任务达到上限时拒绝。<c>Position=next</c> 排到当前任务之后，缺省排到末尾。
    /// 队列正在运行且请求带的单次花费文字与目录一致时，这项任务的花费并入已确认的上限。
    /// </summary>
    public async Task<LifeSnapshot> AddTaskAsync(string principal, LifeTaskAddRequest request, string? idempotencyKey, CancellationToken ct = default)
    {
        if (request is null || request.Position is not (null or "end" or "next") || request.ExpectedCost?.Length > 64) throw new OperationException("request.invalid", "任务内容无效");
        var name = request.DisplayName?.Trim();
        ValidateTask(request.Kind, name, request.Facility, request.Kind == "collect" ? 1 : request.Count, request.Variant);
        TwCliCost? cost = null;
        if (request.ExpectedCost is not null)
        {
            var catalog = await life.CatalogAsync(ct).ConfigureAwait(false);
            cost = UnitCost(catalog, request.Kind);
            if (!string.Equals(request.ExpectedCost.Trim(), cost?.Text ?? string.Empty, StringComparison.Ordinal))
                throw new OperationException("life.costChanged", "指令花费与确认时不同，请按当前花费重新确认", new Dictionary<string, JsonElement>
                { ["cost"] = JsonSerializer.SerializeToElement(cost?.Text ?? string.Empty) });
        }
        lock (gate)
        {
            if (!Repeated(principal, idempotencyKey, request, out var hash))
            {
                if (active.Count >= QueueCapacity) throw new OperationException("life.queueFull", "生活队列已满");
                var task = Create(request.Kind, name, request.Facility, request.Count, request.Variant, immediate: false);
                Remember(principal, idempotencyKey!, hash);
                var index = request.Position == "next" ? (runner?.Current is { } running ? active.IndexOf(running) + 1 : 0) : active.Count;
                active.Insert(Math.Clamp(index, 0, active.Count), task);
                if (runner is not null && cost?.Amount is { } unit && approvedCost is { } approved)
                    approvedCost = (int)Math.Min(int.MaxValue, approved + (long)unit * task.RemainingCalls);
                Changed();
            }
        }
        return Current();
    }

    /// <summary>
    /// 修改一项任务的数量或位置；页面所见的队列修订必须是当前修订。正在执行的任务不能移动，制作开始后次数不能改，
    /// 采集的目标数量必须大于已采集的数量，加工的项数不能少于已完成的项数（正在执行时再加一）。
    /// </summary>
    public LifeSnapshot UpdateTask(string id, LifeTaskUpdateRequest request)
    {
        if (request is null || (request.Count is null && request.Index is null)) throw new OperationException("request.invalid", "修改内容无效");
        lock (gate)
        {
            if (request.ExpectedRevision.Value != queueRevision) throw new OperationException("revision.stale", "生活队列已经变化，请按最新内容再改");
            var task = active.FirstOrDefault(x => x.Id == id) ?? throw new OperationException("resource.notFound", "队列里没有这项任务");
            var running = runner?.Current == task;
            if (request.Count is { } count)
            {
                ValidateTask(task.Kind, task.Target, task.Facility, count, task.Variant);
                if (task.Kind == "collect" && count != 1) throw new OperationException("request.invalid", "领取没有数量");
                if (running && task.Kind == "craft") throw new OperationException("life.taskRunning", "制作已经开始，不能修改次数");
                if (task.Kind == "gather" && count <= task.Done)
                    throw new OperationException("request.invalid", "目标数量需要大于已采集的数量");
                if (task.Kind == "alter" && count < task.Done + (running ? 1 : 0))
                    throw new OperationException("request.invalid", "数量不能少于已完成的项数");
                task.Count = Math.Max(count, task.Done);
            }
            if (request.Index is { } index)
            {
                if (running) throw new OperationException("life.taskRunning", "正在执行的任务不能移动");
                var pending = active.Where(x => x != runner?.Current).ToList();
                if (index < 0 || index >= pending.Count) throw new OperationException("request.invalid", "位置无效");
                pending.Remove(task);
                pending.Insert(index, task);
                var head = runner?.Current is { } current ? [current] : Array.Empty<QueueTask>();
                active.Clear();
                active.AddRange(head.Concat(pending));
            }
            if (task.Done >= task.Count && task.Kind is "gather" or "alter" && !running) Finish(task, LifeTaskState.Completed);
            Changed();
        }
        return Current();
    }

    /// <summary>删除一项任务或一条已结束的记录；正在执行的任务需要先停止。</summary>
    public LifeSnapshot RemoveTask(string id)
    {
        lock (gate)
        {
            if (runner?.Current?.Id == id) throw new OperationException("life.taskRunning", "正在执行的任务需要先停止");
            var removed = active.RemoveAll(x => x.Id == id) + history.RemoveAll(x => x.Id == id);
            if (removed == 0) throw new OperationException("resource.notFound", "队列里没有这项任务");
            if (pauseTaskId == id) pauseTaskId = null;
            if (queueState == LifeQueueState.Paused && active.Count == 0 && runner is null) { queueState = LifeQueueState.Idle; pauseCode = pauseDetail = null; }
            Changed();
        }
        return Current();
    }

    /// <summary>队列命令：开始或继续（核对整队花费上限）、暂停、停止当前、清空已完成。</summary>
    public async Task<LifeSnapshot> CommandAsync(LifeQueueCommand request, CancellationToken ct = default)
    {
        switch (request?.Action)
        {
            case "start": await StartQueueAsync(request.ExpectedCostCeiling, ct).ConfigureAwait(false); break;
            case "pause":
                lock (gate)
                {
                    if (runner is { Immediate: false } running) { running.PauseRequested = true; revision = checked(revision + 1); }
                    else if (runner is null && queueState == LifeQueueState.Idle && active.Count > 0)
                    { queueState = LifeQueueState.Paused; pauseCode = "life.pausedByUser"; pauseDetail = null; pauseTaskId = active[0].Id; Changed(); }
                }
                break;
            case "stop-current": StopCurrent(); break;
            case "clear-finished":
                lock (gate) { history.Clear(); lastAction = null; Changed(); }
                break;
            default: throw new OperationException("request.invalid", "队列命令无效");
        }
        return Current();
    }

    /// <summary>
    /// 整队花费上限：各项剩余命令次数 × 单次花费之和（目录没有写明花费的指令按 0 计，确认框如实写明）。调用方持有 gate。
    /// </summary>
    private int Ceiling(TwCliCatalog? catalog) =>
        (int)Math.Min(int.MaxValue, active.Where(x => !x.Immediate).Sum(task => (long)task.RemainingCalls * (UnitCost(catalog, task.Kind)?.Amount ?? 0)));

    private async Task StartQueueAsync(int? expectedCeiling, CancellationToken ct)
    {
        var status = connector.Current;
        if (status.Status.KnownValue != GameConnectorStatus.Connected)
            throw new OperationException("life.connectorUnavailable", "游戏连接器未连接", new Dictionary<string, JsonElement> { ["reason"] = JsonSerializer.SerializeToElement(status.Reason) });
        var catalog = await life.CatalogAsync(ct).ConfigureAwait(false);
        if (catalog is null || catalog.Loading) throw new OperationException("life.commandUnavailable", "游戏目录尚未读取");
        int ceiling;
        lock (gate)
        {
            if (runner is not null) return;
            if (active.Count == 0) throw new OperationException("life.nothingToRun", "生活队列里没有待执行的任务");
            if (active.FirstOrDefault(x => !catalog.Has(CommandOf(x.Kind))) is { } missing)
                throw new OperationException("life.commandUnavailable", "游戏目录里没有这条指令", new Dictionary<string, JsonElement> { ["command"] = JsonSerializer.SerializeToElement(CommandOf(missing.Kind)) });
            ceiling = Ceiling(catalog);
            if (expectedCeiling != ceiling)
                throw new OperationException("life.costChanged", "整队花费与确认时不同，请按当前花费重新确认", new Dictionary<string, JsonElement>
                { ["ceiling"] = JsonSerializer.SerializeToElement(ceiling) });
        }
        Launch(null, ceiling, Kinds.Select(kind => UnitCost(catalog, kind)?.Currency).FirstOrDefault(x => x is not null));
    }

    // ───────────────────────── 旧版单项接口 ─────────────────────────

    /// <summary>
    /// 旧版单项执行（领取也用它）：核对请求、连接器、目录、单次花费文字与钓鱼限制后，把这一项插到队列最前并立即执行；结束后队列回到原来的状态。
    /// 同一幂等键重复提交不重复执行；队列正在执行、活动被占用或花费与目录不符时拒绝。
    /// </summary>
    public async Task<LifeSnapshot> StartAsync(string principal, LifeActionRequest request, string? idempotencyKey, CancellationToken ct = default)
    {
        if (request is null || !Kinds.Contains(request.Kind, StringComparer.Ordinal)) throw new OperationException("request.invalid", "执行请求无效");
        if (idempotencyKey is null) throw new OperationException("idempotency.required", "执行请求需要 Idempotency-Key");
        bool repeated;
        lock (gate) repeated = Repeated(principal, idempotencyKey, request, out _);
        if (repeated) return Current();
        var kind = request.Kind;
        var name = request.DisplayName?.Trim();
        // 旧版接口的采集数量是轮数：每轮 10 个，换算为目标数量（50 轮即上限 500 个）。
        var count = kind switch
        {
            "collect" => 1,
            "gather" => (int)Math.Clamp((long)request.Count * GatherPerCall, int.MinValue, int.MaxValue),
            _ => request.Count
        };
        if (request.ExpectedCost?.Length > 64) throw new OperationException("request.invalid", "执行请求无效");
        ValidateTask(kind, name, request.Facility, count, request.Variant);

        var status = connector.Current;
        if (status.Status.KnownValue != GameConnectorStatus.Connected)
            throw new OperationException("life.connectorUnavailable", "游戏连接器未连接", new Dictionary<string, JsonElement> { ["reason"] = JsonSerializer.SerializeToElement(status.Reason) });
        var command = CommandOf(kind);
        // 花费取执行当时的目录备注，与确认框显示的文字不一致时要求重新确认。
        var catalog = await life.CatalogAsync(ct).ConfigureAwait(false);
        if (catalog is null || catalog.Loading || !catalog.Has(command))
            throw new OperationException("life.commandUnavailable", "游戏目录里没有这条指令", new Dictionary<string, JsonElement> { ["command"] = JsonSerializer.SerializeToElement(command) });
        var cost = UnitCost(catalog, kind);
        if (!string.Equals(request.ExpectedCost?.Trim() ?? string.Empty, cost?.Text ?? string.Empty, StringComparison.Ordinal))
            throw new OperationException("life.costChanged", "指令花费与确认时不同，请按当前花费重新确认", new Dictionary<string, JsonElement>
            { ["cost"] = JsonSerializer.SerializeToElement(cost?.Text ?? string.Empty) });

        QueueTask? task = null;
        lock (gate)
        {
            if (runner is not null) throw new OperationException("life.actionRunning", "生活队列正在执行");
            if (!Repeated(principal, idempotencyKey, request, out _))
            {
                if (kind == "collect" && WorkToCollect(request.Facility!.Trim()) is null)
                    throw new OperationException("life.nothingToCollect", "该设施没有已完成的加工");
                task = Create(kind, name, request.Facility, count, request.Variant, immediate: true);
                task.CostText = cost?.Text;
            }
        }
        if (task is null) return Current();
        Launch(task, (int)Math.Min(int.MaxValue, (long)(cost?.Amount ?? 0) * task.RemainingCalls), cost?.Currency);
        // 回执在受理成功后才记下：活动被占用等拒绝之后，同一个键可以重新提交。
        lock (gate) Remember(principal, idempotencyKey, Hash(request));
        return Current();
    }

    /// <summary>停止当前：中断命令行进程，游戏里的动作状态按未知处理，队列暂停（立即执行的单项记为已停止），结束后重读相关数据。</summary>
    public LifeSnapshot Stop()
    {
        StopCurrent();
        return Current();
    }

    private void StopCurrent()
    {
        Runner? current;
        lock (gate) current = runner;
        if (current is null) return;
        current.UserStopped = true;
        current.Cancellation.Cancel();
        connector.Cli.Interrupt();
    }

    /// <summary>该设施一项已完成加工的名称（领取的 body 用它选中设施）；没有时为空。调用方持有 gate。</summary>
    private string? WorkToCollect(string facility) =>
        queue?.Works.FirstOrDefault(x => string.Equals(x.Facility, facility, StringComparison.Ordinal) && x.Completed == true)?.Name;
}
