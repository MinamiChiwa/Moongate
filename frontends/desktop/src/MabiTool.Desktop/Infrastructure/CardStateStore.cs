using System.Text.Json.Serialization;
using MabiTool.Desktop.Controls;

namespace MabiTool.Desktop.Infrastructure;

/// <summary>
/// 一张卡片在某个列数的网格里保存的位置：尺寸档与顺序。为空的一项取页面缺省（缺省档、页面里写的先后）。
/// </summary>
public sealed record CardSlot(CardSize? Size = null, int? Order = null);

/// <summary>
/// 一张卡片保存的状态：展开状态（为空表示没有保存过，取卡片自己的缺省值），以及按网格列数（3、2、1）分别保存的位置
/// （<see cref="Layout"/>，没有某个列数的项时该列数取页面缺省布局）。
/// </summary>
public sealed record CardState(bool? Expanded = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<int, CardSlot>? Layout = null);

/// <summary>
/// 全应用卡片状态的唯一存取处：按“页面标识/卡片标识”记下用户的选择，由前端偏好文件的 <c>Cards</c> 保存。
/// 卡片外壳（<see cref="Card"/>）、卡片网格（<see cref="CardGrid"/>）和少数自己排版的折叠区（合奏页底部状态卡、染色页“默认预设”）都读写这里。
/// </summary>
public static class CardStateStore
{
    private static Dictionary<string, CardState> cards = new(StringComparer.Ordinal);

    /// <summary>某张卡片的展开状态变了，参数是它的键（<see cref="Key"/>）。</summary>
    public static event EventHandler<string>? Changed;

    /// <summary>某一页的布局（顺序或尺寸档）变了，参数是页面标识。</summary>
    public static event EventHandler<string>? LayoutChanged;

    public static IReadOnlyDictionary<string, CardState> All => cards;

    public static string Key(string page, string card) => $"{page}/{card}";

    /// <param name="fallback">没有保存过时的取值，即卡片的缺省状态。</param>
    public static bool IsExpanded(string page, string card, bool fallback = true) =>
        cards.TryGetValue(Key(page, card), out var state) && state.Expanded is { } expanded ? expanded : fallback;

    public static void SetExpanded(string page, string card, bool expanded, bool fallback = true)
    {
        if (IsExpanded(page, card, fallback) == expanded) return;
        var key = Key(page, card);
        cards[key] = (cards.GetValueOrDefault(key) ?? new CardState()) with { Expanded = expanded };
        Changed?.Invoke(null, key);
    }

    /// <summary>某张卡片在 <paramref name="columns"/> 列网格里保存的位置；没有保存过时为空。</summary>
    public static CardSlot? Slot(string page, string card, int columns) =>
        cards.TryGetValue(Key(page, card), out var state) && state.Layout is { } layout ? layout.GetValueOrDefault(columns) : null;

    /// <summary>保存一页在 <paramref name="columns"/> 列下的布局：<paramref name="slots"/> 按顺序给出各卡片的标识与尺寸档。</summary>
    public static void SetLayout(string page, int columns, IReadOnlyList<(string Card, CardSize Size)> slots)
    {
        for (var index = 0; index < slots.Count; index++)
        {
            var key = Key(page, slots[index].Card);
            var state = cards.GetValueOrDefault(key) ?? new CardState();
            var layout = state.Layout is null ? new Dictionary<int, CardSlot>() : new Dictionary<int, CardSlot>(state.Layout);
            layout[columns] = new CardSlot(slots[index].Size, index);
            cards[key] = state with { Layout = layout };
        }
        LayoutChanged?.Invoke(null, page);
    }

    /// <summary>去掉一页保存的布局，回到页面缺省；<paramref name="columns"/> 为空时去掉各列数的。折叠状态不变。</summary>
    public static void ClearLayout(string page, int? columns = null)
    {
        var changed = false;
        foreach (var (key, state) in cards.Where(pair => pair.Key.StartsWith(page + "/", StringComparison.Ordinal)).ToList())
        {
            if (state.Layout is null || (columns is { } only && !state.Layout.ContainsKey(only))) continue;
            var layout = columns is { } column ? state.Layout.Where(pair => pair.Key != column).ToDictionary() : [];
            var next = state with { Layout = layout.Count == 0 ? null : layout };
            if (next is { Expanded: null, Layout: null }) cards.Remove(key); else cards[key] = next;
            changed = true;
        }
        if (changed) LayoutChanged?.Invoke(null, page);
    }

    /// <summary>
    /// 一页里的若干卡片回到缺省：去掉它们保存的布局（各列数）与折叠状态，不留保存项。编辑布局里“恢复默认”后按“完成”时使用。
    /// </summary>
    public static void ResetCards(string page, IReadOnlyCollection<string> ids)
    {
        var folded = new List<string>();
        var changed = false;
        foreach (var key in ids.Select(id => Key(page, id)))
        {
            if (!cards.Remove(key, out var state)) continue;
            changed = true;
            if (state.Expanded is not null) folded.Add(key);
        }
        foreach (var key in folded) Changed?.Invoke(null, key);
        if (changed) LayoutChanged?.Invoke(null, page);
    }

    /// <summary>读取偏好时恢复，不触发保存。</summary>
    internal static void Restore(IReadOnlyDictionary<string, CardState>? saved) =>
        cards = saved is null ? new(StringComparer.Ordinal) : new Dictionary<string, CardState>(saved, StringComparer.Ordinal);
}
