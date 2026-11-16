using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Host.Rooms;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Game;

public sealed class GameObservationFeed(GameObservationService observation, RoomSessionManager rooms,
    StateEventHub events, TimeProvider clock) : BackgroundService
{
    private readonly Channel<byte> changed = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private ulong revision;
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        observation.Changed += Signal;
        if (observation.Runtime().Installed) Signal();
        return base.StartAsync(cancellationToken);
    }
    private void Signal() => changed.Writer.TryWrite(0);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in changed.Reader.ReadAllAsync(stoppingToken))
        {
            var snapshot = observation.Snapshot();
            await events.PublishVolatileAsync(new(Guid.NewGuid().ToString("N"), "game-observation", "game-observation",
                new(++revision), "game.observation.changed", JsonSerializer.SerializeToElement(snapshot, ContractJson.Options)), clock.GetUtcNow(), stoppingToken);
            await rooms.PublishGameObservationAsync(snapshot, observation.Principals(), stoppingToken);
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    { observation.Changed -= Signal; await base.StopAsync(cancellationToken); }
}
