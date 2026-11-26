using MabiTool.Contracts;
using MabiTool.Host.Game;

namespace MabiTool.Host.GameCli;

/// <summary>
/// 抓包能回落的事实：本机身份的昵称、进图位置的地图名（取共享抓包的最近观测）、钱包资源里当前观测到数值且有客户端名称的货币。
/// 观测已过期时仍然返回并标为旧数据，不把过期值当作实时值。
/// </summary>
public static class GameCaptureFactsSource
{
    public static Func<GameCaptureFacts> From(GameObservationService observation, Func<InventoryResourcesView?>? wallet = null) => () =>
    {
        var latest = observation.Snapshot().Latest;
        var identity = latest?.Value.Identity;
        var nickname = identity?.Nickname is { Length: > 0 } name ? name : null;
        var map = latest?.Value.Location?.MapName is { Length: > 0 } area ? area : null;
        return new(nickname, map, latest?.ObservedAt, latest?.Quality.KnownValue is ObservationQuality.Stale, Wallet(wallet?.Invoke()));
    };

    /// <summary>当前（实时）观测、已有数值与名称的钱包项；没有实时观测的历史值不回落。</summary>
    public static IReadOnlyList<GameCurrency> Wallet(InventoryResourcesView? view) => view is null ? [] :
        [.. view.Items.Where(x => x.Current && x.State == "observed" && x.Value is not null && x.RawName is { Length: > 0 })
            .Select(x => new GameCurrency(x.RawName!, x.Value!.Value.Value))];
}
