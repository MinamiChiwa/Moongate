using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Inventory;

/// <summary>
/// 背包道具类别代码的显示名（简体、繁体）。改词只改这张表；没有登记的代码原样显示。
/// JamItem 台服目前没有这类道具，不登记译名。台服类别代码取自台服客户端的背包类别与 <c>contracts/fixtures/inventory*.json</c>；小写代码是界面样本里的类别。
/// </summary>
public static class InventoryCategoryNames
{
    private static readonly Dictionary<string, (string Simplified, string Traditional)> Names = new(StringComparer.Ordinal)
    {
        ["AccompanyEquipment"] = ("坐骑装备", "坐騎裝備"),
        ["AquariumFish"] = ("观赏鱼", "觀賞魚"),
        ["Artifact"] = ("神器", "神器"),
        ["Bag"] = ("背包", "背包"),
        ["Chair"] = ("椅子", "椅子"),
        ["Consumable"] = ("消耗品", "消耗品"),
        ["Costume"] = ("时装", "時裝"),
        ["Equipment"] = ("装备", "裝備"),
        ["EquipmentTool"] = ("工具", "工具"),
        ["HousingProp"] = ("装饰", "裝飾"),
        ["MemorialSagaItem"] = ("纪念传说道具", "紀念傳說道具"),
        ["MemorialSagaMinion"] = ("纪念传说伙伴", "紀念傳說夥伴"),
        ["MusicNoteItem"] = ("乐谱", "樂譜"),
        ["Pet"] = ("宠物", "寵物"),
        ["Ridable"] = ("坐骑", "坐騎"),
        ["Socketable"] = ("宝石·符文", "寶石·符文"),
        // 界面样本里的类别。
        ["instrument"] = ("乐器", "樂器"),
        ["equipment"] = ("装备", "裝備"),
        ["consumable"] = ("消耗品", "消耗品"),
        ["material"] = ("材料", "材料"),
        ["misc"] = ("杂项", "雜項")
    };

    /// <summary>当前界面语言下的显示名；没有登记时返回代码原文。</summary>
    public static string Display(string code) =>
        Names.TryGetValue(code, out var name) ? UiText.Pick(name.Simplified, name.Traditional) : code;
}
