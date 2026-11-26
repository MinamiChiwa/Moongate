using MabiTool.Desktop.Pages.Character;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Market;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 游戏连接器的数据读取（第二批）与交易所查价、背包估价（第三批）。数据读取由界面可见性驱动：角色页、环境信息与附近演奏浮窗元件
// 显示时才向后端发数据查询，切走即停；成员都带默认实现，只提供部分接口的后端（测试替身）不必逐个实现。
public partial interface IDesktopBackend
{
    /// <summary>角色信息页的数据；桌面没连上后端、还没读到或后端没有数据接口时由 <see cref="CharacterPageData.Read"/> 标明，值为“未读取”。</summary>
    CharacterPageData GetCharacter() => CharacterPageData.Offline;

    /// <summary>“环境信息”浮窗元件与角色页环境卡的数据；没读到时 <see cref="EnvironmentView.Unread"/> 为真。</summary>
    EnvironmentView GetEnvironment() => EnvironmentView.NotRead;

    /// <summary>“附近演奏字幕”浮窗元件与角色页附近卡的数据；没读到时 <see cref="NearbyView.Unread"/> 为真（不是“附近没有玩家”）。</summary>
    NearbyView GetNearby() => NearbyView.NotRead;

    /// <summary>
    /// 背包估价的当前结果；估价开关没有打开（没有可见使用者，见可见处 <c>inventory.valuation</c>）时 <see cref="InventoryValuationView.Requested"/> 为假。
    /// 后端适配层只在背包页可见且开关打开时按名称向交易所取最低单价，每 60 秒更新一次。
    /// </summary>
    InventoryValuationView GetInventoryValuation() => InventoryValuationView.Off;

    /// <summary>价目查询（名称、分类、收藏）；只读，价格单位德卡。失败时结果带原因，不显示价格；不自动重发。</summary>
    Task<MarketPricesView> QueryMarketPricesAsync(MarketPriceRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(MarketPricesView.None with { Reason = "gameCli.notConnected", Message = Pick("未连接后端", "未連接後端") });

    /// <summary>价格明细（最低与最高挂单价、平均成交价、至多三档价位）；游戏限制每秒一次，冷却由结果的等待秒数表达。</summary>
    Task<MarketDetailView> QueryMarketDetailAsync(MarketDetailRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(MarketDetailView.None with { Reason = "gameCli.notConnected", Message = Pick("未连接后端", "未連接後端") });
}
