using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Controls.Tables;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Combat;

/// <summary>主数据的一块：标签、数值、是否主指标，以及数值下方的一行说明（例如最近 60 秒 DPS、实际输出时间）。</summary>
public sealed record KpiTile(string Label, string Value, bool Primary, string Note = "")
{
    public bool HasNote => Note.Length > 0;
}

public sealed record CoverageTile(string Name, string Percent, double Ratio, string Note);

/// <summary>
/// 技能或目标的一行伤害统计。强击、连击、追击对应后端的 powerfulHit、rapidHit、doubleHit；
/// 后端未提供某项计数时该列显示「不适用」。名称是游戏原文，不随界面语言转换。
/// 无防备、破防、普攻、多重打击对应 breakedHit、breakOccurred、defaultAttack、multiHit，
/// 与 <see cref="Elements"/> 一样以全部已归属伤害次数（eventCount）为分母；这五列只在本场有任一项已知时显示。
/// 首击不算比例，本场最近的一次单独列在 <see cref="FirstAttackRow"/> 里。
/// </summary>
public sealed record DamageRow(string Name, string Damage, string Share, string Hits, string Critical,
    string Strong, string Combo, string Chase, string Range, string Unguarded = "", string BreakOccurred = "",
    string DefaultAttack = "", string MultiHit = "", string Elements = "", string NameTip = "", TagCells? Tags = null, double ShareRatio = 0)
{
    /// <summary>名称的悬停提示：名称缺失时含技能或目标编号，其余为名称本身。</summary>
    public string Tip => NameTip.Length > 0 ? NameTip : Name;

    /// <summary>总表的标签单元格（顺序见 <see cref="CombatMapper.Tags"/>，每个标签两格：次数与占比、伤害与占比）；没有时为空串。</summary>
    public string TagCell(int index) => Tags is { } tags && index < tags.Values.Count ? tags.Values[index] : string.Empty;
}

/// <summary>一行的全部标签单元格；按内容比较，刷新时内容不变的行不被替换。</summary>
public sealed record TagCells(IReadOnlyList<string> Values)
{
    public bool Equals(TagCells? other) => other is not null && Values.SequenceEqual(other.Values);
    public override int GetHashCode() => Values.Count == 0 ? 0 : HashCode.Combine(Values.Count, Values[0]);
}

/// <summary>一次首击：技能名称（游戏原文，缺失时为“未知技能”）、伤害，以及观测到的目标；两个提示在名称缺失时含编号与原因。</summary>
public sealed record FirstAttackRow(string Skill, string Damage, string Target, string SkillTip = "", string TargetTip = "")
{
    public bool HasTarget => Target.Length > 0;
    public string SkillHint => SkillTip.Length > 0 ? SkillTip : Skill;
    public string TargetHint => TargetTip.Length > 0 ? TargetTip : Target;
}

/// <summary>整场元素构成里的一种元素：<see cref="Label"/> 是名称与占比，<see cref="ColorHex"/> 是固定的色块颜色。</summary>
public sealed record ElementPart(string Label, double Weight, string ColorHex)
{
    public BarSegment Segment => new(Weight, new SolidColorBrush(Color.Parse(ColorHex)));
}

/// <summary>整场元素构成；<see cref="Parts"/> 为空表示已知但没有任何元素。</summary>
public sealed record ElementBreakdown(IReadOnlyList<ElementPart> Parts)
{
    public bool None => Parts.Count == 0;
}

public sealed record AttributionPart(string Label, double Weight, string ColorHex, bool Counted)
{
    public BarSegment Segment => new(Weight, new SolidColorBrush(Color.Parse(ColorHex)));
}

/// <summary>效果的极性：正面（客户端 Buff 表 AffectType=Buff）、负面（Debuff）、表里标为无（None），以及没有来源的未知。</summary>
public enum EffectPolarity { Unknown, Positive, Negative, Neutral }

/// <summary>
/// 一个效果。<see cref="EffectId"/> 是后端的效果编号，素材包按它提供效果图标；没有图标时显示字章。<see cref="Tip"/> 是名称的悬停提示（含编号）。
/// 默认图标是将棋棋子形的五边形：正面蓝色尖部朝上，负面红色尖部朝下，未分类为灰色。
/// </summary>
/// <param name="Stacks">合并后的层数（各实例层数之和）；未知时为空。</param>
/// <param name="RemainingMs">读取快照时最短的有限剩余时间；常驻或未知时为空。</param>
/// <param name="ObservedAt">读取快照时的本机 <see cref="System.Diagnostics.Stopwatch"/> 时刻，剩余时间从这里按本地时钟递减。</param>
/// <param name="Infinite">全部实例都是常驻效果。</param>
/// <param name="Scope">效果所在实体的范围键（自身为 self，怪物为实体 ID），与效果编号一起确定出现顺序与总时长。</param>
public sealed record EffectRow(string Glyph, string Name, string Meta, string Remaining, string Coverage, string? EffectId = null,
    string Tip = "", EffectPolarity Polarity = EffectPolarity.Unknown, int? Stacks = null, double? RemainingMs = null,
    long ObservedAt = 0, bool Infinite = false, string Scope = "")
{
    /// <summary>倒计时进度的总时长：首次观测到的剩余时间，续期时重置（<see cref="CombatEffectOrder"/> 给出）。</summary>
    public double? TotalMs { get; init; }
    /// <summary>首次出现的顺序号（越小越早）。</summary>
    public long Order { get; init; }
    public bool Favorite { get; init; }
    private static readonly IBrush PositiveFill = new SolidColorBrush(Color.Parse("#2F6FD6"));
    private static readonly IBrush NegativeFill = new SolidColorBrush(Color.Parse("#D2393B"));
    private static readonly IBrush OtherFill = new SolidColorBrush(Color.Parse("#7D8597"));

    /// <summary>默认图标的底色（色块数据）：正面蓝、负面红、未分类灰。</summary>
    public IBrush Fill => Polarity switch { EffectPolarity.Positive => PositiveFill, EffectPolarity.Negative => NegativeFill, _ => OtherFill };
    /// <summary>负面效果的五边形尖部朝下。</summary>
    public double Angle => Polarity == EffectPolarity.Negative ? 180 : 0;
    public string Hint => Tip.Length > 0 ? Tip : Name;
}

/// <param name="Tip">目标名称的悬停提示：名称缺失时含编号与原因。</param>
public sealed record TargetChip(string Id, string Label, bool Current, bool InScope, string Tip = "")
{
    public string Hint => Tip.Length > 0 ? Tip : Label;
    /// <summary>折叠成一行时也必须显示：当前效果目标。</summary>
    public bool Pinned => Current;
}

/// <summary>怪物效果区目标行末尾的展开项：折叠时显示“+N”，展开时显示“收起”。</summary>
public sealed class EffectTargetMore
{
    public static readonly EffectTargetMore Instance = new();
    public bool Pinned => false;
}

/// <summary>治疗统计的一块：标签、数值与说明（对象、来源或过量是否可得）。</summary>
public sealed record HealingTile(string Label, string Value, string Note)
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// 本场治疗统计。<see cref="Available"/> 为 false 表示后端没有提供治疗块（来源不支持或旧记录），
/// 页面显示“未提供治疗数据”，不显示 0。
/// </summary>
/// <param name="Header">口径：本人施放的治疗与 HPS 的分母（统计时长）。</param>
/// <param name="Note">选中怪物目标时写明治疗与目标无关、按整个范围统计；其余为空。</param>
public sealed record HealingView(bool Available, IReadOnlyList<HealingTile> Tiles, string Header = "", string Note = "")
{
    public bool HasNote => Note.Length > 0;
    /// <summary>浮窗治疗元件用的几项：HPS、总治疗量（本人施放）、施放次数、受到治疗的有效比例（未配对时为“未知”）。</summary>
    public string Hps { get; init; } = "—";
    public string Cast { get; init; } = "—";
    public string CastCount { get; init; } = "—";
    public string Effective { get; init; } = "—";
}

/// <summary>
/// 治疗明细表的一行：施放目标（本人标“本人”）或“受到的治疗（合计）”；治疗量、占比、次数、HPS、有效治疗与溢出。
/// 没有来源的项为“未知”，不适用的项为“不适用”，不显示 0。
/// </summary>
/// <param name="NameTip">名称的悬停提示：其他玩家名称缺失时为“未知玩家（编号 N）”。</param>
public sealed record HealingTargetRow(string Name, string Amount, string Share, string Count, string NameTip = "")
{
    public string Tip => NameTip.Length > 0 ? NameTip : Name;
    public string Hps { get; init; } = "—";
    public string Effective { get; init; } = "—";
    public string Overheal { get; init; } = "—";
    /// <summary>有效与溢出为“未知”时的原因。</summary>
    public string EffectiveTip { get; init; } = string.Empty;
    public string EffectiveHint => EffectiveTip.Length > 0 ? EffectiveTip : Effective;
}

public sealed record EncounterRow(string Id, string When, string Target, string Length, string Dps, string Quality,
    bool Favorite, string Segment = "", string Record = "")
{
    /// <summary>定时测量或保存的记录：“定时测量 1:00”或记录名称；自动分段为空。</summary>
    public bool HasRecord => Record.Length > 0;
    public bool HasSegment => Segment.Length > 0;
    public string FavoriteLabel => Favorite ? "已收藏" : "收藏";
}

/// <summary>各项操作此刻是否可用，由后端状态推出；不可用的按钮显示为停用。</summary>
public sealed record CombatActions(bool CanStart, bool CanStop, bool CanEnd, bool CanSplit, bool CanReset, bool CanClean);

/// <summary>
/// 开始观测后的结果：<see cref="Waiting"/> 为真表示后端已受理、正在等待识别本机角色（可取消）；为假表示开始失败，
/// <see cref="Text"/> 写明原因。观测已经开始或没有发起时为空。
/// </summary>
public sealed record ObservationStartView(bool Waiting, string Text);

/// <summary>
/// 常驻自动观测：<see cref="Enabled"/> 为后端偏好；<see cref="Text"/> 是此刻的观测状态（等待识别角色、未抓包及原因、开始失败等），
/// 观测进行中为空。自动观测开启时不需要“开始观测”。
/// </summary>
public sealed record AutoObservationView(bool Enabled, string Text, bool Problem);

/// <summary>统计范围：整场（本次观测自最近一次重设起的全部分段）、当前分段、定时测量结果。</summary>
public enum CombatScopeKind { Whole, Segment, Measurement }

/// <summary>定时测量的阶段：空闲（显示设定）、等待首次伤害、计时。</summary>
public enum MeasurementPhase { Idle, Waiting, Running }

/// <summary>
/// 定时测量的显示数据。<see cref="RemainingMs"/> 是后端在读取时刻给出的计时剩余（等待首次伤害时为空），界面按本机单调时钟从
/// <see cref="ReadAt"/>（<see cref="System.Diagnostics.Stopwatch"/> 时刻）起递减；<see cref="Damage"/> 与 <see cref="Dps"/>
/// 为计时中的累计，未知时为“—”。<see cref="StartHint"/> 是不能开始时的原因。
/// </summary>
public sealed record MeasurementView(MeasurementPhase Phase, int DurationMs, double? RemainingMs, long ReadAt,
    string Damage, string Dps, bool CanStart, string StartHint, bool CanCancel, string Incomplete, MeasurementResultView? Last,
    ulong? SettingsRevision = null)
{
    public static MeasurementView Unavailable(string hint) => new(MeasurementPhase.Idle, 60000, null, 0, "—", "—", false, hint, false, "", null);
}

/// <summary>最近一次完成的定时测量：DPS（总伤害 ÷ 设定时长）、总伤害、设定时长、实际输出时间与不完整说明。</summary>
public sealed record MeasurementResultView(string Id, string Dps, string Damage, string Length, string Active, string Incomplete)
{
    public bool IsIncomplete => Incomplete.Length > 0;
}

/// <summary>工具栏右侧的战斗状态标签；地图与频道是全局信息，显示在主窗口侧栏。</summary>
public sealed record LocationView(string Activity, ActivityTone Tone);

/// <summary>状态标签的语气：战斗中、空闲、切图中、未观测。</summary>
public enum ActivityTone { Fighting, Idle, Switching, Off }

/// <summary>“定时测量结果”范围下可选的一条记录：最近一次结果与历史中保存的测量。</summary>
public sealed record MeasurementOption(string Id, string Label, bool Current);

/// <summary>
/// 范围的显示：当前范围、标题（定时测量时为“定时测量 1:00 · 时刻”）、可选的测量记录，以及结果不完整时的说明。
/// <see cref="Problem"/> 是范围读取失败或没有数据时的一句话。
/// </summary>
public sealed record ScopeView(CombatScopeKind Kind, string Title, IReadOnlyList<MeasurementOption> Measurements, string Incomplete,
    string Problem)
{
    public bool IsIncomplete => Incomplete.Length > 0;
    public bool HasProblem => Problem.Length > 0;
    /// <summary>标题的悬停提示：整场到期后写明原因与何时开始新的一场；没有时为标题本身。</summary>
    public string TitleTip { get; init; } = string.Empty;
    public string TitleHint => TitleTip.Length > 0 ? TitleTip : Title;
}

/// <summary>
/// “选择目标”的一个标签：全部（Id 为空）或一个目标个体；同名目标按个体分开。<see cref="Tip"/> 是悬停提示（名称缺失时含目标编号）。
/// </summary>
public sealed record RangeTargetChip(string Id, string Name, string Damage, string Share, bool Current, string Tip = "")
{
    public bool IsAll => Id.Length == 0;
    /// <summary>折叠成一行时也必须显示：“全部”与选中的目标。</summary>
    public bool Pinned => IsAll || Current;
}

/// <summary>目标标签末尾的展开项：折叠时显示“+N”，展开时显示“收起”。</summary>
public sealed class RangeTargetMore
{
    public static readonly RangeTargetMore Instance = new();
    public bool Pinned => false;
}

/// <summary>选中单个目标时的详情：目标名、对它的伤害与我的贡献（占范围内全部伤害）。</summary>
public sealed record TargetDetailView(string Name, string Damage, string Contribution, double Ratio, string NameTip = "")
{
    public string Tip => NameTip.Length > 0 ? NameTip : Name;
}

/// <summary>
/// 破防期间（命中带无防备标记）的累计：伤害、次数、每击平均；持续时间、区间次数、时间占比与平均 DPS 需要破防开始与结束事件，暂为“未知”。
/// </summary>
public sealed record BreakView(string Damage, string Count, string Average, string Dps, string Duration, string Intervals, string Share,
    string Note);

/// <summary>
/// 战斗页数据。<see cref="Traits"/> 是“攻击特性”四块（无防备、破防、普攻、多重打击），本场全部未知时为空；
/// <see cref="Elements"/> 是整场元素构成，未知时为空；<see cref="ShowTraitColumns"/> 表示明细表是否显示新增的五列；
/// <see cref="FirstAttack"/> 是本场最近的一次首击，没有首击、事件未知或查不到时为空。
/// </summary>
public sealed record CombatPageData(string Status, IReadOnlyList<KpiTile> Kpis, IReadOnlyList<CoverageTile> Coverage,
    IReadOnlyList<AttributionPart> Attribution, string AttributionNote,
    IReadOnlyList<DamageRow> Skills, IReadOnlyList<DamageRow> Targets,
    IReadOnlyList<EffectRow> SelfEffects, IReadOnlyList<TargetChip> TargetChips,
    IReadOnlyList<EffectRow> TargetEffects, IReadOnlyList<EncounterRow> History, CombatActions Actions,
    IReadOnlyList<CoverageTile>? Traits = null, ElementBreakdown? Elements = null, bool ShowTraitColumns = false,
    FirstAttackRow? FirstAttack = null, HealingView? Healing = null, IReadOnlyList<HealingTargetRow>? HealingTargets = null,
    IReadOnlyList<CoverageTile>? Classes = null, MeasurementView? Measurement = null, LocationView? Location = null, ScopeView? Scope = null,
    IReadOnlyList<RangeTargetChip>? RangeTargets = null, TargetDetailView? TargetDetail = null, BreakView? Break = null,
    bool CanSave = false, string SaveHint = "", string DefaultRecordName = "", ObservationStartView? Starting = null,
    AutoObservationView? Auto = null)
{
    /// <summary>怪物效果区的说明：还没有命中怪物、目标已不在当前观测中或效果读取被拒绝；正常显示时为空。</summary>
    public string TargetEffectsNote { get; init; } = string.Empty;

    /// <summary>总表要显示的标签（<see cref="CombatMapper.Tags"/> 的标识）：暴击、强击、连击、追击总在；攻击特性与首击在有已知数据时，元素在有出现时。</summary>
    public IReadOnlyList<string> TagIds { get; init; } = [];

    /// <summary>此刻的范围选择（observation、segment 或 measurement:&lt;记录&gt;）与所选目标；主页面、明细窗口与浮窗共用这一个选择。</summary>
    public string SelectedScope { get; init; } = "segment";
    public string? SelectedTarget { get; init; }

    /// <summary>
    /// 刚切换范围或目标、新选择的结果还没读到：页面保留上一次的数值与列表，只把选中标记移到新选择上，结果到达后就地更新，不清空重建。
    /// </summary>
    public bool Pending { get; init; }

    /// <summary>自身与怪物效果卡片“只显示收藏”时没有可显示的收藏效果；整体没有效果时为假。</summary>
    public bool SelfFavoritesEmpty { get; init; }
    public bool TargetFavoritesEmpty { get; init; }
}

/// <summary>
/// DPS检测页的界面状态：只保存「按技能／按目标」「只看已收藏」这类显示选择。
/// 数据来自后端，后端状态变化时整页数据重新读取；页面上的列表是固定集合（<see cref="StableList{T}"/>），
/// 刷新时只就地替换变化的项，不重建项容器。按钮只把意图交给后端。切走页面时外壳保留它并停止接收通知（<see cref="IPageLifecycle"/>）。
/// </summary>
public sealed partial class CombatPageModel : ObservableObject, IDisposable, IPageLifecycle
{
    private readonly IDesktopBackend backend;
    private readonly bool page;
    // 明细窗口的伴随模型：范围与目标是窗口自己的选择，读窗口的数据并只额外接收窗口的通知。
    private readonly bool detail;
    private bool byTarget;
    private bool favoritesOnly;
    private bool stopping;
    private int stopRequest;
    private bool timerOpen, saveOpen, resetOpen;
    private ulong? timerSettingsRevision;
    private string timerMinutes = "1", timerSeconds = "0", timerError = string.Empty, recordName = string.Empty;
    // 计时的剩余时间按本机单调时钟每 250 ms 重算；读取时刻与剩余量取后端最近一次状态，不另发请求。
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool attached;

    /// <param name="page">DPS检测页本身为真；明细窗口的伴随模型为假，不改变页面的显示状态。</param>
    /// <param name="detail">明细窗口的伴随模型：范围与目标走窗口自己的选择，与主页面互不影响。</param>
    public CombatPageModel(IDesktopBackend backend, bool page = true, bool detail = false)
    {
        this.backend = backend;
        this.page = page;
        this.detail = detail;
        Data = Fetch();
        AttributionSegments = Data.Attribution.Select(part => part.Segment).ToList();
        ElementSegments = (Data.Elements?.Parts ?? []).Select(part => part.Segment).ToList();
        SyncLists();
        SyncFavorites();
        if (page) backend.SetCombatPageVisible(true);
        ShowSkillsCommand = new RelayCommand(_ => ByTarget = false);
        ShowTargetsCommand = new RelayCommand(_ => ByTarget = true);
        StartCommand = Intent(CombatIntent.StartObservation);
        StopCommand = new RelayCommand(_ =>
        {
            // 后端停止观察最多再等 2 秒：状态变化前显示“正在停止”并停用按钮，不按瞬时完成处理。
            if (stopping) return;
            SetStopping(true);
            var id = ++stopRequest;
            Send(CombatIntent.StopObservation, null);
            StopWait.Expire(() => { if (id == stopRequest) SetStopping(false); });
        }, _ => !stopping);
        EndCommand = Intent(CombatIntent.EndEncounter);
        SplitCommand = Intent(CombatIntent.SplitEncounter);
        ResetCommand = Intent(CombatIntent.ResetEncounter);
        CleanCommand = Intent(CombatIntent.CleanHistory);
        // 怪物效果区的目标名与“选择目标”是同一个选择：按下后整页以该目标为基准。
        SelectTargetCommand = new RelayCommand(chip => Intent("combat.target", (chip as TargetChip)?.Id));
        FavoriteCommand = new RelayCommand(row => Send(CombatIntent.ToggleFavorite, (row as EncounterRow)?.Id));
        ToggleFavoritesOnlyCommand = new RelayCommand(_ =>
        {
            favoritesOnly = !favoritesOnly;
            Raise(nameof(FavoritesOnly));
            History.Sync(FilteredHistory());
            Raise(nameof(NoHistory));
        });
        SelectScopeCommand = new RelayCommand(parameter => SelectScope(parameter as string));
        SelectMeasurementCommand = new RelayCommand(option => Intent("combat.scope", "measurement:" + (option as MeasurementOption)?.Id));
        BackToLiveCommand = new RelayCommand(_ => Intent("combat.scope.live", null));
        SelectRangeTargetCommand = new RelayCommand(chip => Intent("combat.target", (chip as RangeTargetChip)?.Id));
        StartMeasureCommand = new RelayCommand(_ => SendMeasure("combat.measure.start"));
        CancelMeasureCommand = new RelayCommand(_ => SendMeasure("combat.measure.cancel"));
        CancelObservationCommand = new RelayCommand(_ => Send(CombatIntent.StopObservation, null));
        OpenTimerCommand = new RelayCommand(_ => OpenTimer());
        CloseTimerCommand = new RelayCommand(_ => SetLayer(ref timerOpen, false, nameof(IsTimerOpen)));
        ApplyTimerCommand = new RelayCommand(_ => ApplyTimer());
        OpenSaveCommand = new RelayCommand(_ =>
        {
            RecordName = Data.DefaultRecordName;
            SetLayer(ref saveOpen, true, nameof(IsSaveOpen));
        });
        CloseSaveCommand = new RelayCommand(_ => SetLayer(ref saveOpen, false, nameof(IsSaveOpen)));
        SaveRecordCommand = new RelayCommand(_ =>
        {
            if (!CanSaveRecord) return;
            Intent("combat.save", RecordName.Trim());
            SetLayer(ref saveOpen, false, nameof(IsSaveOpen));
        });
        OpenResetCommand = new RelayCommand(_ => SetLayer(ref resetOpen, true, nameof(IsResetOpen)));
        CloseResetCommand = new RelayCommand(_ => SetLayer(ref resetOpen, false, nameof(IsResetOpen)));
        ConfirmResetCommand = new RelayCommand(_ =>
        {
            SetLayer(ref resetOpen, false, nameof(IsResetOpen));
            Send(CombatIntent.ResetEncounter, null);
        });
        clock.Tick += (_, _) => RaiseClock();
        backend.Changed += OnBackendChanged;
        if (detail) backend.CombatDetailChanged += OnBackendChanged;
        CombatEffectFavorites.Changed += OnFavoritesChanged;
        CombatEffectFavorites.CatalogChanged += OnCatalogChanged;
    }

    private CombatPageData Fetch() => detail ? backend.GetCombatDetail() : backend.GetCombat();

    // ---- 开始观测的结果：等待识别角色（可取消）或失败原因 ----

    public ICommand CancelObservationCommand { get; }
    public ObservationStartView? Starting => Data.Starting;
    public bool StartWaiting => Data.Starting is { Waiting: true };
    public bool StartFailed => Data.Starting is { Waiting: false };
    public string StartText => Data.Starting?.Text ?? string.Empty;

    /// <summary>自动观测开关与状态说明；开启时不显示“开始观测”。</summary>
    public bool AutoEnabled => Data.Auto?.Enabled == true;
    public bool HasAuto => Data.Auto is not null;
    public bool HasAutoText => Data.Auto is { Text.Length: > 0 } && Data.Starting is null;
    public string AutoText => Data.Auto?.Text ?? string.Empty;
    public bool AutoProblem => Data.Auto?.Problem == true;
    public string AutoToggleText => AutoEnabled ? UiText.Pick("自动观测：开", "自動觀測：開") : UiText.Pick("自动观测：关", "自動觀測：關");
    public ICommand ToggleAutoCommand => toggleAuto ??= new RelayCommand(_ => Intent("combat.auto", AutoEnabled ? "off" : "on"));
    private ICommand? toggleAuto;

    // ---- 工具栏：定时测量、保存记录、重设、位置与状态 ----

    public ICommand SelectScopeCommand { get; }
    public ICommand SelectMeasurementCommand { get; }
    public ICommand BackToLiveCommand { get; }
    public ICommand SelectRangeTargetCommand { get; }
    public ICommand StartMeasureCommand { get; }
    public ICommand CancelMeasureCommand { get; }
    public ICommand OpenTimerCommand { get; }
    public ICommand CloseTimerCommand { get; }
    public ICommand ApplyTimerCommand { get; }
    public ICommand OpenSaveCommand { get; }
    public ICommand CloseSaveCommand { get; }
    public ICommand SaveRecordCommand { get; }
    public ICommand OpenResetCommand { get; }
    public ICommand CloseResetCommand { get; }
    public ICommand ConfirmResetCommand { get; }

    public MeasurementView Measure => Data.Measurement ?? MeasurementView.Unavailable(string.Empty);
    public bool MeasureIdle => Measure.Phase == MeasurementPhase.Idle;
    /// <summary>已开始测量、等待本机第一次有效伤害：显示“等待首次伤害”，可取消，不显示旧结果。</summary>
    public bool MeasureWaiting => Measure.Phase == MeasurementPhase.Waiting;
    public bool MeasureRunning => Measure.Phase == MeasurementPhase.Running;
    public bool CanStartMeasure => Measure.CanStart && !measureBusy;
    /// <summary>开始或取消请求在等待后端回应：两个按钮停用，不重复提交；回应、失败或超时后恢复。</summary>
    public bool MeasureBusy => measureBusy;
    public bool CanCancelMeasure => !measureBusy;
    private bool measureBusy;

    private async void SendMeasure(string intent)
    {
        if (measureBusy) return;
        measureBusy = true;
        Raise(nameof(MeasureBusy)); Raise(nameof(CanStartMeasure)); Raise(nameof(CanCancelMeasure));
        try { await backend.SendIntentAsync(intent, null, CancellationToken.None); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally
        {
            measureBusy = false;
            Raise(nameof(MeasureBusy)); Raise(nameof(CanStartMeasure)); Raise(nameof(CanCancelMeasure));
        }
    }
    public bool HasMeasureHint => MeasureIdle && Measure.StartHint.Length > 0;
    public string MeasureHint => Measure.StartHint;
    /// <summary>组合按钮左半边：设定的时长，例如“1:00”。</summary>
    public string TimerLabel => DurationText(Measure.DurationMs);
    /// <summary>空闲时的设定说明，例如“1:00 · 从首次伤害开始计时”。</summary>
    public string TimerSettingsText => SettingsText(Measure.DurationMs);
    /// <summary>计时中的累计伤害与当前 DPS；还没有伤害时为“暂无数据”。</summary>
    public string RunningDetail => Measure.Damage == "—"
        ? UiText.Pick("暂无数据", "暫無資料")
        : UiText.Pick($"伤害 {Measure.Damage} · DPS {Measure.Dps}", $"傷害 {Measure.Damage} · DPS {Measure.Dps}");

    /// <summary>此刻的阶段剩余（毫秒），按读取后经过的本机时间递减，不小于 0；后端没有给出时为空。</summary>
    public double? RemainingMs => Measure.RemainingMs is { } remaining
        ? Math.Max(0, remaining - (Measure.ReadAt == 0 ? 0 : Stopwatch.GetElapsedTime(Measure.ReadAt).TotalMilliseconds)) : null;
    /// <summary>计时中的剩余时间“0:42”。</summary>
    public string RunningText => RemainingMs is { } ms ? DurationText(Math.Ceiling(ms / 1000) * 1000) : "—";
    /// <summary>计时进度 0–1。</summary>
    public double RunningProgress => RemainingMs is { } ms && Measure.DurationMs > 0 ? Math.Clamp(1 - ms / Measure.DurationMs, 0, 1) : 0;

    public static string DurationText(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        // 测量时长至多 60 分钟，按“分:秒”写，60 分钟为“60:00”。
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    /// <summary>设定说明：时长与“从首次伤害开始计时”（预备倒计时已停用）。</summary>
    public static string SettingsText(int durationMs) =>
        UiText.Pick($"{DurationText(durationMs)} · 从首次伤害开始计时", $"{DurationText(durationMs)} · 從首次傷害開始計時");

    // 设定层：分与秒；时长 5 秒–60 分钟，越界时不保存并写明范围。
    public bool IsTimerOpen => timerOpen;
    public string TimerMinutes { get => timerMinutes; set { if (Set(ref timerMinutes, value)) ClearTimerError(); } }
    public string TimerSeconds { get => timerSeconds; set { if (Set(ref timerSeconds, value)) ClearTimerError(); } }
    public string TimerError => timerError;
    public bool HasTimerError => timerError.Length > 0;

    private void OpenTimer()
    {
        timerSettingsRevision = Measure.SettingsRevision;
        var duration = TimeSpan.FromMilliseconds(Measure.DurationMs);
        timerMinutes = ((int)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        timerSeconds = duration.Seconds.ToString(CultureInfo.InvariantCulture);
        timerError = string.Empty;
        Raise(nameof(TimerMinutes)); Raise(nameof(TimerSeconds)); Raise(nameof(TimerError)); Raise(nameof(HasTimerError));
        SetLayer(ref timerOpen, true, nameof(IsTimerOpen));
    }

    /// <summary>把设定层的输入换算成毫秒；不合法或越界时返回空。</summary>
    public static int? ParseTimer(string minutes, string seconds)
    {
        static int? Whole(string text) => text.Trim().Length == 0 ? 0
            : int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
        if (Whole(minutes) is not { } m || Whole(seconds) is not { } s || s > 59 || m > 60) return null;
        var duration = (m * 60 + s) * 1000;
        return duration is < 5000 or > 3_600_000 ? null : duration;
    }

    private void ApplyTimer()
    {
        if (ParseTimer(timerMinutes, timerSeconds) is not { } value)
        {
            timerError = UiText.Pick("时长为 5 秒至 60 分钟", "時長為 5 秒至 60 分鐘");
            Raise(nameof(TimerError)); Raise(nameof(HasTimerError));
            return;
        }
        if (timerSettingsRevision is not { } revision)
        {
            timerError = UiText.Pick("测量设定尚未读取，请刷新后再试", "測量設定尚未讀取，請重新整理後再試");
            Raise(nameof(TimerError)); Raise(nameof(HasTimerError));
            return;
        }
        Intent("combat.measure.settings", string.Create(CultureInfo.InvariantCulture, $"{value},0,{revision}"));
        SetLayer(ref timerOpen, false, nameof(IsTimerOpen));
    }

    private void ClearTimerError()
    {
        if (timerError.Length == 0) return;
        timerError = string.Empty;
        Raise(nameof(TimerError)); Raise(nameof(HasTimerError));
    }

    // 保存记录层：名称 1–60 字，默认取范围与时刻。
    public bool IsSaveOpen => saveOpen;
    public string RecordName { get => recordName; set { if (Set(ref recordName, value)) Raise(nameof(CanSaveRecord)); } }
    public bool CanSaveRecord => Data.CanSave && RecordName.Trim().Length is > 0 and <= 60;
    public bool CanSave => Data.CanSave;

    // 重设确认层。
    public bool IsResetOpen => resetOpen;

    private void SetLayer(ref bool field, bool value, string name)
    {
        if (field == value) return;
        field = value;
        Raise(name);
    }

    /// <summary>工具栏右侧的战斗状态标签。</summary>
    public LocationView Location => Data.Location ?? new(UiText.Pick("未观测", "未觀測"), ActivityTone.Off);
    public bool Fighting => Location.Tone == ActivityTone.Fighting;
    public bool Switching => Location.Tone == ActivityTone.Switching;
    public bool Resting => Location.Tone is ActivityTone.Idle or ActivityTone.Off;

    // ---- 范围与目标 ----

    public ScopeView Scope => Data.Scope ?? new(CombatScopeKind.Whole, string.Empty, [], string.Empty, string.Empty);
    public bool ScopeWhole => Scope.Kind == CombatScopeKind.Whole;
    public bool ScopeSegment => Scope.Kind == CombatScopeKind.Segment;
    public bool ScopeMeasurement => Scope.Kind == CombatScopeKind.Measurement;
    /// <summary>范围标题：定时测量的记录名，或当前分段已结束时的“上一段（结束原因）”。</summary>
    public bool HasScopeTitle => Scope.Title.Length > 0;
    public bool HasMeasurementOptions => ScopeMeasurement && Scope.Measurements.Count > 0;
    public IReadOnlyList<RangeTargetChip> RangeTargets => Data.RangeTargets ?? [];
    public bool HasRangeTargets => RangeTargets.Count > 1;
    /// <summary>目标标签加末尾的展开项；折叠成一行时放不下的个数由面板写在展开项上。</summary>
    public StableList<object> RangeTargetItems { get; } = [];
    public StableList<MeasurementOption> MeasurementOptions { get; } = [];

    /// <summary>
    /// 怪物效果区的目标行（与“选择目标”同一顺序）加末尾的“+N”：始终只占一行，“+N”打开浮层列出全部目标，
    /// 展开不推挤卡片（卡片高度固定）。<see cref="TargetChipList"/> 是浮层里的全部目标。
    /// </summary>
    public StableList<object> TargetChipItems { get; } = [];
    public StableList<TargetChip> TargetChipList { get; } = [];
    public string TargetEffectsNote => Data.TargetEffectsNote;
    public bool HasTargetEffectsNote => Data.TargetEffectsNote.Length > 0;

    /// <summary>目标标签是否展开为多行；默认折叠成一行，属于界面显示选择。</summary>
    public bool TargetsExpanded
    {
        get => targetsExpanded;
        private set { if (Set(ref targetsExpanded, value)) Raise(nameof(TargetsToggleTip)); }
    }
    private bool targetsExpanded;
    public string TargetsToggleTip => targetsExpanded ? UiText.Pick("收起，只显示一行", "收起，只顯示一行") : UiText.Pick("展开全部目标", "展開全部目標");
    public ICommand ToggleTargetsCommand => toggleTargets ??= new RelayCommand(_ => TargetsExpanded = !TargetsExpanded);
    private ICommand? toggleTargets;
    public TargetDetailView? TargetDetail => Data.TargetDetail;
    public bool HasTargetDetail => Data.TargetDetail is not null;
    public BreakView? Break => Data.Break;
    public bool HasBreak => Data.Break is not null;
    public StableList<CoverageTile> Classes { get; } = [];
    public bool HasClasses => Classes.Count > 0;
    /// <summary>
    /// 主数据下方的紧凑比率表：暴击、强击、连击、追击，已知时的无防备、破防、普攻、多重打击，以及间接与持续伤害；
    /// 整场、当前分段与定时测量用同一份，分母口径与明细表相同。
    /// </summary>
    public StableList<CoverageTile> Rates { get; } = [];

    private void SelectScope(string? kind)
    {
        if (kind == "measurement")
        {
            // 定时测量：取当前选中的测量，没有时取进行中或最近一次结果；重设清除最近一次结果后不自动选历史记录（历史测量仍可在列表里选）。
            var id = Scope.Measurements.FirstOrDefault(option => option.Current)?.Id ?? Measure.Last?.Id
                ?? (Measure.Phase != MeasurementPhase.Idle ? Scope.Measurements.FirstOrDefault()?.Id : null);
            Intent("combat.scope", id is null ? "measurement" : "measurement:" + id);
            return;
        }
        Intent("combat.scope", kind is "segment" ? "segment" : "observation");
    }

    /// <summary>页面挂上可见窗口与离开时调用；只有显示且处于预备或计时中时，剩余时间定时器才运行。</summary>
    public void SetAttached(bool value)
    {
        if (value && !effectTicking) { EffectTicker.Tick += OnEffectTick; effectTicking = true; }
        else if (!value && effectTicking) { EffectTicker.Tick -= OnEffectTick; effectTicking = false; }
        attached = value;
        UpdateClock();
    }

    private bool effectTicking;
    // 倒计时节拍：推算归零的效果从各区移除（行本身的进度与文字由 EffectLine 自行重绘）。
    private void OnEffectTick(object? sender, EventArgs e) => PruneEffects(System.Diagnostics.Stopwatch.GetTimestamp());

    /// <summary>按 <paramref name="now"/> 移除推算已归零的效果；返回是否有行被移除。</summary>
    internal bool PruneEffects(long now)
    {
        var changed = false;
        foreach (var strip in new[] { SelfPositive, SelfNegative, SelfOther, TargetPositive, TargetNegative, TargetOther })
            changed |= strip.Prune(now);
        return changed;
    }

    public bool ClockRunning => clock.IsEnabled;

    private void UpdateClock()
    {
        var run = attached && MeasureRunning;
        if (run && !clock.IsEnabled) clock.Start();
        else if (!run && clock.IsEnabled) clock.Stop();
    }

    private void RaiseClock()
    {
        Raise(nameof(RemainingMs)); Raise(nameof(RunningText)); Raise(nameof(RunningProgress));
    }

    // 明细窗口里的范围与目标意图改走窗口自己的选择（combat.detail.*），主页面的选择不变。
    private void Intent(string name, string? argument) => _ = backend.SendIntentAsync(
        detail && name is "combat.scope" or "combat.scope.live" or "combat.target" ? "combat.detail." + name["combat.".Length..] : name,
        argument, CancellationToken.None);

    public CombatPageData Data { get; private set; }

    // ---- 页面上的固定集合：刷新时就地更新（见 StableList） ----

    public StableList<KpiTile> Kpis { get; } = [];
    public StableList<CoverageTile> Coverage { get; } = [];
    public StableList<AttributionPart> AttributionParts { get; } = [];
    /// <summary>自身与怪物效果按极性分三行：正面、负面、未分类（表里标为无或没有极性来源）；每行固定一行高，放不下的收进“+N”浮层。</summary>
    public EffectStrip SelfPositive { get; } = new(EffectPolarity.Positive);
    public EffectStrip SelfNegative { get; } = new(EffectPolarity.Negative);
    public EffectStrip SelfOther { get; } = new(EffectPolarity.Unknown);
    public EffectStrip TargetPositive { get; } = new(EffectPolarity.Positive);
    public EffectStrip TargetNegative { get; } = new(EffectPolarity.Negative);
    public EffectStrip TargetOther { get; } = new(EffectPolarity.Unknown);
    public StableList<HealingTile> HealingTiles { get; } = [];

    private void SyncLists()
    {
        Kpis.Sync(Data.Kpis);
        Coverage.Sync(Data.Coverage);
        Traits.Sync(Data.Traits ?? []);
        Classes.Sync(Data.Classes ?? []);
        Rates.Sync([.. Data.Coverage, .. Data.Traits ?? [], .. Data.Classes ?? []]);
        AttributionParts.Sync(Data.Attribution);
        ElementParts.Sync(Data.Elements?.Parts ?? []);
        HealingTiles.Sync(Data.Healing?.Tiles ?? []);
        SelfPositive.Sync(Data.SelfEffects.Where(row => row.Polarity == EffectPolarity.Positive).ToList());
        SelfNegative.Sync(Data.SelfEffects.Where(row => row.Polarity == EffectPolarity.Negative).ToList());
        SelfOther.Sync(Data.SelfEffects.Where(row => row.Polarity is EffectPolarity.Neutral or EffectPolarity.Unknown).ToList());
        TargetPositive.Sync(Data.TargetEffects.Where(row => row.Polarity == EffectPolarity.Positive).ToList());
        TargetNegative.Sync(Data.TargetEffects.Where(row => row.Polarity == EffectPolarity.Negative).ToList());
        TargetOther.Sync(Data.TargetEffects.Where(row => row.Polarity is EffectPolarity.Neutral or EffectPolarity.Unknown).ToList());
        TargetChipList.Sync(Data.TargetChips);
        MeasurementOptions.Sync(Scope.Measurements);
        RangeTargetItems.Sync(HasRangeTargets ? [.. RangeTargets, RangeTargetMore.Instance] : []);
        TargetChipItems.Sync(Data.TargetChips.Count > 0 ? [.. Data.TargetChips, EffectTargetMore.Instance] : []);
        History.Sync(FilteredHistory());
    }

    /// <summary>回到页面后、下一次刷新到达前为真：页面先显示上次的数据并标注“更新中”。</summary>
    public bool Refreshing { get; private set; }

    public bool Cacheable => true;

    /// <summary>页面被切走：停止接收后端通知与本页专用的读取，保留界面与最后一次数据。</summary>
    public void Deactivate()
    {
        if (!subscribed) return;
        subscribed = false;
        backend.Changed -= OnBackendChanged;
        if (detail) backend.CombatDetailChanged -= OnBackendChanged;
        CombatEffectFavorites.Changed -= OnFavoritesChanged;
        CombatEffectFavorites.CatalogChanged -= OnCatalogChanged;
        if (page) backend.SetCombatPageVisible(false);
        attached = false;
        UpdateClock();
    }

    /// <summary>回到页面：立即显示后端适配层内存中的最近结果；后端在下一轮刷新后通知，届时清除“更新中”。</summary>
    public void Activate()
    {
        if (subscribed) return;
        subscribed = true;
        backend.Changed += OnBackendChanged;
        if (detail) backend.CombatDetailChanged += OnBackendChanged;
        CombatEffectFavorites.Changed += OnFavoritesChanged;
        CombatEffectFavorites.CatalogChanged += OnCatalogChanged;
        Refreshing = page && backend.SetCombatPageVisible(true);
        Apply(Fetch());
    }
    private bool subscribed = true;
    public IReadOnlyList<BarSegment> AttributionSegments { get; private set; }
    public ICommand ShowSkillsCommand { get; }
    public ICommand ShowTargetsCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand EndCommand { get; }
    public ICommand SplitCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand SelectTargetCommand { get; }
    public ICommand FavoriteCommand { get; }
    public ICommand ToggleFavoritesOnlyCommand { get; }

    public bool ByTarget
    {
        get => byTarget;
        private set
        {
            if (!Set(ref byTarget, value)) return;
            Raise(nameof(BySkill));
            Raise(nameof(Rows));
            Raise(nameof(NoRows));
            Raise(nameof(NameColumn));
            Raise(nameof(DamageColumns));
            Raise(nameof(DamageTableId));
        }
    }

    /// <summary>按技能与按目标各是一张表，列设置分别保存。</summary>
    public string DamageTableId => byTarget ? "combat.targets" : "combat.skills";

    private string? columnKey;
    private IReadOnlyList<TableColumn> damageColumns = [];

    /// <summary>
    /// 明细表的列：名称、伤害、占比、次数、最小～最大，然后每个显示的标签一组两列（“次数”为占比与次数，“伤害”为带该标签的伤害与占本行伤害），
    /// 标签可重叠；有攻击特性数据时末尾加元素构成一列。列多时表格横向滚动。
    /// </summary>
    public IReadOnlyList<TableColumn> DamageColumns
    {
        get
        {
            var key = $"{byTarget}|{Data.ShowTraitColumns}|{string.Join(',', Data.TagIds)}|{UiText.Language}";
            if (columnKey == key) return damageColumns;
            columnKey = key;
            static TableColumn Number(string id, string header, string path, double width) =>
                new() { Id = id, HeaderKey = header, Path = path, Kind = ColumnKind.Number, Width = width };
            var list = new List<TableColumn>
            {
                new() { Id = "name", HeaderKey = byTarget ? "C.ColTarget" : "C.ColSkill", Path = nameof(DamageRow.Name), Width = 240, MinWidth = 120,
                    Fill = true, CanHide = false, CellClasses = "strong", ToolTipPath = nameof(DamageRow.Tip) },
                Number("damage", "C.Damage", nameof(DamageRow.Damage), 120),
                Number("share", "C.ColShare", nameof(DamageRow.Share), 88),
                Number("hits", "C.ColHits", nameof(DamageRow.Hits), 88),
                Number("range", "C.ColRange", nameof(DamageRow.Range), 170)
            };
            for (var i = 0; i < CombatMapper.Tags.Count; i++)
            {
                var tag = CombatMapper.Tags[i];
                if (!Data.TagIds.Contains(tag.Id)) continue;
                var (count, damage) = (i * 2, i * 2 + 1);
                list.Add(new() { Id = $"tag.{tag.Id}.count", HeaderKey = "C.TagCount", Group = tag.Name, Width = 120,
                    Value = row => (row as DamageRow)?.TagCell(count), ToolTip = _ => TagTip });
                list.Add(new() { Id = $"tag.{tag.Id}.damage", HeaderKey = "C.TagDamage", Group = tag.Name, Width = 150,
                    Value = row => (row as DamageRow)?.TagCell(damage), ToolTip = _ => TagTip });
            }
            if (Data.ShowTraitColumns)
                list.Add(new() { Id = "elements", HeaderKey = "C.ColElements", Path = nameof(DamageRow.Elements), Width = 220, ToolTipPath = nameof(DamageRow.Elements) });
            return damageColumns = list;
        }
    }

    /// <summary>标签列的说明：标签可重叠，各项占比相加可超过 100%；次数占比的分母按标签的适用口径。</summary>
    public static string TagTip => UiText.Pick(
        "标签可重叠：一条伤害可同时计入多个标签，各项占比相加可超过 100%。次数占比的分母：暴击为可暴击次数，强击、连击、追击为直接伤害命中数，其余为全部伤害次数；伤害占比为带该标签的伤害占本行伤害。",
        "標籤可重疊：一條傷害可同時計入多個標籤，各項佔比相加可超過 100%。次數佔比的分母：暴擊為可暴擊次數，強擊、連擊、追擊為直接傷害命中數，其餘為全部傷害次數；傷害佔比為帶該標籤的傷害佔本列傷害。");

    public bool BySkill => !byTarget;

    /// <summary>已按下“停止观测”、后端状态尚未变化。</summary>
    public bool Stopping => stopping;
    public string StopText => stopping ? UiText.Pick("正在停止", "正在停止") : UiText.Pick("停止观测", "停止觀測");
    public bool FavoritesOnly => favoritesOnly;
    public IReadOnlyList<DamageRow> Rows => byTarget ? Data.Targets : Data.Skills;
    public StableList<EncounterRow> History { get; } = [];
    private IReadOnlyList<EncounterRow> FilteredHistory() =>
        favoritesOnly ? Data.History.Where(row => row.Favorite).ToList() : Data.History;
    public string NameColumn => byTarget ? UiText.Pick("目标", "目標") : "技能";
    public bool NoRows => Rows.Count == 0;
    public bool NoSelfEffects => Data.SelfEffects.Count == 0;
    public bool NoTargets => Data.TargetChips.Count == 0;
    public bool HasTargets => Data.TargetChips.Count > 0;
    public bool NoHistory => History.Count == 0;
    public bool HasAttribution => Data.Attribution.Count > 0;

    /// <summary>攻击特性四块；本场全部未知时整组不显示、不占位。</summary>
    public StableList<CoverageTile> Traits { get; } = [];
    public bool HasTraits => Traits.Count > 0;

    /// <summary>整场元素构成：未知时不显示，已知但没有元素时只显示一行“无元素”。</summary>
    public bool HasElements => Data.Elements is not null;
    public bool HasElementParts => Data.Elements is { None: false };
    public bool NoElements => Data.Elements is { None: true };
    public StableList<ElementPart> ElementParts { get; } = [];
    public IReadOnlyList<BarSegment> ElementSegments { get; private set; } = [];

    /// <summary>本场最近的一次首击；事件未知、查不到或本场没有首击时整组不显示、不占位。</summary>
    public FirstAttackRow? FirstAttack => Data.FirstAttack;
    public bool HasFirstAttack => Data.FirstAttack is not null;
    public bool HasFirstAttackTarget => Data.FirstAttack?.HasTarget == true;

    /// <summary>治疗统计：本场有数据时显示；后端未提供治疗块时只显示“未提供治疗数据”。</summary>
    public bool HasHealing => Data.Healing is not null;
    public bool HealingAvailable => Data.Healing is { Available: true };
    public bool NoHealing => Data.Healing is { Available: false };
    public IReadOnlyList<HealingTargetRow> HealingTargets => Data.HealingTargets ?? [];
    public string HealingHeader => Data.Healing?.Header ?? string.Empty;
    public bool HasHealingHeader => HealingHeader.Length > 0;
    public string HealingNote => Data.Healing?.Note ?? string.Empty;
    public bool HasHealingNote => Data.Healing?.HasNote == true;

    /// <summary>“治疗目标”表的列：目标、治疗量、占比、次数、HPS、有效治疗、溢出（主页面与明细窗口同一张表）。</summary>
    public IReadOnlyList<TableColumn> HealingColumns { get; } =
    [
        new() { Id = "name", HeaderKey = "C.ColTarget", Path = nameof(HealingTargetRow.Name), Width = 240, MinWidth = 120,
            Fill = true, CanHide = false, CellClasses = "strong", ToolTipPath = nameof(HealingTargetRow.Tip) },
        new() { Id = "amount", HeaderKey = "C.ColHealing", Path = nameof(HealingTargetRow.Amount), Kind = ColumnKind.Number, Width = 120 },
        new() { Id = "share", HeaderKey = "C.ColShare", Path = nameof(HealingTargetRow.Share), Kind = ColumnKind.Number, Width = 88 },
        new() { Id = "count", HeaderKey = "C.ColHits", Path = nameof(HealingTargetRow.Count), Kind = ColumnKind.Number, Width = 88 },
        new() { Id = "hps", HeaderKey = "C.ColHps", Path = nameof(HealingTargetRow.Hps), Kind = ColumnKind.Number, Width = 96 },
        new() { Id = "effective", HeaderKey = "C.ColEffective", Path = nameof(HealingTargetRow.Effective), Kind = ColumnKind.Number, Width = 110,
            ToolTipPath = nameof(HealingTargetRow.EffectiveHint) },
        new() { Id = "overheal", HeaderKey = "C.ColOverheal", Path = nameof(HealingTargetRow.Overheal), Kind = ColumnKind.Number, Width = 96,
            ToolTipPath = nameof(HealingTargetRow.EffectiveHint) }
    ];

    /// <summary>明细表是否加入攻击特性与元素五列；表格横向滚动可到达。</summary>
    public bool ShowTraitColumns => Data.ShowTraitColumns;

    public void Dispose()
    {
        if (subscribed && page) backend.SetCombatPageVisible(false);
        subscribed = false;
        backend.Changed -= OnBackendChanged;
        if (detail) backend.CombatDetailChanged -= OnBackendChanged;
        CombatEffectFavorites.Changed -= OnFavoritesChanged;
        CombatEffectFavorites.CatalogChanged -= OnCatalogChanged;
        attached = false;
        clock.Stop();
    }

    private void OnBackendChanged(object? sender, EventArgs e)
    {
        Refreshing = false;
        Apply(Fetch());
    }

    // 收藏或“只显示收藏”变化：重读同一份战斗数据（筛选在后端适配层统一套用）。
    private void OnFavoritesChanged(object? sender, EventArgs e)
    {
        Apply(Fetch());
        SyncFavorites();
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => SyncFavorites();

    // 整份数据换新：列表就地同步，标量绑定重取一次（值未变的绑定不会重排布局）；占比条只在比例变化时换新。
    // 刚切换范围或目标、新结果还没到时只移动选中标记，数值与列表保持上一次的显示，不清空重建。
    private void Apply(CombatPageData data)
    {
        Data = data.Pending ? Hold(Data, data) : data;
        if (stopping && !Data.Actions.CanStop) SetStopping(false);
        var attribution = Data.Attribution.Select(part => part.Segment).ToList();
        if (!attribution.Select(part => part.Weight).SequenceEqual(AttributionSegments.Select(part => part.Weight))) AttributionSegments = attribution;
        var elements = (Data.Elements?.Parts ?? []).Select(part => part.Segment).ToList();
        if (!elements.Select(part => part.Weight).SequenceEqual(ElementSegments.Select(part => part.Weight))) ElementSegments = elements;
        SyncLists();
        RefreshDetail();
        Raise(string.Empty);
        UpdateClock();
    }

    /// <summary>
    /// 新选择的结果到达前的显示：范围、目标与依赖范围的数值保持上一次，选中标记换成新选择；
    /// 与范围无关的部分（状态、测量、自身效果、战斗记录、按钮）照常取新数据。
    /// </summary>
    internal static CombatPageData Hold(CombatPageData shown, CombatPageData next)
    {
        var target = next.SelectedTarget;
        var scope = shown.Scope is { } held ? held with { Kind = next.Scope?.Kind ?? held.Kind, Measurements = next.Scope?.Measurements ?? held.Measurements,
            Title = next.Scope?.Title ?? held.Title, TitleTip = next.Scope?.TitleTip ?? held.TitleTip, Problem = string.Empty } : next.Scope;
        return shown with
        {
            Status = next.Status, Actions = next.Actions, Measurement = next.Measurement, Location = next.Location, History = next.History,
            Starting = next.Starting, Auto = next.Auto, SelfEffects = next.SelfEffects, SelfFavoritesEmpty = next.SelfFavoritesEmpty,
            Scope = scope,
            RangeTargets = shown.RangeTargets?.Select(chip => chip with { Current = chip.IsAll ? target is null : chip.Id == target }).ToList(),
            TargetChips = target is null ? shown.TargetChips : shown.TargetChips.Select(chip => chip with { Current = chip.Id == target }).ToList(),
            SelectedScope = next.SelectedScope, SelectedTarget = target, Pending = true
        };
    }

    private void SetStopping(bool value)
    {
        stopping = value;
        Raise(nameof(Stopping));
        Raise(nameof(StopText));
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private RelayCommand Intent(CombatIntent intent) => new(_ => Send(intent, null));

    private void Send(CombatIntent intent, string? targetId) =>
        _ = backend.SendCombatIntentAsync(intent, targetId, CancellationToken.None);
}
