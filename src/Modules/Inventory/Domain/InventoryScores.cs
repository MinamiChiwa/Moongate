// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Globalization;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Modules.Inventory.Domain;

/// <summary>
/// 乐谱实例的元数据与名称。客户端名称目录不收录 MusicNoteItem，每张乐谱的标题、作曲、编曲等随库存属性 <c>data</c>
/// （客户端 MusicNoteItem 记录）保存；名称在读取时由它投影，不改变保存的条目。
/// </summary>
public static class InventoryScores
{
    public const string Category = "MusicNoteItem";
    public const string InstanceSource = "item-instance";
    public const string TitleEvidence = "MusicNoteItem.data.Title";
    private const int MaximumTitle = 256;

    /// <summary>乐谱条目的实例元数据；其他类别或没有 <c>data</c> 对象时为空。表编号取条目类型编号（自制谱为 0）。</summary>
    public static InventoryScoreInfo? Info(InventoryItem item)
    {
        if (item.Category != Category || Field(item.Attributes, "data") is not { ValueKind: JsonValueKind.Object } data) return null;
        return new(item.TypeId, Text(Field(data, "Title")), Text(Field(data, "Composer")), Realm(Field(data, "ComposerRealmId")),
            Text(Field(data, "Arranger")), Realm(Field(data, "ArrangerRealmId")), Flag(Field(data, "IsCopyingAllowed")),
            Flag(Field(data, "IsFavorite")), Flag(Field(data, "IsLocked")), Flag(Field(data, "IsJunked")));
    }

    /// <summary>
    /// 实例标题作为名称：标题非空、不含控制字符且不超过 256 字时返回实例名称，否则为空（调用方沿用按类型的关联）。
    /// 自制谱共用表编号 0，按类型的关联无法区分各张，因此实例标题优先于按类型的关联。
    /// </summary>
    public static InventoryNameAssociation? Name(InventoryItem item, InventoryScoreInfo? info, bool? isInstrument = null) =>
        info?.Title is { } title && !string.IsNullOrWhiteSpace(title) && title.Length <= MaximumTitle && !title.Any(char.IsControl)
            ? new(item.Type, title, title.Trim(), InventoryNameState.ItemInstance, InstanceSource, TitleEvidence, isInstrument)
            : null;

    /// <summary>搜索词命中作曲或编曲。</summary>
    public static bool Matches(InventoryScoreInfo? info, string search) =>
        info is not null && (info.Composer?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                             info.Arranger?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);

    private static JsonElement Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { Length: <= 4096 } value && !value.Any(char.IsControl) ? value : null;

    private static bool? Flag(JsonElement element) => element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : null;

    /// <summary>RealmId 在解码结果里是数字、数字字符串或只含 <c>rawValue</c>／<c>Id</c> 的对象。</summary>
    private static int? Realm(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Count() == 1 &&
            (element.TryGetProperty("rawValue", out var inner) || element.TryGetProperty("Id", out inner))) element = inner;
        var raw = element.ValueKind switch { JsonValueKind.String => element.GetString(), JsonValueKind.Number => element.GetRawText(), _ => null };
        return int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
