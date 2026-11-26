using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

/// <summary>“生活”页某一块的读取状态：<c>NotRead</c> 还没读，<c>Loaded</c> 已读到（空结果也是读到），<c>Failed</c> 读取失败（原因码在 <c>Reason</c>，不带旧数据）。</summary>
public enum LifeReadState { NotRead, Loaded, Failed }

/// <summary>客户端表（归属分类的数据来源）的状态。</summary>
public enum LifeClientState { Loading, Ready, Unavailable }

public enum LifeActionPhase { Running, Completed, Stopped, Failed }

/// <summary>
/// 一轮执行的结果类别：<c>Completed</c> 采集或制作完成，<c>Started</c> 加工已排队（采集时表示命令行开启了自动钓鱼），<c>Collected</c> 领取完成，
/// <c>Stopped</c> 游戏里提前结束，<c>Rejected</c> 游戏拒绝或开始后被打断，<c>Failed</c> 命令行出错、游戏效果未知，
/// <c>Interrupted</c> 被用户停止或紧急停止中断、游戏动作状态未知，<c>NotSent</c> 命令确定没有发出。
/// </summary>
public enum LifeRoundOutcome { Completed, Started, Collected, Stopped, Rejected, Failed, Interrupted, NotSent }

public sealed record LifeSectionStatus(OpenEnum<LifeReadState> State, string? Reason, DateTimeOffset? ReadAt);

public sealed record LifeMaterial(string Name, int? Required, int? Owned);

/// <summary>
/// 可采物。<c>Group</c> 是客户端表确定的生活技能名称，读不到归属时为空并在 <c>GroupReason</c> 写明原因码（界面归入“其他”）；
/// <c>Fishing</c> 为真表示钓鱼类采集物（归属为钓鱼，或本次运行中执行时发现命令行开启了自动钓鱼）；
/// <c>GroupSource</c> 是归属来源：<c>mabiagent</c>（用户 MabiAgent 写好的采集归类）或 <c>client</c>（客户端表），读不到时为空。
/// </summary>
public sealed record LifeGatherItem(string Name, bool? ToolOk, string? Group, string? GroupReason, bool Fishing, string? GroupSource = null);

public sealed record LifeAlterRecipe(string Name, bool? Alterable, int? ProducedPerWork, string? Reason, IReadOnlyList<LifeMaterial> Missing,
    string? Group, string? GroupReason);

public sealed record LifeAlterWork(string Name, string Facility, string? State, bool? Completed, double? RemainingSeconds);

public sealed record LifeAlterQueue(int? CompletedCount, IReadOnlyList<LifeAlterWork> Works);

public sealed record LifeCraftRecipe(string Name, int? Variant, bool? Craftable, int? ProducedPerCraft, double? SuccessRate, double? CriticalSuccessRate,
    string? Reason, IReadOnlyList<LifeMaterial> Ingredients, string? Group, string? GroupReason);

/// <summary>
/// 指令目录里一条生活指令的现状：<c>Available</c> 为假时 <c>Reason</c> 是原因码；<c>CostAmount</c>/<c>CostCurrency</c> 来自目录备注（没有写花费时为空，
/// 界面如实写明），<c>CostText</c> 是确认框显示、执行请求回传核对的花费文字；<c>MovesCharacter</c> 表示执行会自动移动角色。
/// </summary>
public sealed record LifeCommandInfo(string Command, bool Available, string? Reason, int? CostAmount, string? CostCurrency, string? CostText, bool MovesCharacter);

public sealed record LifeReward(string Name, int? Amount);

/// <summary>一轮执行的结果。<c>Cost</c> 是客户端返回的花费句子原文，能解析时 <c>Spent</c>、<c>Left</c> 与 <c>CostCurrency</c> 同时给出。</summary>
public sealed record LifeRoundResult(int Round, OpenEnum<LifeRoundOutcome> Outcome, string? Code, string? Message, string? BlockedKind,
    int? Gained, int? Target, string? Cost, int? Spent, int? Left, string? CostCurrency, int? Collected, IReadOnlyList<LifeReward> Rewards,
    int? CraftCount, int? MaxCount);

/// <summary>
/// 一个执行动作（采集、加工排队、领取、制作）及其逐轮结果。<c>StopCode</c> 是停止或失败的原因码（游戏原因经 gameCli 前缀规范化、
/// 本地原因用 life 前缀）；<c>StateUnknown</c> 为真表示游戏里的动作状态未知（被中断或命令行出错），页面应重新读取。
/// 采集的 <c>Rounds</c>／<c>DoneRounds</c> 按每轮 10 个由目标数量与已采集数量换算。
/// </summary>
public sealed record LifeAction(string ActionId, string Kind, string Target, string? Facility, int Rounds, int DoneRounds,
    OpenEnum<LifeActionPhase> Phase, string? StopCode, string? StopDetail, bool StateUnknown, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string? CostText, IReadOnlyList<LifeRoundResult> Results);

/// <summary>活动锁（<c>activity-control</c>）的占用者；<c>Life</c> 为真表示就是本模块的执行。</summary>
public sealed record LifeOccupancy(string OwnerId, bool Life);

public sealed record LifeSnapshot(string BackendEpoch, UInt64String Revision,
    OpenEnum<GameConnectorStatus> Connector, string ConnectorReason,
    OpenEnum<LifeClientState> Client, string? ClientReason,
    LifeSectionStatus Gather, string? GatherQuery, IReadOnlyList<LifeGatherItem> GatherItems,
    LifeSectionStatus Alter, string? AlterQuery, IReadOnlyList<LifeAlterRecipe> AlterRecipes,
    LifeSectionStatus Works, LifeAlterQueue? Queue,
    LifeSectionStatus Craft, string? CraftQuery, bool? CraftingUnlocked, IReadOnlyList<LifeCraftRecipe> CraftRecipes,
    IReadOnlyList<LifeCommandInfo> Commands, LifeAction? Action, LifeOccupancy? Occupied,
    LifeTaskQueue? Tasks = null, IReadOnlyList<LifeFacility>? Facilities = null);

/// <summary>总队列的状态：<c>Idle</c> 空闲（没有在执行，也没有暂停原因），<c>Running</c> 正在按顺序执行，<c>Paused</c> 已暂停（原因见 <c>PauseCode</c>）。</summary>
public enum LifeQueueState { Idle, Running, Paused }

/// <summary>
/// 一项任务的状态：<c>Pending</c> 待执行（暂停时停在这一项也是待执行），<c>Running</c> 正在执行，<c>Completed</c> 已完成，
/// <c>Failed</c>／<c>Stopped</c> 只用于立即执行的单项（领取、旧版单项接口）结束在失败或停止。
/// </summary>
public enum LifeTaskState { Pending, Running, Completed, Failed, Stopped }

/// <summary>
/// 总队列里的一项任务。<c>Kind</c>：<c>gather</c>（采集，<c>Count</c> 为目标数量）、<c>alter</c>（加工排队，项数）、<c>collect</c>（按设施领取，
/// <c>Target</c> 与 <c>Facility</c> 都是设施名）、<c>craft</c>（制作，<c>Count</c> 为 craftCount，<c>Variant</c> 为配方版本）。
/// <c>Done</c> 是已完成的部分：采集为按每次调用返回的 <c>gained</c> 累计的已采集数量（可能超过 <c>Count</c>），加工为已排队的项数，
/// 制作与领取完成时等于 <c>Count</c>；<c>LastCode</c>／<c>LastDetail</c> 是最近一次停止或失败的原因；
/// <c>StateUnknown</c> 为真表示最近一次执行被中断或宿主重启时正在执行，游戏里的结果未知。<c>Immediate</c> 表示立即执行的单项。
/// <c>Results</c> 只保留最近几次调用。
/// </summary>
public sealed record LifeTask(string Id, string Kind, string Target, string? Facility, int? Variant, int Count, int Done,
    OpenEnum<LifeTaskState> State, bool Immediate, string? LastCode, string? LastDetail, bool StateUnknown,
    DateTimeOffset AddedAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, IReadOnlyList<LifeRoundResult> Results);

/// <summary>
/// 宿主维护的总队列。<c>PauseCode</c>／<c>PauseDetail</c> 是暂停原因（游戏原因经 gameCli 前缀规范化，本地原因用 life 前缀），
/// <c>PauseTaskId</c> 是停在的那一项；<c>CurrentTaskId</c> 是正在执行的一项。花费：<c>ApprovedCost</c> 是本次运行已确认的上限，
/// <c>EstimatedSpent</c> 是按目录单价累计的预计花费，<c>CostCurrency</c> 是货币名；<c>Capacity</c> 是待执行任务的上限。
/// </summary>
public sealed record LifeTaskQueue(UInt64String Revision, OpenEnum<LifeQueueState> State, string? PauseCode, string? PauseDetail, string? PauseTaskId,
    string? CurrentTaskId, int? ApprovedCost, int? EstimatedSpent, string? CostCurrency, int Capacity, IReadOnlyList<LifeTask> Tasks);

/// <summary>
/// 加工设施的槽位情况。<c>QueueFull</c> 为真表示游戏报告槽位已满（配方读取的 <c>queue_full</c> 或排队被 <c>queue_full</c> 拒绝），
/// 原因见 <c>QueueFullSource</c>（<c>recipes</c> 或 <c>execute</c>）。命令行不提供设施等级与槽位数，契约里不给出。
/// </summary>
public sealed record LifeFacility(string Name, bool QueueFull, string? QueueFullSource);

/// <summary>读取请求：<c>Section</c> 为 <c>gather</c>、<c>alter</c>（配方）、<c>works</c>（加工队列）、<c>craft</c> 或 <c>all</c>；<c>Query</c> 是纯文本筛选，对 works 无效。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifeRefreshRequest(string Section, string? Query = null);

/// <summary>
/// 执行请求。<c>Kind</c> 为 <c>gather</c>、<c>alter</c>、<c>collect</c>（按设施领取，<c>Facility</c> 是 <c>get_altering_works</c> 的设施名原文）、<c>craft</c>。
/// <c>Count</c>：采集轮数（每轮 10 个，换算为目标数量）、加工排队项数、制作次数（<c>craftCount</c>）。<c>ExpectedCost</c> 是确认框显示的花费文字，与当前目录解析的花费不同时后端拒绝。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifeActionRequest(string Kind, string? DisplayName = null, string? Facility = null, int Count = 1, int? Variant = null,
    string? ExpectedCost = null);

/// <summary>
/// 加入总队列的任务。<c>Kind</c> 与 <c>Count</c> 同 <see cref="LifeTask"/>；<c>Position</c> 为 <c>end</c>（缺省，排到末尾）或 <c>next</c>（排到当前任务之后）。
/// 队列正在运行时，带上确认框显示的单次花费文字 <c>ExpectedCost</c>（与目录一致）表示用户已确认，这项任务的花费并入已确认的上限。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifeTaskAddRequest(string Kind, string? DisplayName = null, string? Facility = null, int Count = 1, int? Variant = null,
    string? Position = null, string? ExpectedCost = null);

/// <summary>修改一项任务：<c>Count</c> 新数量，<c>Index</c> 移到待执行任务里的位置（0 起）；<c>ExpectedRevision</c> 是页面所见的队列修订，不一致时拒绝。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifeTaskUpdateRequest(UInt64String ExpectedRevision, int? Count = null, int? Index = null);

/// <summary>
/// 队列命令：<c>start</c>（开始或继续，须带确认框显示的整队花费上限 <c>ExpectedCostCeiling</c>，目录没有写明花费时为空）、<c>pause</c>（当前一轮结束后暂停）、
/// <c>stop-current</c>（中断当前任务的命令并暂停）、<c>clear-finished</c>（移除已完成与已结束的记录）。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifeQueueCommand(string Action, int? ExpectedCostCeiling = null);

/// <summary>
/// 开始一次原地专注钓鱼。用户先站到可钓位置，工具不选点、不移动到钓鱼点；<see cref="DurationSeconds"/> 为空表示直到用户停止。
/// </summary>
public sealed record FishingRunRequest(string GameSessionId, UInt64String ExpectedBindingRevision, int? DurationSeconds);

/// <summary>只记轮次与结局，不记鱼获。Submitted 为已观察到上钩或进度条的轮次；Reeled 为收竿动作已提交的轮次；
/// DepletedWithoutRebound 为进度条直接归零、没有回升收竿的轮次；NoResponseRetries 为抛竿 20 秒无反应后的重抛次数。</summary>
public sealed record FishingRounds(int Submitted, int Reeled, int DepletedWithoutRebound, int NoResponseRetries);

public enum FishingPoseState { NotNeeded, Observed, Unconfirmed }

/// <summary>结束记录。<see cref="InputReleased"/> 表示工具按下的输入已全部释放；<see cref="PoseLeft"/> 只有观察到离开钓鱼姿态的画面证据才是 Observed。</summary>
public sealed record FishingStop(string Reason, bool InputReleased, OpenEnum<FishingPoseState> PoseLeft, DateTimeOffset StoppedAt);

/// <summary>
/// 钓鱼运行快照，也是 <c>life.fishing.changed</c> 事件的数据。<see cref="ElapsedSeconds"/> 在首次抛竿前为空（未开始计时，不是 0）；
/// <see cref="Reason"/> 是最近一次值得提示的原因码（如 cast_retry_no_response），不是终态错误。
/// </summary>
public sealed record FishingRun(string Id, string OperationId, string Owner, GameSession BindingSnapshot, int? DurationSeconds,
    string Phase, bool ClockStarted, double? ElapsedSeconds, FishingRounds Rounds, int ConsecutiveRoundsWithoutHook, string? Reason,
    FishingStop? Stop, UInt64String Revision, DateTimeOffset UpdatedAt, string HostClockEpoch);

public sealed record FishingCapability(bool Simulation, OpenEnum<CapabilityStatus> Status, string ReasonCode,
    IReadOnlyList<string> Requirements, string? GameSessionId, string? GameBuild, DateTimeOffset CheckedAt);

/// <summary>能力与当前（或最近一次）运行，与 <c>life.fishing.changed</c> 共用一致的事件水位。</summary>
public sealed record FishingSnapshot(string BackendEpoch, string EventCursor, UInt64String ResourceRevision,
    FishingCapability Capability, FishingRun? Run);

public static class LifeFishingErrors
{
    public const string CastPreflightRetryLimit = "life.fishing.castPreflightRetryLimit";
    public const string DismountNotObserved = "life.fishing.dismountNotObserved";
    public const string BackpackFull = "life.fishing.backpackFull";
    public const string InterfaceUnrecognized = "life.fishing.interfaceUnrecognized";
    public const string HookNotRecognized = "life.fishing.hookNotRecognized";
    public const string LayoutUnsupported = "life.fishing.layoutUnsupported";
    public const string ComponentMissing = "life.fishing.componentMissing";
    public const string RunInactive = "life.fishing.runInactive";

    // 钓鱼运行可能以这些输入原因码结束，它们在其他模块里没有登记为公共错误，这里补齐。
    public static IReadOnlyList<ErrorDefinition> Definitions { get; } = Array.AsReadOnly<ErrorDefinition>([
        new(CastPreflightRetryLimit, 409, false), new(DismountNotObserved, 409, false), new(BackpackFull, 409, false),
        new(InterfaceUnrecognized, 409, false), new(HookNotRecognized, 409, false), new(LayoutUnsupported, 422, false),
        new(ComponentMissing, 422, false), new(RunInactive, 409, false),
        new("input.windowMinimized", 409, false), new("input.backgroundUnsupported", 422, false),
        new("input.backgroundSimulation", 422, false), new("input.backgroundUnavailable", 422, false),
        new("input.backgroundDpiVirtualized", 422, false), new("input.backgroundRejected", 409, false),
        new("input.pointOutsideWindow", 400, false), new("input.planTooLong", 400, false)
    ]);
}
