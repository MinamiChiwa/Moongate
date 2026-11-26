using MabiTool.Contracts;
using MabiTool.Providers.TwCli;

namespace MabiTool.Host.GameCli;

/// <summary>由连接器状态与指令目录生成的能力表条目：<c>game.connector</c>、<c>performance.play</c>、<c>performance.instrument</c>、<c>music.inventory</c>。</summary>
public static class GameConnectorCapabilities
{
    public const string Provider = "tw-cli";
    private static readonly string[] InventoryCommands = ["get_music_scores"];

    public static IReadOnlyList<CapabilitySnapshot> Snapshots(GameConnectorService service, DateTimeOffset checkedAt)
    {
        var state = service.Current;
        var catalog = state.Status.KnownValue == GameConnectorStatus.Connected ? service.Cli.CachedCatalog() : null;
        var session = state.Status.KnownValue == GameConnectorStatus.Connected ? service.Cli.SessionKey : null;
        CapabilitySnapshot Make(string id, string module, IEnumerable<string>? required)
        {
            CapabilitySnapshot Result(CapabilityStatus status, string reason, IReadOnlyList<string>? limitations = null) =>
                new(id, module, status, Provider, state.Cli.Version, null, session, reason, checkedAt, limitations ?? []);
            switch (state.Status.KnownValue)
            {
                case GameConnectorStatus.Connected:
                    if (required is null) return Result(CapabilityStatus.Available, "available");
                    if (catalog is null || catalog.Loading) return Result(CapabilityStatus.Unknown, "gameCli.catalogLoading");
                    var missing = catalog.Missing(required);
                    return missing.Count == 0 ? Result(CapabilityStatus.Available, "available") : Result(CapabilityStatus.Unavailable, "gameCli.commandMissing", missing);
                case GameConnectorStatus.Checking: return Result(CapabilityStatus.Unknown, "gameCli.checking");
                case GameConnectorStatus.Disabled: return Result(CapabilityStatus.Unavailable, "gameCli.disabled");
                case GameConnectorStatus.GameNotRunning: return Result(CapabilityStatus.Unavailable, "gameCli.gameNotRunning");
                case GameConnectorStatus.Expired: return Result(CapabilityStatus.Unavailable, "gameCli.expired");
                default: return Result(CapabilityStatus.Unavailable, state.Reason);
            }
        }
        return [Make("game.connector", "game-access", null),
            Make("performance.play", "performance", TwCliMusicControl.PlayCommands),
            Make("performance.instrument", "performance", TwCliMusicControl.InstrumentCommands),
            Make("music.inventory", "music-library", InventoryCommands)];
    }
}
