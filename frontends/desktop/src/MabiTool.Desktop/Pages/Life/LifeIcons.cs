using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MabiTool.Desktop.Pages.Life;

/// <summary>
/// 生活页与生活浮窗的图标槽（<see cref="Theme.IconRegistry"/> 的 <c>life.*</c>）与色块颜色。按游戏给出的设施与技能显示名选槽，只决定外观，
/// 不参与分类；没有对应的名称用 <c>other</c> 槽。素材包可以按槽名替换成游戏图标。
/// </summary>
public static class LifeIcons
{
    private static readonly Dictionary<string, string> Facilities = new(StringComparer.Ordinal)
    {
        ["金屬加工設備"] = "metal", ["木材加工設備"] = "wood", ["布料加工設備"] = "cloth", ["皮革加工設備"] = "leather",
        ["藥品加工設備"] = "herb", ["食材加工設備"] = "food"
    };

    private static readonly Dictionary<string, string> Crafts = new(StringComparer.Ordinal)
    {
        ["武器製作台"] = "weapon", ["防具製作台"] = "armor", ["藥品製作台"] = "potion", ["食物製作台"] = "food",
        ["多用途製作台"] = "multi", ["裝飾製作台"] = "decor", ["立即製作"] = "instant"
    };

    private static readonly Dictionary<string, string> Skills = new(StringComparer.Ordinal)
    {
        ["伐木"] = "logging", ["採礦"] = "mining", ["採集藥草"] = "herb", ["日常採集"] = "daily", ["昆蟲採集"] = "insect",
        ["剪羊毛"] = "wool", ["收割"] = "harvest", ["鋤地"] = "hoe", ["釣魚"] = "fishing"
    };

    /// <summary>技能分组的显示顺序（与游戏生活技能页一致），不在其中的排在后面，“其他”最后。</summary>
    public static readonly IReadOnlyList<string> SkillOrder = ["伐木", "採礦", "採集藥草", "日常採集", "昆蟲採集", "剪羊毛", "收割", "鋤地", "釣魚"];

    private static readonly Dictionary<string, Color> Colors = new(StringComparer.Ordinal)
    {
        ["metal"] = Color.Parse("#5F6F86"), ["wood"] = Color.Parse("#9A6230"), ["cloth"] = Color.Parse("#7457B8"), ["leather"] = Color.Parse("#8E5632"),
        ["herb"] = Color.Parse("#2E8B57"), ["food"] = Color.Parse("#C26A1E"), ["weapon"] = Color.Parse("#B0453A"), ["armor"] = Color.Parse("#3F6FB0"),
        ["potion"] = Color.Parse("#2F8F7A"), ["multi"] = Color.Parse("#6B7280"), ["decor"] = Color.Parse("#B85C9E"), ["instant"] = Color.Parse("#C98A12"),
        ["logging"] = Color.Parse("#4F7F2F"), ["mining"] = Color.Parse("#66707F"), ["daily"] = Color.Parse("#B07A2A"), ["insect"] = Color.Parse("#7E8F1F"),
        ["wool"] = Color.Parse("#8D7BB5"), ["harvest"] = Color.Parse("#B8962A"), ["hoe"] = Color.Parse("#8A6A3A"), ["fishing"] = Color.Parse("#2F7FB8"),
        ["other"] = Color.Parse("#5F6B85")
    };

    public static string FacilitySlot(string? name) => "life.facility." + Kind(Facilities, name);
    public static string CraftSlot(string? name) => "life.craft." + Kind(Crafts, name);
    public static string SkillSlot(string? name) => "life.skill." + Kind(Skills, name);

    public static IBrush FacilityAccent(string? name) => Brush(Kind(Facilities, name));
    public static IBrush CraftAccent(string? name) => Brush(Kind(Crafts, name));
    public static IBrush SkillAccent(string? name) => Brush(Kind(Skills, name));

    /// <summary>任务种类的图标槽：采集取所属技能的槽（<paramref name="place"/> 为技能名，未知时为通用），加工与领取取设施的槽，制作取制作设施的槽。</summary>
    public static string TaskSlot(string kind, string? place) => kind switch
    {
        "alter" or "collect" => FacilitySlot(place),
        "craft" => CraftSlot(place),
        _ => SkillSlot(place)
    };

    public static IBrush TaskAccent(string kind, string? place) => kind switch
    {
        "alter" or "collect" => FacilityAccent(place),
        "craft" => CraftAccent(place),
        _ => SkillAccent(place)
    };

    private static string Kind(Dictionary<string, string> map, string? name) => name is not null && map.TryGetValue(name, out var kind) ? kind : "other";

    private static readonly Dictionary<string, IBrush> Brushes = new(StringComparer.Ordinal);

    private static IBrush Brush(string kind)
    {
        if (!Brushes.TryGetValue(kind, out var brush)) Brushes[kind] = brush = new ImmutableSolidColorBrush(Colors[kind]);
        return brush;
    }
}
