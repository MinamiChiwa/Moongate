using System.Globalization;
using MabiTool.Contracts;
using MabiTool.Desktop.Pages.Market;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Inventory;

/// <summary>估价列的一个格子：合计数字或原因文字；<see cref="Dim"/> 为真（不是已估价）时显示为次要色，<see cref="Tip"/> 是悬停提示。</summary>
public sealed record ValuationCell(string Text, bool Dim, string Tip);

/// <summary>
/// 背包估价的当前结果：<see cref="Requested"/> 为假表示估价开关没有打开（不请求、不显示）；
/// <see cref="Status"/> 是同步状态行下方的一行（交易所价格、单位德卡、取价时刻，读不到时写原因）；
/// <see cref="Total"/> 是概况里的已估价项合计（含无法估价的项时标“下限”）；<see cref="Cells"/> 按道具名称给出每格。
/// </summary>
public sealed record InventoryValuationView(bool Requested, string Status, string Total, IReadOnlyDictionary<string, ValuationCell> Cells)
{
    public static InventoryValuationView Off { get; } = new(false, string.Empty, string.Empty, new Dictionary<string, ValuationCell>());

    public bool HasStatus => Status.Length > 0;

    /// <summary>某个道具名称的估价格；没有结果（还没读到）时为“未读取”。</summary>
    public ValuationCell CellFor(string? name) => name is { Length: > 0 } && Cells.TryGetValue(name, out var cell)
        ? cell : new(Pick("未读取", "未讀取"), true, string.Empty);
}

/// <summary>估价结果 → 背包页显示的纯映射；价格单位德卡，读不到价格时每格为“未读取”，不显示 0。</summary>
public static class ValuationMapper
{
    public static ValuationCell Cell(GameMarketValuationRow row)
    {
        var unread = Pick("未读取", "未讀取");
        switch (row.State.KnownValue)
        {
            case GameMarketValuationState.Priced when row.UnitPrice is { } price && row.Total is { } total:
                return new(MarketMapper.Money(total), false, Pick($"交易所最低单价 {MarketMapper.Money(price)} × 持有 {row.Count.ToString("N0", CultureInfo.InvariantCulture)} = {MarketMapper.Money(total)} 德卡",
                    $"交易所最低單價 {MarketMapper.Money(price)} × 持有 {row.Count.ToString("N0", CultureInfo.InvariantCulture)} = {MarketMapper.Money(total)} 德卡"));
            case GameMarketValuationState.NoStock:
                return new(MarketText.NoStock, true, Pick("交易所有这个道具，但目前没有在售", "交易所有這個道具，但目前沒有在售"));
            case GameMarketValuationState.NotTradable:
                return new(Pick("不可交易", "不可交易"), true, Pick("交易所里没有这个名称", "交易所裡沒有這個名稱"));
            case GameMarketValuationState.MultipleVariants:
                return new(Pick("多品阶", "多品階"), true, Pick("同名对应多个品阶或星级的记录，不猜测品阶，未估价", "同名對應多個品階或星級的記錄，不猜測品階，未估價"));
            case GameMarketValuationState.NameTooShort:
                return new(Pick("名称太短", "名稱太短"), true, MarketText.Reason("gameCli.nameTooShort"));
            default:
                return new(unread, true, MarketText.Reason(row.Reason));
        }
    }

    /// <summary>估价状态行：“估价：交易所价格，单位德卡，取价于 HH:mm:ss”。</summary>
    public static string StatusText(DateTimeOffset? pricedAt)
    {
        var time = pricedAt is { } at ? at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : null;
        return time is null ? Pick("估价：交易所价格，单位德卡", "估價：交易所價格，單位德卡")
            : Pick($"估价：交易所价格，单位德卡，取价于 {time}", $"估價：交易所價格，單位德卡，取價於 {time}");
    }

    /// <summary>
    /// 把请求过的道具名称与已读到的估价行合成显示结果。<paramref name="failure"/> 是整体读不到的原因码（没有任何行时写进状态行）；
    /// 已估价项合计在含无法估价的项（无在售、不可交易、多品阶、未读取等）时标为下限。
    /// </summary>
    public static InventoryValuationView Map(IReadOnlyCollection<string> names, IReadOnlyDictionary<string, GameMarketValuationRow> rows,
        DateTimeOffset? pricedAt, string? failure)
    {
        var cells = new Dictionary<string, ValuationCell>(StringComparer.Ordinal);
        foreach (var name in names)
            cells[name] = rows.TryGetValue(name, out var row) ? Cell(row)
                : new(Pick("未读取", "未讀取"), true, MarketText.Reason(failure));
        var priced = rows.Values.Where(row => row.State.KnownValue == GameMarketValuationState.Priced && row.Total is not null).ToList();
        var status = rows.Count == 0
            ? Pick("估价：未读取", "估價：未讀取") + (failure is { Length: > 0 } ? "：" + MarketText.Reason(failure) : string.Empty)
            : StatusText(pricedAt);
        var lowerBound = names.Any(name => !rows.TryGetValue(name, out var row) || row.State.KnownValue != GameMarketValuationState.Priced);
        var total = priced.Count > 0
            ? MarketMapper.Money(priced.Sum(row => row.Total!.Value)) + Pick(" 德卡", " 德卡") + (lowerBound ? Pick("（下限）", "（下限）") : string.Empty)
            : rows.Count == 0 ? Pick("未读取", "未讀取") : Pick("没有可估价的项", "沒有可估價的項");
        return new(true, status, total, cells);
    }
}
