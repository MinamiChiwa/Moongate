using System.Diagnostics;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Combat;

namespace MabiTool.Desktop.Overlay;

/// <summary>
/// 收藏浮窗（“收藏浮窗 1”“收藏浮窗 2”）的项：键、默认归属与实际显示的列表。键为 <c>self:编号</c>、<c>target:编号</c>；
/// 选择与顺序存在元件的摆放数据（<see cref="WidgetPlacement.FavoriteItems"/>）里。觉醒监控是独立元件（<see cref="AwakeningBoard"/>）。
/// 没有保存过选择时，“收藏浮窗 1”包含全部收藏（增益为自身，减益为目标，未分类为自身），“收藏浮窗 2”为空。
/// 收藏浮窗只取收藏，不受 DPS 页“只显示收藏”开关影响。
/// </summary>
public static class FavoriteBoard
{
    public const string First = "combat.favorites-1", Second = "combat.favorites-2";
    public const string SelfPrefix = "self:", TargetPrefix = "target:";
    /// <summary>一个元件至多保存的项数（收藏目录上限 4096 的自身与目标远超实际可显示的数量）。</summary>
    public const int MaximumItems = 512;

    public static bool IsBoard(string widgetId) => widgetId is First or Second;

    /// <summary>键是否合法：自身或目标加非空编号。早期版本的觉醒键（<c>awakening:</c>）不合法，读取时去掉。</summary>
    public static bool IsValidKey(string? key) =>
        key is not null && (key.StartsWith(SelfPrefix, StringComparison.Ordinal) && key.Length > SelfPrefix.Length
            || key.StartsWith(TargetPrefix, StringComparison.Ordinal) && key.Length > TargetPrefix.Length);

    /// <summary>效果项的编号；不是效果项时为空。</summary>
    public static string? EffectId(string key) =>
        key.StartsWith(SelfPrefix, StringComparison.Ordinal) ? key[SelfPrefix.Length..]
        : key.StartsWith(TargetPrefix, StringComparison.Ordinal) ? key[TargetPrefix.Length..] : null;

    public static bool IsTarget(string key) => key.StartsWith(TargetPrefix, StringComparison.Ordinal);

    /// <summary>新收藏的默认项：增益勾自身，减益勾目标，未分类勾自身。</summary>
    public static IReadOnlyList<string> Defaults(string effectId) =>
        CombatEffectFavorites.PolarityOf(effectId) == EffectPolarity.Negative ? [TargetPrefix + effectId] : [SelfPrefix + effectId];

    /// <summary>
    /// 元件实际显示的项（按顺序）：保存的选择（没有时按默认），只留仍在收藏里的效果项。
    /// </summary>
    public static List<string> Effective(WidgetPlacement placement)
    {
        var favorites = CombatEffectFavorites.Favorites;
        IEnumerable<string> source = placement.FavoriteItems
            ?? (placement.WidgetId == First ? favorites.Order(StringComparer.Ordinal).SelectMany(Defaults) : []);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in source)
        {
            if (EffectId(key) is { } id && favorites.Contains(id) && seen.Add(key)) result.Add(key);
        }
        return result;
    }

    /// <summary>名称：收藏目录里的游戏原文，缺失时按“未知效果 #编号”。</summary>
    public static string NameOf(string effectId) =>
        CombatEffectFavorites.NameOf(effectId) ?? UiText.Pick($"未知效果 #{effectId}", $"未知效果 #{effectId}");

    /// <summary>按当前数据给出元件的各项（显示内容在绘制时按本机时钟另算）。</summary>
    public static IReadOnlyList<FavoriteItem> Items(WidgetPlacement placement, CombatPageData data)
    {
        var self = data.AllSelfEffects.Where(row => row.EffectId is not null).GroupBy(row => row.EffectId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var aim = data.AimEffects.Where(row => row.EffectId is not null).GroupBy(row => row.EffectId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var result = new List<FavoriteItem>();
        foreach (var key in Effective(placement))
        {
            var id = EffectId(key)!;
            var target = IsTarget(key);
            var tag = target ? UiText.Pick("目标", "目標") : UiText.Pick("自身", "自身");
            EffectRow? row = null;
            var predicted = false;
            if (target) aim.TryGetValue(id, out row);
            else if (!self.TryGetValue(id, out row) && FavoriteEffectTracker.Predicted(id) is { } remembered) (row, predicted) = (remembered, true);
            var polarity = row?.Polarity is { } known && known != EffectPolarity.Unknown ? known : CombatEffectFavorites.PolarityOf(id);
            result.Add(new(key, target ? FavoriteItemKind.Target : FavoriteItemKind.Self, row?.Name is { Length: > 0 } name && !predicted ? name : NameOf(id),
                tag, polarity, row, predicted));
        }
        return result;
    }
}

/// <summary>
/// 觉醒监控浮窗（<c>combat.awakening</c>）：独立元件，只有“徽章觉醒”一项（职业觉醒不监控），状态取 <see cref="FavoriteEffectTracker"/>；
/// 横竖排存在摆放数据的 <see cref="WidgetPlacement.Horizontal"/>，提示设置在版面文件顶层（<see cref="OverlayLayoutStore.Awakening"/>）。
/// DPS 元件打开“觉醒监控”（<see cref="WidgetPlacement.HideAwakening"/> 为假）时显示同一项、使用同一份提示设置。
/// </summary>
public static class AwakeningBoard
{
    public const string Id = "combat.awakening";
    public const string DpsId = "combat.dps";
    public const string BadgeKey = "awakening:badge";

    public static string BadgeTitle => UiText.Pick("徽章觉醒", "徽章覺醒");

    /// <summary>“徽章觉醒”一项；显示内容在绘制时按本机时钟由 <see cref="FavoriteItem.Describe"/> 从觉醒状态算出。</summary>
    public static IReadOnlyList<FavoriteItem> Items() =>
        [new(BadgeKey, FavoriteItemKind.Badge, BadgeTitle, string.Empty, EffectPolarity.Positive, null, false)];

    /// <summary>
    /// 觉醒提示音只在这时播放：当前版面有未关闭的觉醒监控浮窗，或有未关闭且打开了觉醒显示的 DPS 元件。
    /// 两者同时存在时一次触发仍只通知一次（<see cref="FavoriteEffectTracker.Triggered"/> 每次触发只发一次）。
    /// </summary>
    public static bool Watched(OverlayLayoutStore store) => store.Active.Widgets.Any(widget => !widget.Closed
        && (widget.WidgetId == Id || widget.WidgetId == DpsId && !widget.HideAwakening));
}

public enum FavoriteItemKind { Self, Target, Badge }

/// <summary>
/// 收藏浮窗的一项：键、种类、名称、标记（自身／目标）、极性与此刻的效果行（没有出现时为空）。
/// <paramref name="Predicted"/> 为换图后按记下的到期时间推算的自身效果。
/// </summary>
public sealed record FavoriteItem(string Key, FavoriteItemKind Kind, string Name, string Tag, EffectPolarity Polarity, EffectRow? Row, bool Predicted)
{
    /// <summary>此刻绘制的内容：名称、标记、层数、剩余文字、附注（推算、冷却）、进度（0–1，没有时为空）、是否警示、是否变暗、是否闪烁中、极性与觉醒项触发时刻。</summary>
    public FavoriteLook Describe(long now)
    {
        if (Kind == FavoriteItemKind.Badge)
        {
            var state = FavoriteEffectTracker.Describe(now);
            return state.Phase switch
            {
                AwakeningPhase.Active => new(state.Badge is { Length: > 0 } badge ? $"{Name}·{badge}" : Name, string.Empty, string.Empty,
                    Seconds(state.RemainingMs), string.Empty, state.Progress, false, false, EffectPolarity.Positive, false, state.TriggerTick),
                AwakeningPhase.Cooldown => new(Name, string.Empty, string.Empty, Seconds(state.RemainingMs),
                    state.Predicted ? UiText.Pick("冷却·推算", "冷卻·推算") : UiText.Pick("冷却", "冷卻"), state.Progress, false, false,
                    EffectPolarity.Neutral, false, state.TriggerTick),
                _ => new(Name, string.Empty, string.Empty, string.Empty, string.Empty, null, false, true, EffectPolarity.Positive, false, state.TriggerTick)
            };
        }
        if (Row is not { } row) return new(Name, Tag, string.Empty, string.Empty, string.Empty, null, false, true, Polarity, false, 0);
        var remaining = CombatEffectOrder.Remaining(row, now);
        if (remaining is <= 0) return new(Name, Tag, string.Empty, string.Empty, string.Empty, null, false, true, Polarity, false, 0);
        var text = row.Infinite ? UiText.Pick("持续", "持續") : remaining is { } ms ? Seconds(ms) : UiText.Pick("未知", "未知");
        return new(Name, Tag, row.Stacks is { } stacks and > 1 ? $"×{stacks}" : string.Empty, text,
            Predicted ? UiText.Pick("推算", "推算") : string.Empty, CombatEffectOrder.Progress(row, now), remaining is < EffectLine.WarningMs, false,
            Polarity, row.Infinite, 0);
    }

    private static string Seconds(double? ms) => ms is { } value
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Max(0, Math.Ceiling(value / 1000)):0}s") : string.Empty;
}

public readonly record struct FavoriteLook(string Name, string Tag, string Stacks, string Remaining, string Note, double? Progress, bool Warning,
    bool Dim, EffectPolarity Polarity, bool Infinite, long TriggerTick);

/// <summary>觉醒项的阶段：未觉醒、觉醒中、冷却中。</summary>
public enum AwakeningPhase { Idle, Active, Cooldown }

/// <summary>觉醒项此刻的状态：阶段、触发的徽章名、剩余时间、进度、冷却是否推算与触发时刻（本机 Stopwatch）。</summary>
public readonly record struct AwakeningView(AwakeningPhase Phase, string? Badge, double? RemainingMs, double? Progress, bool Predicted, long TriggerTick);

/// <summary>
/// 收藏浮窗的状态记忆，整个前端一份，由后端适配层在 <see cref="CombatEffectFavorites.Apply"/> 里随每份新数据更新：
/// <list type="bullet">
/// <item>自身收藏效果的到期时间：换地图后宿主清空效果（效果消失而没有收到到期移除、剩余还多于 1 秒），按记下的到期时间继续推算，
/// 直到到期、收到该效果的到期移除或重新收到该效果；目标的项不保留。</item>
/// <item>徽章觉醒：按后端效果视图的觉醒标记（按 Buff Id 判定）识别触发、觉醒剩余与冷却；收到冷却效果时按它的实际时长，
/// 否则从触发时刻按 90 秒推算。1 秒内的再次触发只算一次。后端的职业觉醒标记（class、class-cooldown）不监控。</item>
/// </list>
/// </summary>
public static class FavoriteEffectTracker
{
    /// <summary>后端给徽章觉醒效果的标记；徽章符文冷却效果为 <c>badge-cooldown</c>。</summary>
    public const string Badge = "badge";
    public const double BadgeCooldownMs = 90_000;
    /// <summary>同一项在这么短的时间里再次触发只算一次（觉醒效果与冷却效果先后到达、或同一效果的刷新）。</summary>
    public const double RepeatMs = 1000;
    /// <summary>效果消失时剩余多于这么久才按换图推算；更短的按到期处理。</summary>
    public const double PredictMinimumMs = 1000;
    /// <summary>施加时刻早于这么久的觉醒（程序启动或观测开始前已在生效）只记状态，不闪烁、不提示。</summary>
    public const double FreshMs = 5000;

    private sealed class SelfMemory(EffectRow row)
    {
        public EffectRow Row = row;
        public bool Predicted;
    }

    private sealed class State
    {
        public DateTimeOffset? Applied;
        public long TriggerTick, ActiveUntil, CooldownUntil;
        public double? ActiveTotalMs, CooldownTotalMs;
        public bool CooldownActual;
        public string? BadgeName;
    }

    private static readonly object gate = new();
    private static readonly Dictionary<string, SelfMemory> self = new(StringComparer.Ordinal);
    private static readonly State badge = new();

    /// <summary>测试用的时钟：本机 Stopwatch 时刻与墙上时间。</summary>
    internal static Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;
    internal static Func<DateTimeOffset> WallClock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>徽章觉醒新触发；1 秒内的再次触发不重复通知。可能在任一线程上触发。</summary>
    public static event Action? Triggered;

    /// <summary>测试用：清空记忆并恢复时钟。</summary>
    internal static void Reset()
    {
        lock (gate)
        {
            self.Clear();
            badge.Applied = null; badge.TriggerTick = badge.ActiveUntil = badge.CooldownUntil = 0;
            badge.ActiveTotalMs = badge.CooldownTotalMs = null; badge.CooldownActual = false; badge.BadgeName = null;
            Clock = Stopwatch.GetTimestamp;
            WallClock = () => DateTimeOffset.UtcNow;
        }
    }

    private static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000);
    private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    private static long? Expiry(EffectRow row) => row.Infinite || row.RemainingMs is not { } ms ? null : row.ObservedAt + Ticks(ms);

    public static void Observe(CombatPageData data)
    {
        bool fired;
        lock (gate)
        {
            var now = Clock();
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in data.AllSelfEffects)
            {
                if (row.EffectId is not { } id || !present.Add(id)) continue;
                if (CombatEffectFavorites.IsFavorite(id)) self[id] = new(row);
                else self.Remove(id);
            }
            foreach (var (id, memory) in self.ToList())
            {
                if (present.Contains(id)) continue;
                var remaining = CombatEffectOrder.Remaining(memory.Row, now);
                // 收到到期移除（移除的观测时刻不早于最后一次看到它）：效果已结束。
                var removed = data.SelfRemoved.TryGetValue(id, out var at) && (memory.Row.SeenAt is not { } seen || at >= seen);
                if (removed || remaining is not { } left || left <= 0 || !memory.Predicted && (left <= PredictMinimumMs || !data.Observing)
                    || !CombatEffectFavorites.IsFavorite(id))
                    self.Remove(id);
                else memory.Predicted = true;
            }
            fired = Track(badge, data, now);
        }
        if (fired) Triggered?.Invoke();
    }

    private static bool Track(State state, CombatPageData data, long now)
    {
        const string activeRole = Badge, cooldownRole = Badge + "-cooldown";
        var actives = data.AllSelfEffects.Where(row => row.Awakening == activeRole).ToList();
        var cooldowns = data.AllSelfEffects.Where(row => row.Awakening == cooldownRole).ToList();
        var fire = false;
        var activeRow = actives.OrderByDescending(row => row.AppliedAt).FirstOrDefault();
        var cooldownRow = cooldowns.OrderByDescending(row => row.AppliedAt).FirstOrDefault();
        // 上一次的觉醒或冷却已经结束、同一效果又以更晚的到期出现：宿主没收到到期移除时施加时刻不更新，按到期时间判定为再次触发。
        var renewed = state.TriggerTick != 0
            && (activeRow is not null && Expiry(activeRow) is { } a && now >= state.ActiveUntil && a > state.ActiveUntil + Ticks(RepeatMs)
                || cooldownRow is not null && Expiry(cooldownRow) is { } c && now >= state.CooldownUntil && c > state.CooldownUntil + Ticks(RepeatMs));
        if (actives.Concat(cooldowns).Max(row => row.AppliedAt) is { } applied
            && (state.Applied is not { } last || applied > last.AddMilliseconds(RepeatMs) || renewed))
        {
            // 触发时刻按施加时刻回推（同一台机器上的宿主时间），至多回推一小时；按到期判定的再次触发取此刻。
            var elapsed = renewed && state.Applied is { } previous && applied <= previous.AddMilliseconds(RepeatMs)
                ? 0 : Math.Clamp((WallClock() - applied).TotalMilliseconds, 0, 3_600_000);
            var trigger = now - Ticks(elapsed);
            var repeated = state.TriggerTick != 0 && Math.Abs(Ms(trigger - state.TriggerTick)) < RepeatMs;
            state.Applied = applied;
            if (!repeated)
            {
                state.TriggerTick = trigger;
                state.BadgeName = null;
                state.ActiveUntil = 0; state.ActiveTotalMs = null;
                state.CooldownUntil = trigger + Ticks(BadgeCooldownMs); state.CooldownTotalMs = BadgeCooldownMs; state.CooldownActual = false;
                fire = elapsed < FreshMs;
            }
        }
        if (activeRow is { } active && Expiry(active) is { } until)
        {
            if (state.ActiveTotalMs is null || until > state.ActiveUntil + Ticks(RepeatMs)) state.ActiveTotalMs = active.RemainingMs;
            state.ActiveUntil = until;
            state.BadgeName = active.Name;
        }
        if (cooldownRow is { } cooling && Expiry(cooling) is { } end)
        {
            if (!state.CooldownActual || end > state.CooldownUntil + Ticks(RepeatMs)) state.CooldownTotalMs = cooling.RemainingMs;
            state.CooldownUntil = end; state.CooldownActual = true;
        }
        return fire;
    }

    /// <summary>换图后仍在推算的自身收藏效果（剩余按记下的到期时间递减）；没有时为空。</summary>
    public static EffectRow? Predicted(string effectId)
    {
        lock (gate)
            return self.TryGetValue(effectId, out var memory) && memory.Predicted && CombatEffectOrder.Remaining(memory.Row, Clock()) is > 0
                ? memory.Row : null;
    }

    /// <summary>徽章觉醒此刻的状态。</summary>
    public static AwakeningView Describe(long now)
    {
        lock (gate)
        {
            var state = badge;
            if (state.TriggerTick == 0) return new(AwakeningPhase.Idle, null, null, null, false, 0);
            if (now < state.ActiveUntil)
            {
                var left = Ms(state.ActiveUntil - now);
                return new(AwakeningPhase.Active, state.BadgeName, left, state.ActiveTotalMs is > 0 and var total ? Math.Clamp(left / total, 0, 1) : null,
                    false, state.TriggerTick);
            }
            if (now < state.CooldownUntil)
            {
                var left = Ms(state.CooldownUntil - now);
                return new(AwakeningPhase.Cooldown, state.BadgeName, left,
                    state.CooldownTotalMs is > 0 and var total ? Math.Clamp(left / total, 0, 1) : null, !state.CooldownActual, state.TriggerTick);
            }
            return new(AwakeningPhase.Idle, null, null, null, false, state.TriggerTick);
        }
    }
}

/// <summary>“徽章觉醒”的提示设置：闪烁开关、颜色与时长，音效开关、声音（空为内置合成提示音）与音量。</summary>
public sealed class AwakeningAlert
{
    public const double MinimumSeconds = 1, MaximumSeconds = 10, SecondsStep = 1, VolumeStep = 0.1;
    public bool Flash { get; set; } = true;
    public string Color { get; set; } = string.Empty;
    public double FlashSeconds { get; set; } = 3;
    public bool Sound { get; set; }
    /// <summary>本机 wav 或 mp3 文件；为空时用工具运行时合成的提示音。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SoundFile { get; set; }
    public double Volume { get; set; } = 0.8;

    public void Normalize(string defaultColor)
    {
        Color = ColorMath.NormalizeHex(Color) ?? defaultColor;
        FlashSeconds = double.IsFinite(FlashSeconds) ? Math.Clamp(Math.Round(FlashSeconds), MinimumSeconds, MaximumSeconds) : 3;
        Volume = double.IsFinite(Volume) ? Math.Clamp(Math.Round(Volume * 10) / 10, 0, 1) : 0.8;
        SoundFile = string.IsNullOrWhiteSpace(SoundFile) ? null : SoundFile.Trim();
    }

    public bool IsDefault(string defaultColor) =>
        Flash && Color == defaultColor && FlashSeconds == 3 && !Sound && SoundFile is null && Volume == 0.8;
}

/// <summary>
/// 觉醒提示设置，保存在版面文件顶层（全部为默认值时不写出），格式与早期版本相同：只有 <c>Badge</c>。
/// 觉醒监控浮窗与 DPS 元件的觉醒显示读写同一个实例。早期版本的 <c>Class</c>（职业觉醒）读取时忽略，保存时不再写出。
/// </summary>
public sealed class AwakeningSettings
{
    public const string BadgeColor = "#FFC14D";
    public AwakeningAlert Badge { get; set; } = new() { Color = BadgeColor };

    public void Normalize()
    {
        Badge ??= new();
        Badge.Normalize(BadgeColor);
    }

    public bool IsDefault => Badge.IsDefault(BadgeColor);
}
