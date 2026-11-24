using System.Diagnostics;
using System.Globalization;
using MabiTool.Desktop.Pages.Combat;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 样本后端的 DPS检测页新增部分：范围、目标、定时测量与破防。只模拟后端的回执形状，供界面开发与测试使用。
public sealed partial class SampleBackend
{
    private CombatScopeKind combatScope = CombatScopeKind.Whole;
    private string? rangeTarget;
    private int measurementDurationMs = 60000;

    internal ulong? SampleMeasurementSettingsRevision { get; set; } = 0;

    /// <summary>测试用：预先选中的范围与目标。</summary>
    internal CombatScopeKind SampleScope { set => combatScope = value; }
    internal string? SampleRangeTarget { set => rangeTarget = value; }

    /// <summary>测试用：定时测量所处的阶段（空闲、等待首次伤害、计时）。</summary>
    internal MeasurementPhase SampleMeasurementPhase { get; set; }

    /// <summary>测试用：定时测量不能开始（没有进行中的 DPS 观测）。</summary>
    internal bool MeasurementUnavailable { get; set; }

    /// <summary>测试用：常驻自动观测的状态说明（例如等待识别角色）；为空表示观测进行中。</summary>
    internal string SampleAutoText { get; set; } = string.Empty;
    private bool autoEnabled = true;

    /// <summary>测试用：主数据里的 DPS 数值，用来检查刷新后页面就地更新。</summary>
    internal string SampleDps { get; set; } = "18,420";

    /// <summary>测试用：当前范围的结果不完整。</summary>
    internal bool IncompleteRange { get; set; }

    /// <summary>测试用：范围内另有这么多个目标（“小怪 1”…，伤害依次递减），用来检查目标标签折叠成一行。</summary>
    internal int ExtraRangeTargets { get; set; }

    /// <summary>
    /// 后端选定的当前攻击目标（契约形状，经与真实后端相同的映射显示）；样本默认是一只示例首领，数值一看即知是示例。
    /// 测试改成各种状态或设为空（没有当前目标）。
    /// </summary>
    internal MabiTool.Contracts.CombatCurrentTarget? SampleCurrentTarget { get; set; } = SampleTarget("sample-boss", 1_234_567, 12_345_678);

    /// <summary>示例目标：首领标志为真、血量已读，名称为“示例首领”。</summary>
    internal static MabiTool.Contracts.CombatCurrentTarget SampleTarget(string id, ulong current, ulong? maximum, string? name = null) => new(id,
        new("sample", new(1), new(4_242), new(1)), id, "main-target", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        new(new(current), maximum is { } max ? new(max) : null, DateTimeOffset.UnixEpoch, "read"), IsBoss: true,
        ResolvedName: new("monster", new(1), name ?? Pick("示例首领", "示例首領"), CombatNames.Resolved), ActorType: "monster");

    // 明细窗口自己的范围与目标；窗口打开时取主页面当时的选择。
    private CombatScopeKind detailScope = CombatScopeKind.Whole;
    private string? detailTarget;

    public event EventHandler? CombatDetailChanged;

    public void SetCombatDetailOpen(bool open)
    {
        if (open) (detailScope, detailTarget) = (combatScope, rangeTarget);
    }

    /// <summary>明细窗口的数据：按窗口自己的选择生成，主页面的选择不变。</summary>
    public CombatPageData GetCombatDetail()
    {
        var shown = (combatScope, rangeTarget);
        (combatScope, rangeTarget) = (detailScope, detailTarget);
        try { return GetCombat(); }
        finally { (combatScope, rangeTarget) = shown; }
    }

    private void ApplyDetailIntent(string intent, string? argument)
    {
        switch (intent)
        {
            case "combat.detail.scope":
                var next = CombatMapper.ScopeKind(argument ?? "observation");
                if (next != detailScope) detailTarget = null;
                detailScope = next;
                break;
            case "combat.detail.scope.live": detailScope = CombatScopeKind.Whole; detailTarget = null; break;
            case "combat.detail.target": detailTarget = string.IsNullOrEmpty(argument) ? null : argument; break;
        }
    }

    /// <summary>样本的记录明细：沿用明细窗口的样本数据，标题为样本记录。</summary>
    public CombatPageData GetCombatRecord(string id) => GetCombatDetail() with
    {
        RecordInfo = new(Pick("样本记录", "樣本紀錄"), Pick("保存于 11/14 21:30", "儲存於 11/14 21:30"), false, false, string.Empty)
    };

    public CombatTimelineView? GetCombatRecordTimeline(string id) => GetCombatTimeline();

    /// <summary>样本的 DPS 曲线（明细窗口的范围）：整场按本机时钟、其余按相对时间，各 60 个点。</summary>
    public CombatTimelineView? GetCombatTimeline() =>
        new(Enumerable.Range(0, 60).Select(i => 18000 + 6000 * Math.Sin(i / 6.0) + (i % 7) * 900.0).ToList(), 2.5, 150, null,
            Origin: detailScope == CombatScopeKind.Whole ? new DateTimeOffset(2026, 11, 14, 13, 30, 0, TimeSpan.Zero) : null);

    private void ApplyCombatIntent(string intent, string? argument)
    {
        switch (intent)
        {
            case "combat.scope":
                var next = CombatMapper.ScopeKind(argument ?? "observation");
                if (next != combatScope) rangeTarget = null;
                combatScope = next;
                break;
            case "combat.scope.live": combatScope = CombatScopeKind.Whole; rangeTarget = null; break;
            case "combat.auto": autoEnabled = argument == "on"; break;
            case "combat.target": rangeTarget = string.IsNullOrEmpty(argument) ? null : argument; break;
            case "combat.measure.toggle":
                SampleMeasurementPhase = SampleMeasurementPhase == MeasurementPhase.Idle ? MeasurementPhase.Waiting : MeasurementPhase.Idle;
                break;
            case "combat.measure.start": SampleMeasurementPhase = MeasurementPhase.Waiting; break;
            case "combat.measure.cancel": SampleMeasurementPhase = MeasurementPhase.Idle; break;
            case "combat.measure.settings" when argument?.Split(',') is [var duration, _, var revision] &&
                ulong.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var readRevision) &&
                readRevision == SampleMeasurementSettingsRevision:
                measurementDurationMs = int.Parse(duration, CultureInfo.InvariantCulture);
                SampleMeasurementSettingsRevision = readRevision + 1;
                break;
        }
    }

    private CombatPageData WithRange(CombatPageData data)
    {
        var unknown = Pick("未知", "未知");
        var times = Pick("次", "次");
        var phase = SampleMeasurementPhase;
        var measurement = new MeasurementView(phase, measurementDurationMs,
            phase == MeasurementPhase.Running ? 41500 : null, Stopwatch.GetTimestamp(),
            phase == MeasurementPhase.Running ? "1,032,700" : "—", phase == MeasurementPhase.Running ? "55,822" : "—",
            !MeasurementUnavailable && phase == MeasurementPhase.Idle, MeasurementUnavailable ? Pick("需要先开始 DPS 观测", "需要先開始 DPS 觀測") : string.Empty,
            phase != MeasurementPhase.Idle, string.Empty,
            new("m1", "18,904", "1,134,240", "1:00", "0:57", string.Empty), SampleMeasurementSettingsRevision);
        var measurementTitle = Pick("定时测量 1:00 · 11/14 21:30", "定時測量 1:00 · 11/14 21:30");
        var scope = new ScopeView(combatScope, combatScope == CombatScopeKind.Measurement ? measurementTitle : string.Empty,
            [new("m1", measurementTitle, combatScope == CombatScopeKind.Measurement), new("m2", Pick("木桩 3 分钟", "木樁 3 分鐘"), false)],
            IncompleteRange ? Pick("不完整：期间切换了地图", "不完整：期間切換了地圖") : string.Empty, string.Empty);
        var targets = new List<RangeTargetChip>
        {
            new(string.Empty, Pick("全部", "全部"), "2,781,420", "100%", rangeTarget is null),
            new("boss", "巨魔首領", "1,982,640", "71%", rangeTarget == "boss"),
            new("archer", "巨魔弓手", "561,300", "20%", rangeTarget == "archer"),
            new("archer-2", "巨魔弓手", "237,480", "9%", rangeTarget == "archer-2")
        };
        for (var index = 1; index <= ExtraRangeTargets; index++)
            targets.Add(new($"mob-{index}", Pick($"洞穴小怪 {index}", $"洞穴小怪 {index}"), (90_000 - index * 1_000).ToString("N0", CultureInfo.InvariantCulture), "1%",
                rangeTarget == $"mob-{index}"));
        var detail = targets.FirstOrDefault(chip => !chip.IsAll && chip.Current) is { } chosen
            ? new TargetDetailView(chosen.Name, chosen.Damage, chosen.Share, double.Parse(chosen.Share.TrimEnd('%'), CultureInfo.InvariantCulture) / 100)
            : null;
        return data with
        {
            SelectedScope = combatScope switch { CombatScopeKind.Segment => "segment", CombatScopeKind.Measurement => "measurement:m1", _ => "observation" },
            SelectedTarget = rangeTarget,
            Measurement = measurement,
            Auto = new(autoEnabled, autoEnabled ? SampleAutoText : Pick("自动观测已关闭", "自動觀測已關閉"), false),
            Actions = autoEnabled ? data.Actions with { CanStart = false, CanStop = false } : data.Actions,
            Location = new(Pick("战斗中", "戰鬥中"), ActivityTone.Fighting),
            Scope = scope,
            RangeTargets = targets,
            TargetDetail = detail,
            Health = CombatMapper.Health(SampleCurrentTarget, GetCapabilityNotice(CapabilityIds.MonsterHealth)),
            HealthIdle = SampleCurrentTarget is null && GetCapabilityNotice(CapabilityIds.MonsterHealth) is null,
            AimId = SampleCurrentTarget?.EntityId,
            AimEffects = SampleCurrentTarget is null ? [] : data.TargetEffects,
            Observing = true,
            Break = new("612,880", "87", "7,044", "24,190", "0:25", "3", "17%",
                Pick("破防区间：本人造成破防的一击起，到目标破防值归零止，按目标分别计；平均 DPS = 无防备伤害 ÷ 区间总时长，占比按范围时长",
                    "破防區間：本人造成破防的一擊起，到目標破防值歸零止，按目標分別計；平均 DPS = 無防備傷害 ÷ 區間總時長，佔比按範圍時長")),
            Classes =
            [
                new(Pick("间接伤害", "間接傷害"), "10%", 0.10, $"42 / 412 {times}"),
                new(Pick("持续伤害", "持續傷害"), "18%", 0.18, $"74 / 412 {times}")
            ],
            CanSave = true,
            DefaultRecordName = Pick("整场 11/14 21:30", "整場 11/14 21:30"),
            Kpis =
            [
                new("DPS", SampleDps, true, Pick("最近 60 秒 22,310", "最近 60 秒 22,310")),
                new(Pick("总伤害", "總傷害"), "2,781,420", false),
                new(Pick("战斗时间", "戰鬥時間"), "2:31", false, Pick("实际输出 2:24", "實際輸出 2:24")),
                new(Pick("命中次数", "命中次數"), "412", false)
            ]
        };
    }
}
