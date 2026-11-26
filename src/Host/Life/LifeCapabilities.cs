using MabiTool.Contracts;
using MabiTool.Host.GameCli;
using MabiTool.Providers.TwCli;

namespace MabiTool.Host.Life;

/// <summary>由连接器状态与指令目录生成的能力表条目：<c>life.gather</c>、<c>life.altering</c>、<c>life.crafting</c>（所需指令都在运行时目录里才可用）。</summary>
public static class LifeCapabilities
{
    public static IReadOnlyList<CapabilitySnapshot> Snapshots(GameConnectorService service, DateTimeOffset checkedAt)
    {
        var state = service.Current;
        var connected = state.Status.KnownValue == GameConnectorStatus.Connected;
        var catalog = connected ? service.Cli.CachedCatalog() : null;
        var session = connected ? service.Cli.SessionKey : null;
        CapabilitySnapshot Make(string id, IReadOnlyList<string> required)
        {
            CapabilitySnapshot Result(CapabilityStatus status, string reason, IReadOnlyList<string>? limitations = null) =>
                new(id, "life", status, GameConnectorCapabilities.Provider, state.Cli.Version, null, session, reason, checkedAt, limitations ?? []);
            switch (state.Status.KnownValue)
            {
                case GameConnectorStatus.Connected:
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
        return [Make("life.gather", TwCliLifeControl.GatherCommands), Make("life.altering", TwCliLifeControl.AlterCommands), Make("life.crafting", TwCliLifeControl.CraftCommands)];
    }
}
