using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.GameCli;

/// <summary>乐谱能否经连接器在游戏里演奏：标题（去除富文本标记与首尾空白）在游戏提供的乐谱列表里为可演奏，不在为玩家自制，无法判断为未知。</summary>
public sealed class GameScoreSupportService(GameConnectorService connector, IGameMusicControl music,
    Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, string>> rawTitles, string backendEpoch)
{
    public const int MaximumIds = 200;
    private static readonly TimeSpan ListingAge = TimeSpan.FromSeconds(30);

    public async Task<GameScoreSupportPage> QueryAsync(GameScoreSupportQuery query, CancellationToken ct)
    {
        if (query?.ScoreIds is null || query.ScoreIds.Count is < 1 or > MaximumIds ||
            query.ScoreIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)))
            throw new OperationException("request.invalid", "乐谱可演奏性查询需要 1–200 个乐谱 ID");
        var ids = query.ScoreIds.Distinct(StringComparer.Ordinal).ToArray();
        var state = connector.Current;
        IReadOnlyList<GameScoreSupport> Unknown(string reason) => ids.Select(id => new GameScoreSupport(id, GameScoreSupportState.Unknown, reason)).ToArray();
        if (state.Status.KnownValue != GameConnectorStatus.Connected)
            return new(backendEpoch, state.Status, Unknown(connector.MusicAvailability().Reason));
        var listing = await music.ListScoresAsync(ListingAge, ct).ConfigureAwait(false);
        if (!listing.Available) return new(backendEpoch, state.Status, Unknown(listing.Reason ?? "gameCli.notConnected"));
        var titles = rawTitles(ids);
        var items = new List<GameScoreSupport>(ids.Length);
        foreach (var id in ids)
        {
            if (!titles.TryGetValue(id, out var title)) { items.Add(new(id, GameScoreSupportState.Unknown, "gameCli.notFound")); continue; }
            items.Add(music.TryIsGameProvided(title) switch
            {
                true => new(id, GameScoreSupportState.Supported, null),
                false => new(id, GameScoreSupportState.UserComposed, "gameCli.userComposedNotAllowed"),
                _ => new(id, GameScoreSupportState.Unknown, "gameCli.catalogLoading")
            });
        }
        return new(backendEpoch, state.Status, items);
    }
}
