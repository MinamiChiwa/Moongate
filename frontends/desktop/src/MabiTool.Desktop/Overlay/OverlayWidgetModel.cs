using System.Windows.Input;
using Avalonia.Threading;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Dyeing;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Rooms;

namespace MabiTool.Desktop.Overlay;

/// <summary>
/// 内建浮窗元件的数据：与主窗口页面读同一份后端状态，后端变化时重读。
/// 元件里的按钮以 <see cref="IntentCommand"/> 交出“名称:参数”形式的意图；元件在游戏浮窗上解锁时才接收输入。
/// </summary>
/// <summary>合奏信息浮窗里成员状态的语气：已就绪、未就绪、已离线、房主（免就绪）。</summary>
public enum ReadinessTone { Ready, Waiting, Offline, Host }

/// <summary>合奏信息浮窗里的一位成员：顺位、昵称、身份（总指挥另加标记）、乐器与状态。</summary>
public sealed record ReadinessMember(string Order, string Nickname, string Role, bool Conductor, string Instrument, string State,
    ReadinessTone Tone)
{
    /// <summary>昵称之后的次要部分：“ · 身份 · 乐器”。</summary>
    public string Detail => string.Concat(new[] { Role, Instrument }.Where(part => part.Length > 0).Select(part => " · " + part));
    public string ConductorMark => Conductor ? UiText.Pick(" · 总指挥", " · 總指揮") : string.Empty;
    public bool HasOrder => Order.Length > 0;
    public bool IsReady => Tone == ReadinessTone.Ready;
    public bool IsWaiting => Tone == ReadinessTone.Waiting;
    public bool IsMuted => Tone is ReadinessTone.Offline or ReadinessTone.Host;
}

/// <summary>一组成员：组标题与组内按顺位排列的成员。</summary>
public sealed record ReadinessGroup(string Title, IReadOnlyList<ReadinessMember> Members);

public sealed class OverlayWidgetModel : ObservableObject, IDisposable
{
    private readonly IDesktopBackend backend;
    private readonly string? topic;
    private readonly LocalizedText? title;
    // 合奏信息浮窗的倒计时与乐谱进度按本机单调时钟每 200 ms 重算；时间基准取后端例行刷新的最新值，不另发请求。
    // 只在元件显示、处于房间且本轮进行中时运行。
    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private RoomTiming? roomTiming;
    private RoomStatus? roomStatus;
    private bool shown;
    private bool editing;

    /// <param name="topic">元件依赖的展示接口主题；为空时元件始终显示。</param>
    /// <param name="title">元件名称；编辑状态下主题缺失时与说明一起显示。</param>
    public OverlayWidgetModel(string widgetId, IDesktopBackend backend, string? topic = null, LocalizedText? title = null)
    {
        WidgetId = widgetId;
        this.backend = backend;
        this.topic = topic;
        this.title = title;
        Read();
        IntentCommand = new RelayCommand(parameter =>
        {
            if (parameter is not string text || text.Length == 0) return;
            var split = text.IndexOf(':');
            _ = backend.SendIntentAsync(split < 0 ? text : text[..split], split < 0 ? null : text[(split + 1)..], CancellationToken.None);
        });
        backend.Changed += OnChanged;
        // Buff 收藏筛选与主页面共用一份状态，变化时随之重读。
        CombatEffectFavorites.Changed += OnChanged;
        clockTimer.Tick += (_, _) => OnClockTick();
    }

    /// <summary>元件内按钮的意图，参数写成“名称:参数”，与页面的意图命令相同。</summary>
    public ICommand IntentCommand { get; }

    public string WidgetId { get; }

    /// <summary>版面处于在屏幕上编辑的状态；编辑时依赖缺失主题的元件也要留在屏幕上，以便标注与摆放。</summary>
    public bool Editing
    {
        get => editing;
        set
        {
            if (Set(ref editing, value)) Raise(string.Empty);
        }
    }

    /// <summary>元件依赖的展示主题缺失时的说明；主题可用、未知或没有依赖时为空。</summary>
    public TopicNotice? Notice => topic is null ? null : backend.GetPresentationNotice(topic);

    /// <summary>
    /// 外框是否显示：台服没有该数据来源（provider.unavailable）时，游戏浮窗里整个元件不显示；
    /// 编辑状态下和其他缺失原因下保留外框。
    /// </summary>
    public bool FrameVisible => Notice is not { PlatformUnsupported: true } || editing;
    public bool HasNotice => Notice is not null && FrameVisible;
    public string NoticeText => Notice?.Text ?? string.Empty;

    /// <summary>编辑状态下，缺失主题的元件在说明上方显示自己的名称；平时游戏浮窗里只有说明。</summary>
    public bool ShowNoticeTitle => HasNotice && editing;
    public string NoticeTitle => title?.Current ?? WidgetId;
    private bool Live => Notice is null;

    public bool IsNowPlaying => Live && WidgetId == "performance.now-playing";
    public bool IsNearby => Live && WidgetId == "performance.nearby";
    public bool IsReadiness => Live && WidgetId == "rooms.readiness";
    public bool IsColor => Live && WidgetId == "color.current";
    public bool IsDps => Live && WidgetId == "combat.dps";
    public bool IsSkills => Live && WidgetId == "combat.skills";
    public bool IsCoverage => Live && WidgetId == "combat.coverage";
    public bool IsSelfEffects => Live && WidgetId == "combat.self-effects";
    public bool IsTargetEffects => Live && WidgetId == "combat.target-effects";
    public bool IsMeasurement => Live && WidgetId == "combat.measurement";
    public bool IsHealing => Live && WidgetId == "combat.healing";
    public bool IsFavorites => Live && FavoriteBoard.IsBoard(WidgetId);
    public bool IsAwakening => Live && WidgetId == AwakeningBoard.Id;

    // 收藏浮窗：选择、顺序、横竖排与觉醒监控取版面里这个元件的摆放数据（画布按摆放数据写入），内容取同一份战斗数据里不受筛选的部分。
    private WidgetPlacement? favoritePlacement;
    private OverlayLayoutStore? favoriteStore;
    private string favoriteSignature = string.Empty;

    /// <summary>画布写入收藏浮窗或觉醒监控浮窗的摆放数据与版面；选择、顺序或排列变了才通知界面。</summary>
    public void SetFavoriteSource(WidgetPlacement placement, OverlayLayoutStore store)
    {
        favoritePlacement = placement;
        favoriteStore = store;
        var signature = string.Join('|', FavoriteBoard.IsBoard(placement.WidgetId) ? string.Join(',', FavoriteBoard.Effective(placement)) : string.Empty,
            placement.Horizontal);
        if (signature == favoriteSignature) return;
        favoriteSignature = signature;
        foreach (var name in new[] { nameof(FavoriteItems), nameof(FavoritesHorizontal), nameof(AwakeningAlerts), nameof(FavoritesHint), nameof(HasFavoritesHint) })
            Raise(name);
    }

    /// <summary>收藏浮窗的各项（没有摆放数据，即单独使用模型时为空）；觉醒监控浮窗为固定的两项。</summary>
    public IReadOnlyList<FavoriteItem> FavoriteItems => IsAwakening ? AwakeningBoard.Items()
        : IsFavorites && favoritePlacement is { } placement ? FavoriteBoard.Items(placement, Combat) : [];
    public bool FavoritesHorizontal => favoritePlacement?.Horizontal == true;
    public AwakeningSettings? AwakeningAlerts => favoriteStore?.Awakening;
    /// <summary>没有勾选任何项时的说明。</summary>
    public string FavoritesHint => CombatEffectFavorites.Favorites.Count == 0
        ? UiText.Pick("在 DPS 页收藏效果后，到浮窗版面页勾选", "在 DPS 頁收藏效果後，到浮窗版面頁勾選")
        : UiText.Pick("到浮窗版面页勾选要显示的收藏", "到浮窗版面頁勾選要顯示的收藏");
    public bool HasFavoritesHint => IsFavorites && favoritePlacement is not null && FavoriteItems.Count == 0;

    /// <summary>浮窗上拖动排序松手：保存新顺序。</summary>
    public Action<string, int> ReorderFavorite => reorderFavorite ??= (key, index) =>
    {
        if (favoritePlacement is { } placement && favoriteStore is { } store) store.MoveFavoriteItem(placement, key, index);
    };
    private Action<string, int>? reorderFavorite;

    /// <summary>暴击、强击、连击、追击比率（当前统计范围，与 DPS 页同一份）；DPS 元件与定时测量元件在数值下方列出。</summary>
    public IReadOnlyList<CoverageTile> Rates => Combat.Coverage;
    /// <summary>定时测量元件只在页面范围是定时测量（计时中自动切到这次测量、完成后切到结果）时列出比率。</summary>
    public bool HasMeasureRates => Combat.Scope?.Kind == CombatScopeKind.Measurement && Rates.Count > 0 && !MeasureWaiting;

    // 治疗元件：与 DPS 页治疗卡片同一份数据（当前统计范围）；后端没有治疗块时只显示“未读取治疗数据”。
    public bool HealingAvailable => Combat.Healing is { Available: true };
    public string HealingHps => Combat.Healing?.Hps ?? "—";
    public string HealingCast => Combat.Healing?.Cast ?? "—";
    public string HealingCount => Combat.Healing?.CastCount ?? "—";
    public string HealingEffective => Combat.Healing?.Effective ?? "—";
    public static string HealingCastLabel => UiText.Pick("总治疗量", "總治療量");
    public static string HealingCountLabel => UiText.Pick("治疗次数", "治療次數");
    public static string HealingEffectiveLabel => UiText.Pick("受到治疗有效", "受到治療有效");
    /// <summary>统计范围：整场（到期后为“上一场”）、当前分段或定时测量。</summary>
    public string HealingScope => Combat.Scope?.Kind switch
    {
        CombatScopeKind.Segment => UiText.Pick("当前分段", "目前分段"),
        CombatScopeKind.Measurement => UiText.Pick("定时测量", "定時測量"),
        _ => Combat.Scope?.TitleTip.Length > 0 ? UiText.Pick("上一场", "上一場") : UiText.Pick("整场", "整場")
    };

    // 定时测量元件：空闲显示设定与最近一次结果，等待中显示“等待首次伤害”，计时显示剩余时间、进度、累计伤害与当前 DPS。
    // 剩余时间与页面相同，按本机单调时钟从后端读取时刻递减，元件显示且测量进行中时每 200 ms 重算。
    public MeasurementView Measure => Combat.Measurement ?? MeasurementView.Unavailable(string.Empty);
    public bool MeasureIdle => Measure.Phase == MeasurementPhase.Idle;
    public bool MeasureWaiting => Measure.Phase == MeasurementPhase.Waiting;
    public bool MeasureRunning => Measure.Phase == MeasurementPhase.Running;
    public string MeasureSettings => CombatPageModel.SettingsText(Measure.DurationMs);
    public string MeasureDuration => CombatPageModel.DurationText(Measure.DurationMs);
    private double? MeasureRemaining => Measure.RemainingMs is { } remaining
        ? Math.Max(0, remaining - (Measure.ReadAt == 0 ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(Measure.ReadAt).TotalMilliseconds)) : null;
    public string MeasureRunningText => MeasureRemaining is { } ms ? CombatPageModel.DurationText(Math.Ceiling(ms / 1000) * 1000) : "—";
    public double MeasureProgress => MeasureRemaining is { } ms && Measure.DurationMs > 0 ? Math.Clamp(1 - ms / Measure.DurationMs, 0, 1) : 0;
    public string MeasureDamage => Measure.Damage;
    public string MeasureDps => Measure.Dps;
    public bool HasMeasureResult => MeasureIdle && Measure.Last is not null;
    public MeasurementResultView? MeasureResult => Measure.Last;
    public string MeasureResultLine => Measure.Last is { } last
        ? UiText.Pick($"总伤害 {last.Damage} · {last.Length}", $"總傷害 {last.Damage} · {last.Length}") : string.Empty;
    public bool MeasureResultIncomplete => Measure.Last?.IsIncomplete == true;
    public string MeasureHint => Measure.StartHint;
    public bool HasMeasureHint => MeasureIdle && Measure.StartHint.Length > 0;
    public bool CanStartMeasure => Measure.CanStart;

    public CombatPageData Combat { get; private set; } = null!;
    public MusicPageData Music { get; private set; } = null!;
    public RoomsPageData Rooms { get; private set; } = null!;
    public ColorPageData Color { get; private set; } = null!;

    // 染色元件的剩余与已用时间：与染色页运行栏同一份进度锚点，按本机时钟重算，元件显示且寻色进行中时每 200 ms 刷新。
    public string ColorRemaining => Color.Progress?.RemainingText(DateTimeOffset.Now) ?? string.Empty;
    public bool HasColorRemaining => ColorRemaining.Length > 0;
    public bool ColorLowTime => Color.Progress?.Low(DateTimeOffset.Now) == true;
    public string ColorElapsed => Color.Progress?.ElapsedText(DateTimeOffset.Now) ?? string.Empty;
    public bool HasColorElapsed => ColorElapsed.Length > 0;

    public QueueRow? NowPlaying => Music.Queue.FirstOrDefault(row => row.Current);
    public string NowPlayingTitle => NowPlaying?.Title ?? UiText.Pick("没有演奏中的乐谱", "沒有演奏中的樂譜");
    public string NowPlayingNote => NowPlaying is { } row ? $"{row.Note}，{row.Length}" : Music.QueueSummary;
    public QueueRow? Upcoming => Music.Queue.SkipWhile(row => !row.Current).Skip(1).FirstOrDefault();
    public string UpcomingTitle => Upcoming?.Title ?? UiText.Pick("没有下一首", "沒有下一首");
    public string PrimaryDps => Combat.Kpis.FirstOrDefault(tile => tile.Primary)?.Value ?? "—";

    /// <summary>
    /// 版面里这个元件的“怪物血量”开关（画布按摆放数据写入）；默认显示。
    /// </summary>
    public bool ShowHealth
    {
        get => showHealth;
        set
        {
            if (Set(ref showHealth, value)) Raise(nameof(HealthExpanded));
        }
    }

    /// <summary>DPS 元件的血量区块展开：有当前目标（或能力停用说明）且开关打开。</summary>
    public bool HealthExpanded => IsDps && showHealth && Combat.Health is not null;

    /// <summary>血量区块显示的内容：当前目标的血量；目标清空后的收起过程中保留最后一次的内容。</summary>
    public TargetHealthView? HealthShown => Combat.Health ?? lastHealth;
    private TargetHealthView? lastHealth;
    private bool showHealth = true;
    /// <summary>战斗状态标签（战斗中／战斗外／空闲／切图中／未观测），与 DPS检测页工具栏的状态标签同一来源。</summary>
    public string CombatState => Combat.Location?.Activity ?? string.Empty;
    public string RollingDps => Combat.Kpis.Count > 1 ? $"{Combat.Kpis[1].Label} {Combat.Kpis[1].Value}" : string.Empty;
    /// <summary>主要 DPS 之外的其余指标；元件越高显示越多。</summary>
    public IReadOnlyList<KpiTile> OtherKpis => Combat.Kpis.Where(tile => !tile.Primary).ToList();
    // 列表全部交给元件，按元件尺寸显示能放下的部分，其余在元件内滚动。
    public IReadOnlyList<DamageRow> TopSkills => Combat.Skills;
    // 浮窗的效果元件跟随 DPS 页对应卡片的“只显示收藏”开关（筛选已在同一份数据里套用）：顺序与页面相同，收藏在前、其余按首次出现；
    // 推算已归零的不列。元件放不下时末行标“+N”，排在前面的收藏总是可见。
    public IReadOnlyList<EffectRow> SelfEffects => ShownEffects(Combat.SelfEffects);
    public IReadOnlyList<EffectRow> TargetEffects => ShownEffects(Combat.TargetEffects);
    internal static IReadOnlyList<EffectRow> ShownEffects(IReadOnlyList<EffectRow> rows)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        return rows.Where(row => !CombatEffectOrder.Expired(row, now)).ToList();
    }
    /// <summary>“只显示收藏”打开但还没有收藏任何效果：提示去 DPS 页收藏。</summary>
    public bool SelfFavoriteHint => CombatEffectFavorites.SelfOnly && CombatEffectFavorites.Favorites.Count == 0;
    public bool TargetFavoriteHint => CombatEffectFavorites.TargetOnly && CombatEffectFavorites.Favorites.Count == 0;
    public bool NoSelfEffectsShown => !SelfFavoriteHint && SelfEffects.Count == 0;
    public bool NoTargetEffectsShown => !TargetFavoriteHint && TargetEffects.Count == 0;
    public string TargetName => Combat.TargetChips.FirstOrDefault(chip => chip.Current)?.Label ?? UiText.Pick("没有目标", "沒有目標");
    // 合奏信息浮窗（原工具 InformationWindow）：房间与连接、阶段与倒计时、按组列出的成员、开始与就绪按钮；
    // 按钮转发与合奏页相同的意图，可见与可用取同一份 RoomFlow.Describe。
    // 阶段文字里的剩余秒数、倒计时与乐谱进度取同一份时间基准（与合奏页相同）。
    public RoomStatus RoomStatus => roomStatus ??= RoomFlow.Describe(Rooms with { Timing = roomTiming }, Rooms.Room?.Mode ?? "ensemble",
        Rooms.Preferences is { } preferences ? RoomLocalSettings.Effective(preferences) : null);
    public bool InRoom => Rooms.InRoom;
    public bool NotInRoom => !Rooms.InRoom;

    /// <summary>标题行：房间码，未入房时说明尚未加入。</summary>
    public string RoomTitle => Rooms.Room is { } room ? UiText.Pick($"房间 {room.Code}", $"房間 {room.Code}") : UiText.Pick("尚未加入房间", "尚未加入房間");
    public bool RoomConnected => Rooms.InRoom && Rooms.RoomConnected;
    public bool RoomDisconnected => Rooms.InRoom && !Rooms.RoomConnected;
    public string RoomConnection => !Rooms.InRoom ? string.Empty
        : Rooms.RoomConnected ? UiText.Pick("已连接", "已連線") : UiText.Pick("连接中断", "連線中斷");

    /// <summary>
    /// 倒计时进度（与合奏页状态卡相同）：开始前“距离同步开始”、已确认统一停止后“距离统一停止”与剩余秒数；
    /// 还没有时间基准时只显示“正在校时”。
    /// </summary>
    public bool HasRoomCountdown => Rooms.InRoom && RoomStatus.Countdown is not null;
    public string RoomCountdownLabel => RoomStatus.Countdown?.Label ?? string.Empty;
    public string RoomCountdownValue => RoomStatus.Countdown?.Value ?? string.Empty;
    public double RoomCountdownFraction => RoomStatus.Countdown?.Fraction ?? 0;
    public bool RoomCountdownStarting => RoomStatus.Countdown is { Stopping: false, Calibrating: false };
    public bool RoomCountdownStopping => RoomStatus.Countdown is { Stopping: true, Calibrating: false };

    /// <summary>总指挥乐谱的估计播放进度；没有完整乐谱时长时不显示。</summary>
    public bool HasRoomScoreProgress => Rooms.InRoom && RoomStatus.ScoreProgress is not null;
    public bool HasRoomScoreFraction => RoomStatus.ScoreProgress?.Fraction is not null;
    public double RoomScoreFraction => RoomStatus.ScoreProgress?.Fraction ?? 0;
    public string RoomScoreProgressText => RoomStatus.ScoreProgress?.Text ?? string.Empty;

    /// <summary>
    /// 成员按组分块：合奏按组号与顺位排列，每组标题“第 N 组”；表情动作没有分组，整体一块“成员”。
    /// 每位成员是“顺位 · 昵称 · 身份 · 乐器”，状态（已就绪、未就绪、已离线；房主免就绪显示“房主”）单独一列。
    /// </summary>
    public IReadOnlyList<ReadinessGroup> RoomGroups
    {
        get
        {
            if (Rooms.Room is not { } room) return [];
            if (room.Mode != "ensemble")
                return [new(UiText.Pick("成员", "成員"), room.Members.Select(member => Member(room, member, ensemble: false)).ToList())];
            return room.Members.GroupBy(member => member.Group).OrderBy(group => group.Key)
                .Select(group => new ReadinessGroup(UiText.Pick($"第 {group.Key} 组", $"第 {group.Key} 組"),
                    group.OrderBy(member => member.Order).Select(member => Member(room, member, ensemble: true)).ToList()))
                .ToList();
        }
    }

    private static ReadinessMember Member(RoomState room, RoomMember member, bool ensemble)
    {
        var tone = !member.Connected ? ReadinessTone.Offline
            : member.IsHost ? ReadinessTone.Host
            : member.Ready ? ReadinessTone.Ready : ReadinessTone.Waiting;
        var state = tone switch
        {
            ReadinessTone.Offline => UiText.Pick("已离线", "已離線"),
            ReadinessTone.Host => UiText.Pick("房主", "房主"),
            ReadinessTone.Ready => UiText.Pick("已就绪", "已就緒"),
            _ => UiText.Pick("未就绪", "未就緒")
        };
        var role = ensemble
            ? member.Initiator ? UiText.Pick("乐谱使用者", "樂譜使用者") : UiText.Pick("参与者", "參與者")
            : member.IsHost ? UiText.Pick("房主", "房主") : UiText.Pick("成员", "成員");
        return new ReadinessMember(ensemble ? member.Order.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
            member.Nickname, role, ensemble && member.Id == room.LeaderId, ensemble ? RoomFlow.InstrumentText(member.Instrument) : string.Empty,
            state, tone);
    }

    /// <summary>
    /// 左列给房主：没有进行中的本轮时是“开始”，开始后同一位置变为“停止”（倒计时内取消本轮 rooms.cancel，开始后全员停止 rooms.stop-all）。
    /// 右列给成员：平时是“就绪”（房主免就绪），本轮进行中变为“本机停止”或表情的“取消本机任务”（rooms.local-stop）。
    /// 两列宽度固定，状态变化时按钮不移动。
    /// </summary>
    public bool ShowRoomStart => Rooms.InRoom && RoomStatus.ShowStart && !RoomStatus.Active;
    public bool ShowRoomStop => Rooms.InRoom && (RoomStatus.ShowCancel || RoomStatus.ShowStopAll);
    public bool CanRoomStop => RoomStatus.ShowCancel ? RoomStatus.CanCancel : RoomStatus.CanStopAll;
    public string RoomStopIntent => RoomStatus.ShowCancel ? "rooms.cancel" : "rooms.stop-all";
    public bool ShowRoomLocalStop => Rooms.InRoom && !Rooms.IsHost && RoomStatus.ShowLocalStop;
    public string RoomLocalStopLabel => RoomStatus.LocalStopLabel;
    public bool ShowRoomReady => Rooms.InRoom && RoomStatus.ShowReady && !Rooms.IsHost && !ShowRoomLocalStop;
    public bool RoomReadyOn => RoomStatus.ReadyOn;
    public bool RoomReadyOff => !RoomStatus.ReadyOn;
    // 两种文字叠放，按较长的一种定宽，切换时只换显示的那一种。
    public double ReadyOffOpacity => RoomStatus.ReadyOn ? 0 : 1;
    public double ReadyOnOpacity => RoomStatus.ReadyOn ? 1 : 0;
    public string RoomReadyIntent => RoomStatus.ReadyOn ? "rooms.ready:off" : "rooms.ready:on";
    public bool HasRoomScore => RoomStatus.ScoreCard.Length > 0;
    public bool NoSkills => TopSkills.Count == 0;

    /// <summary>
    /// 元件是否在画面上显示；由视图在挂上可见窗口与离开时设置，决定倒计时定时器是否运行，并向后端报告可见处
    /// （隐藏的元件不为它拉数据）。隐藏期间到达的变化在重新显示时补读一次。
    /// </summary>
    public void SetShown(bool value)
    {
        shownReported = true;
        if (shown != value)
        {
            shown = value;
            backend.SetSurfaceVisible("overlay", value);
            if (DataSurface is { } surface) backend.SetSurfaceVisible(surface, value);
            if (value && stale && Read()) Raise(string.Empty);
        }
        UpdateClock();
    }

    private bool Combatant => WidgetId.StartsWith("combat.", StringComparison.Ordinal);

    // 元件所需数据对应的可见处：战斗元件要战斗数据，演奏中与合奏元件要乐谱与队列，染色元件要染色状态。
    private string? DataSurface => Combatant ? "overlay.combat"
        : WidgetId is "performance.now-playing" or "rooms.readiness" ? "music"
        : WidgetId == "color.current" ? "color" : null;
    private bool stale, shownReported;
    private TopicNotice? shownNotice;

    /// <summary>倒计时定时器是否在运行。</summary>
    public bool ClockRunning => clockTimer.IsEnabled;

    public void Dispose()
    {
        backend.Changed -= OnChanged;
        CombatEffectFavorites.Changed -= OnChanged;
        SetShown(false);
        clockTimer.Stop();
    }

    // 隐藏的元件只记下有变化；显示的元件只重读自己用到的数据，数据对象没变（后端适配层对同一状态返回同一对象）时不通知。
    private void OnChanged(object? sender, EventArgs e)
    {
        // 视图还没报告过显示状态（单独使用模型时）按显示处理。
        if (shownReported && !shown) { stale = true; return; }
        if (Read()) Raise(string.Empty);
        UpdateClock();
    }

    private bool Read()
    {
        var first = (object?)Combat is null;
        var changed = false;
        if (first || Combatant) changed |= Swap<CombatPageData>(Combat!, backend.GetCombat(), value => Combat = value);
        if (first || WidgetId == "performance.now-playing") changed |= Swap<MusicPageData>(Music!, backend.GetMusic(), value => Music = value);
        if (first || WidgetId == "rooms.readiness") changed |= Swap<RoomsPageData>(Rooms!, backend.GetRooms(), value => Rooms = value);
        if (first || WidgetId == "color.current") changed |= Swap<ColorPageData>(Color!, backend.GetColor(), value => Color = value);
        var notice = Notice;
        changed |= !Equals(notice, shownNotice);
        shownNotice = notice;
        stale = false;
        if (changed)
        {
            roomTiming = Rooms.Timing;
            roomStatus = null;
            if (Combat?.Health is { } health) lastHealth = health;
        }
        return changed;
    }

    private static bool Swap<T>(T current, T next, Action<T> assign) where T : class
    {
        if (ReferenceEquals(current, next)) return false;
        assign(next);
        return true;
    }

    private void UpdateClock()
    {
        var run = shown && (IsReadiness && Rooms.Room?.Round is { Active: true } || IsMeasurement && MeasureRunning ||
            IsColor && Color.Progress is not null);
        if (run && !clockTimer.IsEnabled) clockTimer.Start();
        else if (!run && clockTimer.IsEnabled) clockTimer.Stop();
    }

    // 只重算与时间有关的显示，成员列表等不随定时器重建。
    private void OnClockTick()
    {
        roomTiming = backend.GetRoomTiming();
        roomStatus = null;
        foreach (var name in ClockProperties) Raise(name);
    }

    private static readonly string[] ClockProperties =
    [
        nameof(RoomStatus), nameof(HasRoomCountdown), nameof(RoomCountdownLabel), nameof(RoomCountdownValue), nameof(RoomCountdownFraction),
        nameof(RoomCountdownStarting), nameof(RoomCountdownStopping), nameof(HasRoomScoreProgress), nameof(HasRoomScoreFraction),
        nameof(RoomScoreFraction), nameof(RoomScoreProgressText), nameof(MeasureRunningText), nameof(MeasureProgress),
        nameof(ColorRemaining), nameof(HasColorRemaining), nameof(ColorLowTime), nameof(ColorElapsed), nameof(HasColorElapsed)
    ];
}
