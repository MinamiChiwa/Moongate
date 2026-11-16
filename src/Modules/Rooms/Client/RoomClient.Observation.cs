using System.Globalization;
using MabiTool.Contracts;

namespace MabiTool.Modules.Rooms.Client;

public sealed partial class RoomClient
{
    public Task ReportGameObservationAsync(GameObservationValue? value, string generation, long sequence,
        string? roundId, CancellationToken ct)
    {
        bool Compatible(ObservedGameIdentity item) => int.TryParse(item.RealmId, NumberStyles.None, CultureInfo.InvariantCulture, out var realm) &&
            realm >= 0 && !string.IsNullOrWhiteSpace(item.Nickname) && item.Nickname.EnumerateRunes().Count() <= 20;
        var identity = value?.Identity is { } observed && Compatible(observed) && value.Concert?.Members.All(Compatible) != false ? observed : null;
        object? Character(ObservedGameIdentity? item) => item is null ? null : new
        { characterId = item.CharacterId.ToString(), realmId = int.Parse(item.RealmId, CultureInfo.InvariantCulture), nickname = item.Nickname };
        var concert = identity is null ? null : value?.Concert;
        var music = identity is null ? null : value?.Music;
        // 游戏内演奏自动循环、不会自然停止，一轮结束只能按开始时间与整谱时长推算；开始时间为抓包估算（tw-receive-offset-v1）。
        var estimated = music?.EstimatedStartMonotonicMs is { } start && music.TimingSource == "tw-receive-offset-v1" && _roomClock.Estimate is { } clock
            ? start + clock.OffsetMs : (double?)null;
        return SendAsync(new { type = "game-observation", generation, sequence, sessionId = roundId,
            status = identity is null ? "unavailable" : "identified", identity = Character(identity),
            concert = concert is null ? null : new { id = concert.Id.ToString(), hostCharacterId = concert.HostCharacterId.ToString(),
                // 房间协议 v1（含 MabiSync 0.1.8 服务）要求成员附带同序的角色 ID 列表；这里只按候选成员填写，客户端不做顺序核对。
                orderedCharacterIds = concert.Members.Select(member => member.CharacterId.ToString()).ToArray(),
                members = concert.Members.Select(Character).ToArray() },
            music = music is null ? null : new { concertId = music.ConcertId.ToString(), performerCharacterId = music.PerformerCharacterId.ToString(),
                hostCharacterId = music.HostCharacterId.ToString(), title = music.Title, isLoop = music.IsLoop, selectedChannels = music.SelectedChannels,
                scoreSha256 = music.ScoreSha256, wholeScoreDurationSeconds = music.WholeScoreDurationSeconds,
                selectedDurationSeconds = music.SelectedDurationSeconds, issue = music.Issue,
                timingSource = music.TimingSource, estimatedStartServerMs = estimated } }, ct);
    }
}
