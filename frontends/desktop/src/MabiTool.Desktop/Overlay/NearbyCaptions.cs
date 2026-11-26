using MabiTool.Desktop.Pages.Character;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Overlay;

/// <summary>一次新出现的附近演奏提示：曲目与字幕文字。</summary>
public sealed record NearbyAnnouncement(string Title, string Text);

/// <summary>
/// 附近演奏字幕的提示规则：同一曲目只提示一次（键为曲目标题，多人演奏同一首只提示一次），
/// 曲目在最新一次读取里没有任何人演奏即重置，再次开始时可以再次提示。
/// </summary>
public sealed class NearbyAnnouncer
{
    private readonly HashSet<string> announced = new(StringComparer.Ordinal);

    /// <summary>用最新一次读取到的演奏者更新状态，返回这次新出现的曲目的提示。</summary>
    public IReadOnlyList<NearbyAnnouncement> Update(IReadOnlyList<NearbyPerformer> performers)
    {
        var active = performers.Select(performer => performer.Title).ToHashSet(StringComparer.Ordinal);
        announced.RemoveWhere(title => !active.Contains(title));
        var fresh = new List<NearbyAnnouncement>();
        foreach (var group in performers.GroupBy(performer => performer.Title, StringComparer.Ordinal))
            if (announced.Add(group.Key)) fresh.Add(new(group.Key, Caption(group.Key, group.ToList())));
        return fresh;
    }

    public void Reset() => announced.Clear();

    /// <summary>
    /// “称号（Lv.等级 职业） 正在演奏《曲名》”（附近玩家没有昵称，没有称号时身份是“附近玩家”）；同一身份多人时带人数，
    /// 至多列出三种身份，多声部带声部数。
    /// </summary>
    public static string Caption(string title, IReadOnlyList<NearbyPerformer> performers)
    {
        var identities = performers.GroupBy(performer => performer.Identity).Select(group => group.Count() > 1 ? $"{group.Key} ×{group.Count()}" : group.Key).ToList();
        var names = string.Join(Pick("、", "、"), identities.Take(3));
        if (identities.Count > 3) names += Pick(" 等", " 等");
        var text = Pick($"{names} 正在演奏《{title}》", $"{names} 正在演奏《{title}》");
        var voices = performers.Max(performer => performer.Channels);
        return voices > 1 ? text + Pick($" · {voices} 声部", $" · {voices} 聲部") : text;
    }
}

/// <summary>
/// “附近演奏字幕”元件的显示状态：新出现的曲目提示一次并停留 <see cref="Hold"/>；读取失败（未读取）时不改变提示规则的状态，
/// 避免读取恢复后重复提示。
/// </summary>
public sealed class NearbyCaptionState(TimeSpan hold, Func<DateTimeOffset>? clock = null)
{
    /// <summary>字幕默认停留的秒数（“附近演奏字幕”的 holdSeconds 设置没有可编辑的摆放字段时的取值）。</summary>
    public const double DefaultHoldSeconds = 8;

    private readonly NearbyAnnouncer announcer = new();
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private string? text;
    private DateTimeOffset until;

    public TimeSpan Hold { get; } = hold;

    /// <summary>采用一次读取；返回字幕文字是否变化。</summary>
    public bool Observe(NearbyView view)
    {
        if (view.Unread) return false;
        var fresh = announcer.Update(view.Performers);
        if (fresh.Count == 0) return false;
        text = fresh[^1].Text;
        until = now() + Hold;
        return true;
    }

    /// <summary>当前应显示的字幕；已过停留时间或还没有提示时为空。</summary>
    public string? Current => text is not null && now() < until ? text : null;

    /// <summary>字幕是否还在停留（供定时器判断是否需要继续计时）。</summary>
    public bool Active => Current is not null;
}
