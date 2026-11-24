using System.Diagnostics;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Combat;

namespace MabiTool.Desktop.Backend;

// 刷新调度：每类数据有自己的读取时机（事件、用户操作、可见性变化或各自的例行间隔）。战斗数据由独立的战斗循环读取，
// 其余数据由主循环读取，两者互不等待：图标、合奏、曲长等慢读取不挡住战斗读取。
// 战斗事件按主题标记要读的对象，到达即读，两轮之间至少隔一帧（约 16 ms），间隔内的变化并入下一轮；用户操作不等间隔。
// 界面各处（主窗口、迷你窗、浮窗元件、背包页）报告自己是否可见，没有可见的使用者时不为它拉数据，重新可见时立即补读一次。
public sealed partial class LiveBackend
{
    /// <summary>
    /// 两轮事件驱动的战斗读取的最小间隔（每秒至多 60 轮）：事件到达即读，读取进行中或间隔内到达的事件并入下一轮，不丢变化。
    /// 下一轮的时刻由高精度计时器唤醒。
    /// </summary>
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    /// <summary>
    /// 只有工作区修订推进时，整场或所选目标的范围汇总、明细窗口的范围与首击时间线的最短重读间隔：它们在宿主上按时间线重新统计，
    /// 不随每一帧重读；间隔内的变化安排一轮补读。
    /// </summary>
    internal static readonly TimeSpan SecondaryInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>绑定与输入占用的例行间隔：浮窗跟随游戏窗口位置、活动胶囊的占用状态都依赖它，没有对应事件。</summary>
    internal static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    /// <summary>没有任何界面可见时绑定与输入占用的间隔。</summary>
    internal static readonly TimeSpan HiddenStatusInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 战斗快照的兜底间隔：观察状态与本机战斗状态（开始、等待识别、战斗中／战斗外）变化时宿主不一定推送事件，
    /// 战斗视图可见时按这个间隔补读一次快照，修订未变时不通知界面。
    /// </summary>
    internal static readonly TimeSpan SnapshotFallbackInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 等待第一份战斗数据（开始观测的结果未出、观察不在进行中或还没有当前分段）时的快照兜底间隔：首个摘要事件丢失时，
    /// 界面最迟约 1 秒后出现数据；与绑定、占用的每秒读取同一节拍，不额外唤醒。拿到当前分段后回到 <see cref="SnapshotFallbackInterval"/>。
    /// </summary>
    internal static readonly TimeSpan WaitingSnapshotInterval = TimeSpan.FromSeconds(1);

    // 调用方持有 gate。定时测量等待或计时中同样按 1 秒：宿主只在阶段变化时推送测量事件，计时中的累计伤害与 DPS 要随读取更新。
    private TimeSpan SnapshotInterval => startingObservation is not null || MeasurementActive
        || state.Workspace is not { Observation.State: "observing", Encounter: not null }
        ? WaitingSnapshotInterval : SnapshotFallbackInterval;

    // 调用方持有 gate。
    private bool MeasurementActive => measurementState?.Phase is "waiting" or "running";

    /// <summary>效果覆盖率（分段时间线的 effectCoverage）随分段修订重读的最短间隔。</summary>
    internal static readonly TimeSpan CoverageInterval = TimeSpan.FromSeconds(2);

    /// <summary>抓包与合奏状态在没有事件时的兜底间隔；抓包网卡检测进行中、身在房间内时按 1 秒读取。</summary>
    internal static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(5);

    [Flags]
    private enum Due
    {
        None = 0,
        Snapshot = 1,
        History = 2,
        TargetEffects = 4,
        Measurement = 8,
        Auto = 16,
        // 只重算依赖选择的部分（范围、效果目标、首击），不重读快照。
        Recompute = 32,
        Status = 64,
        Capture = 128,
        Room = 256,
        Combat = Snapshot | History | TargetEffects | Measurement | Auto | Recompute,
        All = Combat | Status | Capture | Room
    }

    private Due due = Due.All;
    private long lastCombatRound, lastSecondaryRead, lastSnapshotRead, lastStatusRead, lastCaptureRead, lastRoomRead, lastColorRunRead,
        lastSurfacesRead, followUpAt;
    private bool urgent = true;
    private CombatEncounterPage? historyPage;
    private (string Key, CombatRangeSummary? Range, string? Problem)? rangeRead;
    private (string EncounterId, string Revision, long At, IReadOnlyList<CombatEffectCoverage> Coverage)? coverageRead;
    private (string? Id, CombatEntityEffects? Effects, string? Problem, long At) targetRead;
    /// <summary>当前攻击目标（快照 currentTarget）的效果，收藏浮窗的目标项用；与效果目标相同时沿用 <see cref="targetRead"/>。</summary>
    private (string? Id, CombatEntityEffects? Effects, string? Problem, long At) aimRead;
    private CombatMeasurementState? measurementState;
    private long measurementAt;

    private static long Now => Stopwatch.GetTimestamp();
    private static long Span(TimeSpan value) => (long)(value.TotalSeconds * Stopwatch.Frequency);
    private static bool Elapsed(long since, TimeSpan interval) => since == 0 || Now - since >= Span(interval);

    /// <summary>
    /// 标记要读取的部分并唤醒对应的循环（战斗部分唤醒战斗循环，其余唤醒主循环）；<paramref name="immediate"/> 为真（用户操作、可见性变化）时
    /// 不等帧间隔。
    /// </summary>
    private void MarkDue(Due flags, bool immediate)
    {
        lock (gate)
        {
            due |= flags;
            if (immediate) urgent = true;
        }
        if ((flags & Due.Combat) != 0) SignalCombat();
        if ((flags & ~Due.Combat) != 0) Signal();
    }

    /// <summary>测试用：界面当前取用的战斗工作区修订。</summary>
    internal ulong? ShownWorkspaceRevision => Read().State.Workspace?.Revision.Value;

    /// <summary>测试用：界面当前取用的范围结果（是否有结果、问题码与所针对的选择键）。</summary>
    internal (bool HasRange, string? Problem, string? Key) ShownRange
    {
        get { var shown = Read().State; return (shown.Range is not null, shown.RangeProblem, shown.RangeKey); }
    }

    /// <summary>测试用：主循环已经开始的战斗读取轮数。</summary>
    internal int CombatRounds => Volatile.Read(ref combatRounds);
    private int combatRounds;

    // ---- 可见性 ----

    private readonly Dictionary<string, int> surfaces = new(StringComparer.Ordinal);
    private bool surfacesReported;

    /// <summary>
    /// 界面报告某一处显示或隐藏（成对调用，按次数计）：<c>main</c> 主窗口、<c>mini</c> 迷你窗、<c>overlay</c> 任一浮窗元件、
    /// <c>overlay.combat</c> 战斗浮窗元件；页面与元件按所需数据报告 <c>music</c>（乐谱库、清单、队列与乐器）、<c>color</c>（染色方案与引擎）、
    /// <c>settings</c>（快捷键清单与设置）、<c>inventory</c>（背包快照与透视表）。变为可见时立即补读对应数据。
    /// </summary>
    public void SetSurfaceVisible(string surface, bool visible)
    {
        bool appeared;
        lock (gate)
        {
            // 只有窗口一级的可见处（主窗口、迷你窗、浮窗）才表示界面在报告可见性；单独使用的页面模型（测试、样本）不改变“全部可见”的默认。
            if (surface is "main" or "mini" or "overlay" or "overlay.combat") surfacesReported = true;
            var count = surfaces.GetValueOrDefault(surface);
            var next = Math.Max(0, count + (visible ? 1 : -1));
            surfaces[surface] = next;
            appeared = count == 0 && next > 0;
            if (appeared && surface == "inventory") inventoryDue = true;
            if (appeared && surface is "main" or "mini" or "overlay" or "music" or "color" or "settings") pagesDue = true;
        }
        if (!appeared) return;
        MarkDue(surface == "overlay.combat" ? Due.Snapshot | Due.Recompute | Due.Status : Due.Status, immediate: true);
    }

    // 调用方持有 gate。还没有窗口报告过可见性（测试、样本）时按全部可见处理。
    private bool Showing(string surface) => !surfacesReported || surfaces.GetValueOrDefault(surface) > 0;
    private bool AnySurface => !surfacesReported || surfaces.Values.Any(count => count > 0);
    private bool CombatWanted => !surfacesReported || combatPageVisible || combatDetailOpen || surfaces.GetValueOrDefault("overlay.combat") > 0;

    // ---- 等待下一项到期的工作 ----

    /// <summary>距下一项到期工作的时间；为零表示现在就有要做的。</summary>
    private TimeSpan NextWork()
    {
        lock (gate)
        {
            var now = Now;
            var next = long.MaxValue;
            void At(long when) => next = Math.Min(next, when);
            if ((due & Due.Status) != 0) At(now);
            At(lastStatusRead + Span(AnySurface ? StatusInterval : HiddenStatusInterval));
            if (startingObservation is not null) At(lastStatusRead + Span(StatusInterval));
            if (colorRunId is not null) At(lastColorRunRead + Span(StatusInterval));
            if (PagesDue(now)) At(now);
            else if (AnySurface) At(pagesReadAt + Span(TimeSpan.FromSeconds(5)));
            if (SurfacesWanted()) At(lastSurfacesRead + Span(StatusInterval));
            if ((due & Due.Capture) != 0) At(now);
            At(lastCaptureRead + Span(channelDetection?.State == "running" ? StatusInterval : SlowInterval));
            if ((due & Due.Room) != 0 || roomPreferencesDue) At(now);
            if (InRoom || AnySurface) At(lastRoomRead + Span(InRoom ? StatusInterval : SlowInterval));
            if (durations.HasWanted) At(now);
            if (ClientIconCatalogAt(now) is > 0 and var icons) At(icons);
            if (iconsInUse && PendingIcons().Count > 0) At(now);
            var wait = next - now;
            return wait <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(wait, Span(TimeSpan.FromSeconds(5))) / (double)Stopwatch.Frequency);
        }
    }

    // 调用方持有 gate。用户操作立即执行；事件与上一轮至少隔一帧（FrameInterval）。
    private long CombatReadyAt() => urgent ? Now : Math.Max(Now, lastCombatRound + Span(FrameInterval));

    /// <summary>距下一项到期的战斗读取的时间；为零表示现在就读。没有战斗视图可见时不读。</summary>
    private TimeSpan NextCombatWork()
    {
        lock (gate)
        {
            if (!CombatWanted) return TimeSpan.FromSeconds(5);
            var now = Now;
            var next = long.MaxValue;
            void At(long when) => next = Math.Min(next, when);
            if ((due & Due.Combat) != 0) At(CombatReadyAt());
            At(lastSnapshotRead + Span(SnapshotInterval));
            if (followUpAt != 0) At(followUpAt);
            var wait = next - now;
            return wait <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(wait, Span(TimeSpan.FromSeconds(5))) / (double)Stopwatch.Frequency);
        }
    }

    private async Task WaitForWorkAsync(CancellationToken token)
    {
        while (NextWork() is { } wait && wait > TimeSpan.Zero) await wake.WaitAsync(wait, token);
    }

    /// <summary>
    /// 战斗循环的等待：帧间隔以内的等待由高精度计时器按截止时间唤醒（期间到达的事件并入这一轮），更长的等待（兜底读取、补读）可被事件提前唤醒。
    /// </summary>
    private async Task WaitForCombatAsync(Task events, CancellationToken token)
    {
        while (!events.IsCompleted && NextCombatWork() is var wait && wait > TimeSpan.Zero)
        {
            if (wait <= FrameInterval + FrameInterval) await combatTimer.DelayAsync(wait, token);
            else await combatWake.WaitAsync(wait, token);
        }
    }

    // ---- 事件 ----

    /// <summary>十进制字符串或数字形式的修订；无法解析时为空。</summary>
    private static ulong? RevisionOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when ulong.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        JsonValueKind.Number when value.TryGetUInt64(out var number) => number,
        _ => null
    };

    /// <summary>事件信封的资源修订；没有或无法解析时为空。</summary>
    internal static (string? Resource, ulong? Revision) EventVersion(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object) return (null, null);
        var resource = envelope.TryGetProperty("resourceId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        ulong? revision = null;
        if (envelope.TryGetProperty("resourceRevision", out var value))
            revision = value.ValueKind switch
            {
                JsonValueKind.String when ulong.TryParse(value.GetString(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                JsonValueKind.Number when value.TryGetUInt64(out var number) => number,
                _ => null
            };
        return (resource, revision);
    }

    /// <summary>战斗事件对应要读的部分：已经读到同一观察的同一或更新修订时不读快照；效果事件只读列出的实体。</summary>
    private void ReceiveCombatEvent(StreamMessage message)
    {
        var (resource, revision) = EventVersion(message.Data);
        CombatWorkspace? workspace;
        string? target, aim;
        lock (gate) { workspace = state.Workspace; target = targetRead.Id; aim = aimRead.Id; }
        var known = workspace is { } current && resource == current.Observation.Id && revision is { } seen && seen <= current.Revision.Value;
        switch (message.Event)
        {
            case "combat.live.changed":
            {
                // 资源修订是宿主内的通知序号；所指的观察与工作区修订在负载里。
                if (!message.Data.TryGetProperty("data", out var data) || !data.TryGetProperty("observationId", out var id) || id.ValueKind != JsonValueKind.String ||
                    !data.TryGetProperty("workspaceRevision", out var value) || RevisionOf(value) is not { } live)
                { MarkDue(Due.Snapshot, immediate: false); break; }
                double? captured = data.TryGetProperty("capturedMonotonicMs", out var mono) && mono.ValueKind == JsonValueKind.Number ? mono.GetDouble() : null;
                RememberLive(id.GetString()!, live, captured);
                if (!(workspace is { } shown && shown.Observation.Id == id.GetString() && live <= shown.Revision.Value)) MarkDue(Due.Snapshot, immediate: false);
                break;
            }
            case "combat.summary.changed" or "combat.observation.degraded":
                if (!known) MarkDue(Due.Snapshot, immediate: false);
                break;
            case "combat.encounter.closed":
                MarkDue(Due.Snapshot | Due.History, immediate: false);
                break;
            case "combat.effects.changed":
            {
                IReadOnlyList<string>? changed = null;
                if (message.Data.TryGetProperty("data", out var data) && data.TryGetProperty("changedEntityIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                    changed = ids.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray();
                // 宿主至多列出 256 个实体，列满时可能有未列出的；恢复写出的效果事件不带列表。两种情况都按全部变化处理。
                if (changed is { Count: >= MaximumChangedEntities }) changed = null;
                var self = workspace?.SelfEffects?.Id;
                var flags = Due.None;
                if (!known && (changed is null || self is null || changed.Contains(self))) flags |= Due.Snapshot;
                if (changed is null || target is not null && changed.Contains(target) || aim is not null && changed.Contains(aim)) flags |= Due.TargetEffects;
                if (flags != Due.None) MarkDue(flags, immediate: false);
                break;
            }
            case "combat.measurement.changed":
            {
                CombatMeasurementState? value = null;
                try { value = message.Data.Deserialize<EventEnvelope<CombatMeasurementState>>(ContractJson.Options)?.Data; }
                catch (JsonException) { }
                if (value is null) { MarkDue(Due.Measurement, immediate: false); break; }
                ApplyMeasurement(value, Now);
                MarkDue(Due.Recompute, immediate: false);
                break;
            }
        }
    }

    /// <summary>效果事件 changedEntityIds 的上限（宿主 CombatHistoryStore 截取前 256 个）。</summary>
    private const int MaximumChangedEntities = 256;

    /// <summary>采用一份测量状态（事件携带或读取所得）：比已有的旧时不采用；新测量开始或出结果时切到它。</summary>
    private void ApplyMeasurement(CombatMeasurementState value, long at)
    {
        lock (gate)
        {
            if (measurementState is { } shown && shown.Revision.Value > value.Revision.Value && shown.MeasurementId == value.MeasurementId) return;
            var next = FollowMeasurement(selection.Scope, seenMeasurementRun, seenMeasurementResult, value);
            if (next.Scope != selection.Scope) selection.Follow(selection.Version, next.Scope);
            (seenMeasurementRun, seenMeasurementResult) = (next.SeenRun, next.SeenResult);
            measurementState = value;
            measurementAt = at;
        }
    }

    // ---- 映射结果的缓存 ----

    // 同一份状态只映射一次：一次通知里页面、明细窗口、各浮窗元件与外壳取到的是同一个对象，取用方据此判断有没有变化。
    private sealed class Memo<T> where T : class
    {
        private object?[]? key;
        private T? value;

        public T Get(object?[] next, Func<T> build)
        {
            lock (this)
                if (key is not null && Same(key, next)) return value!;
            var built = build();
            lock (this) { key = next; value = built; }
            return built;
        }

        private static bool Same(object?[] a, object?[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] is ValueType or string ? !Equals(a[i], b[i]) : !ReferenceEquals(a[i], b[i])) return false;
            return true;
        }
    }

    private readonly Memo<CombatPageData> combatMemo = new();
    private readonly Memo<Pages.Music.MusicPageData> musicMemo = new();
    private readonly Memo<Pages.Settings.SettingsPageData> settingsMemo = new();
    private readonly Memo<Pages.Modules.ModulesPageData> modulesMemo = new();
    private readonly Memo<Pages.Inventory.InventoryPageData> inventoryMemo = new();
    private readonly Memo<Pages.Rooms.RoomsPageData> roomsMemo = new();

    private object?[] CombatKey()
    {
        var health = HealthNotice();
        lock (gate)
            return [state, notice is null, selection.Version, autoObservation, startingResult, captureRuntime, captureSnapshot,
                CombatEffectFavorites.Version, Localization.UiText.Language, health];
    }
}
