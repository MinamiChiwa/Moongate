using System.Globalization;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Character;

/// <summary>角色页数据的读取情况：桌面没连上后端、已连上但还没读到、后端没有数据接口（旧宿主）、已读到。</summary>
public enum CharacterRead { Offline, NotRead, Unsupported, Loaded }

/// <summary>
/// 一项“名称 / 数值”；<see cref="Unread"/> 为真时数值显示为次要色（未读取）。<see cref="Icon"/> 是随包游戏图标（“分组/键”，对应不上时为空），
/// <see cref="Fraction"/> 是可画成量表的比例（0–1，没有时为空）。
/// </summary>
public sealed record StatLine(string Label, string Value, bool Unread = false, string? Icon = null, double? Fraction = null)
{
    public bool HasFraction => Fraction is not null;
    public double Progress => Fraction ?? 0;
}

/// <summary>一项生活技能：等级、经验（满级没有经验，<see cref="Exp"/> 为空）与经验进度（0–1，没有时为空）；图标按游戏原文名称对应。</summary>
public sealed record SkillLine(string Name, string Level, string Exp, double? Fraction, bool Unread, string? Icon = null)
{
    public bool HasExp => Exp.Length > 0;
    public bool HasFraction => Fraction is not null;
    public double Progress => Fraction ?? 0;
}

/// <summary>概况卡：昵称只取抓包识别的本机身份，服务器（<c>RealmName</c>）与称号各占一行。</summary>
public sealed record CharacterOverview(string Nickname, string Server, string Title, string Level, string Job, bool NicknameUnread,
    bool ServerUnread, bool TitleUnread, bool LevelUnread, bool JobUnread);

/// <summary>一条任务；<see cref="Ready"/> 为真表示已完成未领奖。</summary>
public sealed record MissionLine(string Title, string Description, string Progress, bool Ready)
{
    public bool HasDescription => Description.Length > 0;
}

/// <summary>
/// 日常或周常的任务页签：未完成在前（<see cref="Todo"/>），已完成未领奖单列（<see cref="Ready"/>），已领奖不占待办位置、只给个数。
/// <see cref="Unread"/> 为真表示没读到（显示未读取，不显示“没有任务”）。
/// </summary>
public sealed record MissionTabView(BlockNote Note, bool Unread, IReadOnlyList<MissionLine> Todo, IReadOnlyList<MissionLine> Ready,
    int ReceivedCount, string Summary)
{
    public bool HasTodo => Todo.Count > 0;
    public bool HasReady => Ready.Count > 0;
    public bool HasReceived => ReceivedCount > 0;
    public string ReceivedText => Pick($"已领奖 {ReceivedCount} 项", $"已領獎 {ReceivedCount} 項");
    public static MissionTabView NotRead => new(BlockNote.Unread(), true, [], [], 0, Pick("未读取", "未讀取"));
}

/// <summary>成长指南的一项；未解锁的内容带解锁条件。</summary>
public sealed record GrowthLine(string Target, string Kind, string State, bool Dim);

/// <summary>成长指南卡：游戏内的内容指南未解锁时写明原因与游戏给出的解锁条件。</summary>
public sealed record GrowthView(BlockNote Note, bool Unread, bool Locked, string LockedText, IReadOnlyList<GrowthLine> Items)
{
    public bool HasItems => Items.Count > 0;
    public bool HasLockedText => LockedText.Length > 0;
    public static GrowthView NotRead => new(BlockNote.Unread(), true, false, string.Empty, []);
}

/// <summary>
/// 环境（频道、区域、天气、游戏内时间原文）：角色页周边卡与“环境信息”浮窗元件共用；未读到的项显示“未读取”。
/// <see cref="WeatherIcon"/> 是天气对应的随包图标（未收录的天气为空）。
/// </summary>
/// <param name="Time">游戏内时间：<c>ErinnNow</c> 里的时刻（时:分）；读不出时刻时为原文。</param>
/// <param name="TimeRaw">游戏内时间的原文（悬停提示）。</param>
public sealed record EnvironmentView(BlockNote Note, bool Unread, string Channel, string Area, string Weather, string Time, string Position,
    string Housing, string? WeatherIcon = null, string? TimeRaw = null)
{
    public string TimeTip => string.IsNullOrWhiteSpace(TimeRaw) || TimeRaw == Time ? Time : Localization.UiText.Pick("游戏原文：", "遊戲原文：") + TimeRaw;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Localization.UiLanguage, EnvironmentView> Unreads = new();

    /// <summary>没读到时的环境（按界面语言各缓存一份，同一语言下是同一个对象）。</summary>
    public static EnvironmentView NotRead => Unreads.GetOrAdd(Localization.UiText.Language, _ => new(BlockNote.Unread(), true, CharacterText.Unread,
        CharacterText.Unread, CharacterText.Unread, CharacterText.Unread, CharacterText.Unread, CharacterText.Unread));
    public string Tip => Note.Text + (Note.HasDetail ? "\n" + Note.Detail : string.Empty);
}

/// <summary>
/// 附近的一位玩家：身份（称号，没有称号时写“附近玩家”）、等级职业与距离、演奏状态；演奏中才有 <see cref="Performance"/>；
/// <see cref="Tip"/> 是悬停提示（服务器名）。
/// </summary>
public sealed record NearbyLine(string Name, string Detail, string Performance, string Tip)
{
    public bool IsPerforming => Performance.Length > 0;
}

/// <summary>附近正在演奏的一首曲子的演奏者：<see cref="Identity"/> 是称号加等级职业（没有称号时为“附近玩家”），用于“同一曲目只提示一次”。</summary>
public sealed record NearbyPerformer(string Identity, string Title, int Channels);

/// <summary><see cref="Unread"/> 为真表示没读到（不是“附近没有玩家”）；<see cref="Performers"/> 是演奏中的玩家。</summary>
public sealed record NearbyView(BlockNote Note, bool Unread, IReadOnlyList<NearbyLine> Players, IReadOnlyList<NearbyPerformer> Performers)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Localization.UiLanguage, NearbyView> Unreads = new();

    /// <summary>没读到时的附近玩家（按界面语言各缓存一份，同一语言下是同一个对象）。</summary>
    public static NearbyView NotRead => Unreads.GetOrAdd(Localization.UiText.Language, _ => new(BlockNote.Unread(), true, [], []));
    public bool HasPlayers => Players.Count > 0;
    public string Empty => Unread ? Pick("未读取", "未讀取") : Pick("附近没有玩家", "附近沒有玩家");
    public string Tip => Note.Text + (Note.HasDetail ? "\n" + Note.Detail : string.Empty);
}

/// <summary>
/// 角色信息页的全部数据（人物与成长；货币归背包页的资源卡）。命令行独有的块（属性、评分、状态、生活技能、任务、成长指南）没读到时为未读取；
/// 昵称来自抓包，来源写在各块的 <see cref="BlockNote"/> 里。<see cref="Scores"/> 是概况里的四项评分（战斗力、生活力、魅力、装饰分数，
/// 始终四项，没读到的为未读取），<see cref="Stats"/> 是其余属性。
/// </summary>
public sealed record CharacterPageData(CharacterRead Read, string Status, bool Sample, BlockNote ProfileNote, CharacterOverview Overview,
    IReadOnlyList<StatLine> Scores, IReadOnlyList<StatLine> Stats, IReadOnlyList<StatLine> PaladinStats, IReadOnlyList<StatLine> Vitals,
    IReadOnlyList<SkillLine> Skills, MissionTabView Daily, MissionTabView Weekly, GrowthView Growth, EnvironmentView Environment, NearbyView Nearby)
{
    public bool Loaded => Read == CharacterRead.Loaded;

    // 空页数据带界面文字：按界面语言各缓存一份，同一语言下返回同一个对象（页面模型据此判断没有变化）。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(CharacterRead, Localization.UiLanguage), CharacterPageData> Empties = new();
    private static CharacterPageData Placeholder(CharacterRead read) =>
        Empties.GetOrAdd((read, Localization.UiText.Language), key => CharacterMapper.Empty(key.Item1));

    public static CharacterPageData Offline => Placeholder(CharacterRead.Offline);
    public static CharacterPageData NotReadYet => Placeholder(CharacterRead.NotRead);
    public static CharacterPageData Unsupported => Placeholder(CharacterRead.Unsupported);
}

/// <summary>契约数据 → 角色页数据的纯映射；样本后端与真实后端共用，缺省字段一律显示“未读取”。</summary>
public static class CharacterMapper
{
    private static string Unread => CharacterText.Unread;

    public static string StatusLine(CharacterRead read) => read switch
    {
        CharacterRead.Offline => Pick("未连接后端", "未連接後端"),
        CharacterRead.Unsupported => Pick("后端没有提供角色数据", "後端沒有提供角色資料"),
        CharacterRead.NotRead => Pick("正在读取", "正在讀取"),
        _ => string.Empty
    };

    internal static CharacterPageData Empty(CharacterRead read) => Map(read, null);

    public static CharacterPageData Map(CharacterRead read, GameDataSnapshot? data, bool sample = false)
    {
        var profile = data?.Profile?.Profile;
        var profileNote = CharacterText.Note(data?.Profile?.Status, sample);
        var overview = new CharacterOverview(
            Text(profile?.Nickname, out var nickname) ? nickname : Unread, Text(profile?.ServerName, out var server) ? server : Unread,
            Text(profile?.Title, out var title) ? title : Unread,
            profile?.Level is { } level ? Pick($"Lv.{level}", $"Lv.{level}") : Unread,
            Text(profile?.CombatJob, out var job) ? job : Unread,
            string.IsNullOrWhiteSpace(profile?.Nickname), string.IsNullOrWhiteSpace(profile?.ServerName), string.IsNullOrWhiteSpace(profile?.Title),
            profile?.Level is null, string.IsNullOrWhiteSpace(profile?.CombatJob));
        return new(read, StatusLine(read), sample, profileNote, overview, Scores(profile?.Stats),
            Stats(profile?.Stats?.Where(stat => !IsScore(stat.Key)).ToList()), Stats(profile?.PaladinStats), Vitals(profile?.Vitals),
            Skills(profile?.LivingSkills),
            MissionTab(data?.Missions?.Daily, sample), MissionTab(data?.Missions?.Weekly, sample),
            Growth(data?.GrowthGuide, sample), Environment(data?.Environment, sample), Nearby(data?.Nearby, sample));
    }

    private static bool Text(string? value, out string text)
    {
        text = value ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>概况的四项评分（<c>get_my_info</c> 的字段名 → 图标与缺省名称）。</summary>
    private static readonly (string Key, string Icon, string Simplified, string Traditional)[] ScoreKeys =
    [
        ("CombatScore", "character/combatScore", "战斗力", "戰鬥力"),
        ("LivingScore", "character/livingScore", "生活力", "生活力"),
        ("AttractivenessScore", "character/attractivenessScore", "魅力", "魅力"),
        ("DecorScore", "character/decorScore", "装饰分数", "裝飾分數")
    ];

    private static bool IsScore(string key) => ScoreKeys.Any(score => string.Equals(score.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>四项评分始终列出（顺序固定），名称优先取游戏原文，没读到的为“未读取”。</summary>
    private static IReadOnlyList<StatLine> Scores(IReadOnlyList<GameStat>? stats) => ScoreKeys.Select(score =>
    {
        var stat = stats?.FirstOrDefault(item => string.Equals(item.Key, score.Key, StringComparison.OrdinalIgnoreCase));
        var label = string.IsNullOrWhiteSpace(stat?.DisplayName) ? Pick(score.Simplified, score.Traditional) : stat!.DisplayName;
        return new StatLine(label, CharacterText.Number(stat?.Value), stat?.Value is null, score.Icon);
    }).ToList();

    private static IReadOnlyList<StatLine> Stats(IReadOnlyList<GameStat>? stats) =>
        stats?.Select(stat => new StatLine(string.IsNullOrWhiteSpace(stat.DisplayName) ? stat.Key : stat.DisplayName,
            CharacterText.Number(stat.Value), stat.Value is null, Theme.GameIcons.Find("stat/" + stat.Key)?.Id)).ToList() ?? [];

    private static double? Ratio(double? current, double? maximum) =>
        current is { } value && maximum is { } max and > 0 ? Math.Clamp(value / max, 0, 1) : null;

    /// <summary>状态五行固定列出（体力、护盾、饱腹、背包重量带量表）；没读到的项显示“未读取”。</summary>
    private static IReadOnlyList<StatLine> Vitals(GameVitals? vitals) =>
    [
        new(Pick("体力", "體力"), CharacterText.Pair(vitals?.HealthCurrent, vitals?.HealthMax), vitals?.HealthCurrent is null && vitals?.HealthMax is null,
            "status/health", Ratio(vitals?.HealthCurrent, vitals?.HealthMax)),
        new(Pick("护盾", "護盾"), CharacterText.Pair(vitals?.ShieldAmount, vitals?.ShieldMax), vitals?.ShieldAmount is null && vitals?.ShieldMax is null,
            "status/shield", Ratio(vitals?.ShieldAmount, vitals?.ShieldMax)),
        new(Pick("饱腹", "飽腹"), CharacterText.Pair(vitals?.SatietyValue, vitals?.SatietyMax), vitals?.SatietyValue is null && vitals?.SatietyMax is null,
            "status/satiety", Ratio(vitals?.SatietyValue, vitals?.SatietyMax)),
        new(Pick("背包重量", "背包重量"), CharacterText.Pair(vitals?.InventoryWeightCurrent, vitals?.InventoryWeightMax, 2),
            vitals?.InventoryWeightCurrent is null && vitals?.InventoryWeightMax is null, "status/weight", Ratio(vitals?.InventoryWeightCurrent, vitals?.InventoryWeightMax)),
        new(Pick("生效 Buff", "生效 Buff"), vitals?.ActiveBuffCount is { } count ? count.ToString(CultureInfo.InvariantCulture) : Unread, vitals?.ActiveBuffCount is null,
            "status/buffs")
    ];

    /// <summary>生活技能：等级与经验；满级没有经验字段，只显示等级。图标按游戏原文名称对应，对应不上时不显示。</summary>
    private static IReadOnlyList<SkillLine> Skills(IReadOnlyList<GameLivingSkill>? skills) =>
        skills?.Select(skill =>
        {
            var icon = Theme.GameIcons.LifeSkill(skill.DisplayName);
            if (skill.Level is not { } level) return new SkillLine(skill.DisplayName, Unread, string.Empty, null, true, icon);
            var exp = skill.Exp is { } current && skill.MaxExp is { } maximum
                ? $"{current.ToString("N0", CultureInfo.InvariantCulture)} / {maximum.ToString("N0", CultureInfo.InvariantCulture)}" : string.Empty;
            double? fraction = skill.Exp is { } done && skill.MaxExp is { } max and > 0 ? Math.Clamp((double)done / max, 0, 1) : null;
            return new SkillLine(skill.DisplayName, $"Lv.{level}", exp, fraction, false, icon);
        }).ToList() ?? [];

    /// <summary>
    /// 一个任务页签：未完成在前（保持游戏返回的顺序），已完成未领奖单列，已领奖只计个数。没读到（<c>Items</c> 为空）时为未读取。
    /// </summary>
    public static MissionTabView MissionTab(GameMissionList? list, bool sample = false)
    {
        var note = CharacterText.Note(list?.Status, sample);
        if (list?.Items is not { } items) return new(note, true, [], [], 0, Unread);
        MissionLine Line(GameMission mission, bool ready) => new(mission.Title, mission.Description ?? string.Empty, Progress(mission), ready);
        var todo = items.Where(item => !item.IsCompleted).Select(item => Line(item, false)).ToList();
        var ready = items.Where(item => item.IsCompleted && !item.IsRewardReceived).Select(item => Line(item, true)).ToList();
        var received = items.Count(item => item.IsCompleted && item.IsRewardReceived);
        var summary = items.Count == 0 ? Pick("当前没有任务", "目前沒有任務")
            : Pick($"待办 {todo.Count} · 可领奖 {ready.Count} · 已领奖 {received}", $"待辦 {todo.Count} · 可領獎 {ready.Count} · 已領獎 {received}");
        return new(note, false, todo, ready, received, summary);
    }

    private static string Progress(GameMission mission) => mission.CurrentCount is { } current && mission.GoalCount is { } goal
        ? $"{current.ToString("N0", CultureInfo.InvariantCulture)} / {goal.ToString("N0", CultureInfo.InvariantCulture)}"
        : mission.GoalCount is { } only ? Pick($"目标 {only}", $"目標 {only}") : Unread;

    private static GrowthView Growth(GameGrowthGuideData? data, bool sample)
    {
        if (data is null) return GrowthView.NotRead;
        var note = CharacterText.Note(data.Status, sample);
        if (data.Locked)
            return new(note with { Dim = true }, false, true,
                Pick("内容指南尚未解锁", "內容指南尚未解鎖") + (string.IsNullOrWhiteSpace(data.UnlockHint) ? string.Empty : "：" + data.UnlockHint), []);
        if (data.Items is not { } items) return new(note, true, false, string.Empty, []);
        return new(note, false, false, string.Empty, items.Select(item => new GrowthLine(item.Target,
            string.Equals(item.DayType, "Weekly", StringComparison.OrdinalIgnoreCase) ? Pick("周常", "週常")
                : string.Equals(item.DayType, "Daily", StringComparison.OrdinalIgnoreCase) ? Pick("日常", "日常") : item.DayType,
            GrowthState(item), !item.IsUnlocked)).ToList());
    }

    private static string GrowthState(GameGrowthContent item) =>
        !item.IsUnlocked ? Pick("未解锁", "未解鎖") + (string.IsNullOrWhiteSpace(item.UnlockHint) ? string.Empty : "：" + item.UnlockHint)
        : item.IsCompleted == true ? Pick("已完成", "已完成")
        : item.RemainingCount is { } remaining ? Pick($"剩余 {remaining} 次", $"剩餘 {remaining} 次")
        : item.IsCompleted == false ? Pick("未完成", "未完成") : Unread;

    public static EnvironmentView Environment(GameEnvironmentData? data, bool sample = false)
    {
        if (data is null) return EnvironmentView.NotRead;
        var note = CharacterText.Note(data.Status, sample);
        var value = data.Value;
        string Or(string? text) => string.IsNullOrWhiteSpace(text) ? Unread : text;
        return new(note, value is null, Or(value?.Channel), Or(value?.Area), CharacterText.Weather(value?.Weather), CharacterText.ErinnClock(value?.ErinnTime),
            CharacterText.Position(value?.X, value?.Y), CharacterText.Housing(value?.Housing), Theme.GameIcons.Weather(value?.Weather),
            string.IsNullOrWhiteSpace(value?.ErinnTime) ? null : value!.ErinnTime!.Trim());
    }

    public static NearbyView Nearby(GameNearbyData? data, bool sample = false)
    {
        if (data is null) return NearbyView.NotRead;
        var note = CharacterText.Note(data.Status, sample);
        if (data.Players is not { } players) return new(note, true, [], []);
        var ordered = players.OrderByDescending(player => player.Performance?.IsPlaying == true).ThenBy(player => player.Distance ?? double.MaxValue).ToList();
        var performers = ordered.Where(player => player.Performance is { IsPlaying: true, MusicTitle.Length: > 0 })
            .Select(player => new NearbyPerformer(Identity(player, withDetail: true), player.Performance!.MusicTitle!, player.Performance.ChannelCount ?? 1)).ToList();
        return new(note, false, ordered.Select(Line).ToList(), performers);
    }

    /// <summary>附近玩家没有昵称字段：以称号作身份，称号为空时写“附近玩家”，不用服务器名顶替昵称。</summary>
    private static string Identity(GameNearbyPlayer player, bool withDetail)
    {
        var name = string.IsNullOrWhiteSpace(player.Title) ? Pick("附近玩家", "附近玩家") : player.Title!.Trim();
        if (!withDetail) return name;
        var parts = LevelJob(player);
        return parts.Count == 0 ? name : $"{name}（{string.Join(" ", parts)}）";
    }

    private static List<string> LevelJob(GameNearbyPlayer player)
    {
        var parts = new List<string>();
        if (player.Level is { } level) parts.Add($"Lv.{level}");
        if (!string.IsNullOrWhiteSpace(player.CombatJob)) parts.Add(player.CombatJob!);
        return parts;
    }

    private static NearbyLine Line(GameNearbyPlayer player)
    {
        var parts = LevelJob(player);
        if (player.Distance is { } distance) parts.Add(Pick($"距离 {distance:0} m", $"距離 {distance:0} m"));
        var performance = player.Performance is { IsPlaying: true, MusicTitle.Length: > 0 } active ? PerformanceText(active) : string.Empty;
        return new(Identity(player, withDetail: false), parts.Count == 0 ? Unread : string.Join(" · ", parts), performance,
            string.IsNullOrWhiteSpace(player.ServerName) ? string.Empty : Pick("服务器：", "伺服器：") + player.ServerName);
    }

    /// <summary>“演奏《曲名》”，多声部带声部数，已知剩余时间时再带剩余。</summary>
    public static string PerformanceText(GameNearbyPerformance performance)
    {
        var text = Pick($"演奏《{performance.MusicTitle}》", $"演奏《{performance.MusicTitle}》");
        if (performance.ChannelCount is > 1 and var channels) text += Pick($" · {channels} 声部", $" · {channels} 聲部");
        if (performance.RemainingSeconds is { } remaining and > 0) text += Pick($" · 剩余 {CharacterText.Duration(remaining)}", $" · 剩餘 {CharacterText.Duration(remaining)}");
        return text;
    }
}
