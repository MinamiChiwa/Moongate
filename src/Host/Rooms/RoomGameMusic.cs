using MabiTool.ModuleSdk;
using MabiTool.Modules.Rooms.Domain;

namespace MabiTool.Host.Rooms;

/// <summary>合奏的游戏连接器端口：把 <see cref="IGameMusicControl"/> 适配为准备乐器、开奏与停止。</summary>
public sealed class RoomGameMusic(IGameMusicControl music) : IRoomGameMusic
{
    private static readonly TimeSpan WarmAge = TimeSpan.FromSeconds(30);

    public bool Available => music.Availability.Available;
    public double StartLeadMs => music.RecentCallLatencyMs;
    public bool? TryIsPlayable(string title) => music.TryIsGameProvided(title);

    public async ValueTask<bool?> WarmAsync(string? title, CancellationToken ct)
    {
        if (!Available) return null;
        var listing = await music.ListScoresAsync(WarmAge, ct).ConfigureAwait(false);
        return !listing.Available || title is null ? null : music.TryIsGameProvided(title);
    }

    public async ValueTask<GameInstrumentPreparation> PrepareInstrumentAsync(GameActivityProof proof, string? instrumentName, CancellationToken ct)
    {
        if (!Available || string.IsNullOrWhiteSpace(instrumentName)) return new(null);
        var owned = await music.ListInstrumentsAsync(ct).ConfigureAwait(false);
        // 读不到持有乐器说明连接器当前不可用：按原流程就绪，不因此失败。
        if (owned is null) return new(null);
        var match = GameMusicNames.MatchInstrument(owned, instrumentName, out var ambiguous);
        if (match is null) return new(ambiguous ? "room.instrumentAmbiguous" : "room.instrumentNotOwned");
        if (match.Equipped) return new(null);
        var result = await music.ChangeInstrumentAsync(proof, match.Name, ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            GameMusicOutcome.Accepted or GameMusicOutcome.Unavailable => new(null),
            _ => new("room.instrumentChangeFailed", result.Code)
        };
    }

    public async ValueTask<GameMusicResult> StartAsync(GameActivityProof proof, string title, CancellationToken ct)
    {
        // 开奏发生在同步目标时刻，只用已缓存的列表，不在关键时刻再调命令行取列表。
        var listing = await music.ListScoresAsync(TimeSpan.FromHours(1), ct).ConfigureAwait(false);
        var key = GameMusicNames.Title(title);
        var provided = listing.Scores.FirstOrDefault(x => GameMusicNames.Title(x.DisplayTitle) == key);
        return provided is null
            ? new(GameMusicOutcome.Rejected, "gameCli.userComposedNotAllowed")
            : await music.PlayScoreAsync(proof, provided.DisplayTitle, ct).ConfigureAwait(false);
    }

    public ValueTask<GameMusicResult> StopAsync(GameActivityProof proof, CancellationToken ct) => music.StopAsync(proof, ct);
}
