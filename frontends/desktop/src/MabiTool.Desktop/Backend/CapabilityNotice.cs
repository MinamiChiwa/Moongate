using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

/// <summary>后端能力编号。前端只为这些编号对应的主操作显示不可用说明；编号以后端能力表为准。</summary>
public static class CapabilityIds
{
    public const string PerformancePlay = "performance.play";
    /// <summary>演奏前自动换乐器（游戏连接器的 <c>get_instruments</c>、<c>change_instrument</c>）。</summary>
    public const string PerformanceInstrument = "performance.instrument";
    /// <summary>乐谱库与合奏查询背包乐器。</summary>
    public const string MusicInventory = "music.inventory";
    /// <summary>游戏连接器本身（台服游戏自带的命令行工具）。</summary>
    public const string GameConnector = "game.connector";
    /// <summary>环境信息（频道、区域、天气、游戏内时间；游戏连接器的 <c>get_current_environment</c>）。</summary>
    public const string EnvironmentObserve = "environment.observe";
    /// <summary>附近玩家及其演奏状态（<c>get_near_pcs</c>）。</summary>
    public const string NearbyObserve = "nearby.observe";
    /// <summary>角色信息页的命令行数据（属性、货币、日常与周常任务）。</summary>
    public const string CharacterInfo = "character.info";
    /// <summary>交易所查价（<c>get_market_prices</c>、<c>get_market_price_detail</c>）。</summary>
    public const string MarketPrices = "market.prices";
    public const string RoomsCoordinate = "rooms.coordinate";
    public const string ColorSearch = "color.search";
    public const string InventoryFull = "inventory.full";
    /// <summary>怪物血量与当前攻击目标；来自战斗能力表（GET /combat/capabilities）。</summary>
    public const string MonsterHealth = "combat.health.monster";
}

/// <summary>能力表里一项的状态，供界面显示状态行；与 <see cref="CapabilityNotice.Describe"/> 同源，不可用才有说明。</summary>
public enum CapabilityLevel { Unknown, Available, Degraded, Unavailable }

/// <summary>把能力表里的一项排成界面上的短句；只有状态为不可用时才给出说明。</summary>
public static class CapabilityNotice
{
    public static string? Describe(CapabilitySnapshot? capability)
    {
        if (capability is null || capability.Status.KnownValue != CapabilityStatus.Unavailable) return null;
        return capability.ReasonCode switch
        {
            "module.notImplemented" => Pick("后端未实现，暂不可用", "後端未實作，暫不可用"),
            // 游戏连接器的原因（未启用、未连接游戏、已失效、命令行工具缺失等）后面指向侧栏的“游戏连接器”胶囊。
            { } code when code.StartsWith("gameCli.", StringComparison.Ordinal) =>
                Pick("暂不可用：", "暫不可用：") + Localization.CodeText.Reason(code) + ConnectorText.Pointer,
            var code => Pick("暂不可用：", "暫不可用：") + Localization.CodeText.Reason(code)
        };
    }
}

/// <summary>
/// 展示接口里某个主题的缺失说明。<see cref="ReasonCode"/> 为 provider.unavailable 表示台服没有该数据来源，
/// 此时浮窗里不显示对应元件；其他原因显示“暂不可用：原因说明”。
/// </summary>
public sealed record TopicNotice(string? ReasonCode, string Text)
{
    public const string ProviderUnavailable = "provider.unavailable";
    public bool PlatformUnsupported => ReasonCode == ProviderUnavailable;
}

/// <summary>展示接口的主题名。</summary>
public static class PresentationTopics
{
    public const string Nearby = "nearby";
    public const string Environment = "environment";
    public static readonly string[] Watched = [Nearby, Environment];
}

public static class TopicNoticeText
{
    /// <summary>主题状态不是不可用时没有说明；缺失原因为 provider.unavailable 时说明台服暂不支持。</summary>
    public static TopicNotice? Describe(PresentationSourceStatus? source)
    {
        if (source is null || source.Status.KnownValue != CapabilityStatus.Unavailable) return null;
        return source.Reason == TopicNotice.ProviderUnavailable
            ? new(source.Reason, Pick("台服暂不支持", "台服暫不支援"))
            : new(source.Reason, source.Reason is { Length: > 0 } code
                ? Pick("暂不可用：", "暫不可用：") + Localization.CodeText.Reason(code)
                : Pick("暂不可用", "暫不可用"));
    }
}
