using MabiTool.Contracts;

namespace MabiTool.Host.GameCli;

/// <summary>读取类功能能否使用连接器：已连接、目录已取得且含所需指令；不能时给出连接器的原因码。</summary>
public readonly record struct GameCliReadiness(bool Ready, string Reason, IReadOnlyList<string> Missing, bool Unknown)
{
    public static GameCliReadiness Check(GameConnectorService connector, IEnumerable<string> commands)
    {
        var state = connector.Current;
        switch (state.Status.KnownValue)
        {
            case GameConnectorStatus.Connected:
                var catalog = connector.Cli.CachedCatalog();
                if (catalog is null || catalog.Loading) return new(false, "gameCli.catalogLoading", [], true);
                var missing = catalog.Missing(commands);
                return missing.Count == 0 ? new(true, "available", [], false) : new(false, "gameCli.commandMissing", missing, false);
            case GameConnectorStatus.Checking: return new(false, "gameCli.checking", [], true);
            case GameConnectorStatus.Disabled: return new(false, "gameCli.disabled", [], false);
            case GameConnectorStatus.GameNotRunning: return new(false, "gameCli.gameNotRunning", [], false);
            case GameConnectorStatus.Expired: return new(false, "gameCli.expired", [], false);
            default: return new(false, state.Reason, [], false);
        }
    }
}

/// <summary>抓包提供的、可以回落使用的事实：本机昵称、地图名、已读到数值的货币（钱包资源）。</summary>
public sealed record GameCaptureFacts(string? Nickname, string? MapName, DateTimeOffset? ObservedAt, bool Stale, IReadOnlyList<GameCurrency> Wallet)
{
    public static GameCaptureFacts None { get; } = new(null, null, null, false, []);
}

/// <summary>读取类能力：<c>environment.observe</c>、<c>nearby.observe</c>、<c>character.info</c>、<c>market.prices</c>，随连接器状态与目录生成。</summary>
public static class GameDataCapabilities
{
    public static readonly IReadOnlyList<string> EnvironmentCommands = ["get_current_environment"];
    public static readonly IReadOnlyList<string> NearbyCommands = ["get_near_pcs"];
    public static readonly IReadOnlyList<string> CharacterCommands = ["get_my_info", "get_currencies", "get_daily_missions", "get_weekly_missions"];
    public static readonly IReadOnlyList<string> MarketCommands = ["get_market_prices", "get_market_price_detail"];

    public static IReadOnlyList<CapabilitySnapshot> Snapshots(GameConnectorService connector, Func<GameCaptureFacts> capture, DateTimeOffset checkedAt)
    {
        var state = connector.Current;
        var session = state.Status.KnownValue == GameConnectorStatus.Connected ? connector.Cli.SessionKey : null;
        CapabilitySnapshot Make(string id, string module, IReadOnlyList<string> commands, bool fallback = false)
        {
            var ready = GameCliReadiness.Check(connector, commands);
            CapabilitySnapshot Result(CapabilityStatus status, string reason, IReadOnlyList<string>? limitations = null) =>
                new(id, module, status, GameConnectorCapabilities.Provider, state.Cli.Version, null, session, reason, checkedAt, limitations ?? []);
            if (ready.Ready) return Result(CapabilityStatus.Available, "available");
            if (ready.Unknown) return Result(CapabilityStatus.Unknown, ready.Reason);
            // 命令行不可用但抓包已读到地图时，环境降级而不是不可用。
            if (fallback && capture().MapName is { Length: > 0 }) return Result(CapabilityStatus.Degraded, ready.Reason, ["source.capture"]);
            return Result(CapabilityStatus.Unavailable, ready.Reason, ready.Missing);
        }
        return [Make("environment.observe", "environment", EnvironmentCommands, fallback: true),
            Make("nearby.observe", "game-access", NearbyCommands),
            Make("character.info", "game-access", CharacterCommands),
            Make("market.prices", "game-access", MarketCommands)];
    }
}
