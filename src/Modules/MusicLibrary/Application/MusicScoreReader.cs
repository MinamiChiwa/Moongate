using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;
using MabiTool.Modules.MusicLibrary.Persistence;

namespace MabiTool.Modules.MusicLibrary.Application;

/// <summary>跨模块使用固定来源和修订读取乐谱；内容哈希与历史文件哈希分别判定。</summary>
public sealed class MusicScoreReader(ScoreCatalogStore catalogs, MusicLibraryStore library, ScoreTimelineStore timelines)
{
    public MusicResolvedScore Read(string owner, MusicScoreSelection selection)
    {
        if (selection is null || selection.ScoreId is null || selection.ScoreId.Length is < 1 or > 128 ||
            selection.ScoreId.Any(char.IsControl) || selection.PartId is { Length: > 128 } || selection.PartId == "" ||
            selection.PartId?.Any(char.IsControl) == true)
            throw new OperationException("request.invalid", "乐谱选择无效");
        var source = catalogs.ReadByKey(selection.ScopeKey) ?? throw new OperationException("room.scoreMissing", "本地乐谱来源不存在");
        if (source.Revision != selection.SourceRevision.Value) throw new OperationException("revision.stale", "本地乐谱来源修订已变化");
        var score = source.Entries.SingleOrDefault(item => item.Id == selection.ScoreId)
            ?? throw new OperationException("room.scoreMissing", "本地乐谱不存在");
        var settings = library.Read().NameSettings;
        var options = new ScoreNameOptions(source.Scope.Region == "TW" ? ScoreRegion.TW : ScoreRegion.KR,
            settings.Artists.Select(item => new ArtistDefinition(item.Id, item.Name, item.Aliases)).ToArray(),
            new Dictionary<string, string>(), settings.Noise);
        var display = ScoreNameRules.Classify([score.RawTitle], options)[0];
        var entry = new ScoreEntry(score.Id, score.RawTitle, display.Cleaned,
            settings.Assignments.GetValueOrDefault(score.Id), display.Tags,
            [new(source.Scope.Provider, source.Scope.Region, source.Scope.RealmId + ":" + source.Scope.CharacterId,
                score.StableSourceId, null, source.Scope.Kind is ScoreSourceKind.ImportedMml or ScoreSourceKind.ImportedLibrary
                    ? "none" : score.StableSourceId is null ? "ambiguous" : "stable-id", score.Ambiguous)],
            score.CandidateGroup, score.Availability.ToString().ToLowerInvariant(), new(source.Revision));
        var timeline = timelines.Resolve(owner, score.Id, selection.PartId) with { PartId = selection.PartId };
        var rawHash = timeline.SourceHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? timeline.SourceHash[7..] : timeline.SourceHash;
        var content = timeline.EvidenceKind is TimelineEvidenceKind.ImportedMmlEstimate or TimelineEvidenceKind.PlaybackObservation &&
            timeline.Quality is ObservationQuality.Estimated or ObservationQuality.Confirmed &&
            rawHash.Length == 64 && rawHash.All(char.IsAsciiHexDigit) ? rawHash.ToLowerInvariant() : null;
        var unavailable = score.Ambiguous ? "room.scoreAmbiguous" :
            score.Availability is ScoreAvailability.Missing or ScoreAvailability.Uncertain ? "room.scoreMissing" :
            !catalogs.IsCurrent(source) ? "room.scoreSourceStale" : null;
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { selection, score, source.Status, content, timeline, unavailable }, ContractJson.Options)));
        return new(entry, content, timeline, fingerprint, unavailable);
    }
}
