using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.Modules.MusicLibrary.Domain;

namespace MabiTool.Modules.MusicLibrary.Persistence;

public sealed class ScoreTimelineStore(SqliteStore database, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public static DatabaseMigration Migration { get; } = new("music-library", 4, """
        CREATE TABLE music_score_timeline_evidence (
            owner TEXT NOT NULL, id INTEGER PRIMARY KEY AUTOINCREMENT, score_id TEXT NOT NULL,
            part_id TEXT NULL, recorded_at INTEGER NOT NULL, evidence_kind TEXT NOT NULL, snapshot TEXT NOT NULL
        ) STRICT;
        CREATE INDEX music_score_timeline_lookup
            ON music_score_timeline_evidence(owner,score_id,part_id,recorded_at DESC,id DESC);
        """);

    public ScoreTimeline Resolve(string owner, string scoreId, string? selectedPartId)
    {
        ValidateScope(owner, scoreId, selectedPartId);
        var json = database.ReadScalar("""
            SELECT json_group_array(snapshot) FROM (
                SELECT snapshot FROM music_score_timeline_evidence
                WHERE owner=$owner AND score_id=$score
                  AND (part_id IS NULL OR part_id=$part)
                ORDER BY recorded_at DESC, id DESC LIMIT 256
            )
            """, ("$owner", owner), ("$score", scoreId), ("$part", selectedPartId)) as string ?? "[]";
        using var document = JsonDocument.Parse(json);
        var evidence = document.RootElement.EnumerateArray()
            .Select(x => JsonSerializer.Deserialize<ScoreTimelineEvidence>(x.GetString()!, ContractJson.Options)!)
            .ToArray();
        return ScoreTimelinePolicy.Resolve(scoreId, selectedPartId, evidence);
    }

    public Task RecordAsync(string owner, ScoreTimelineEvidence evidence, CancellationToken cancellationToken = default)
    {
        ValidateScope(owner, evidence.ScoreId, evidence.PartId);
        if (string.IsNullOrWhiteSpace(evidence.SourceId) || evidence.SourceId.Length > 128 ||
            string.IsNullOrWhiteSpace(evidence.CalculatorVersion) || evidence.CalculatorVersion.Length > 64 ||
            string.IsNullOrWhiteSpace(evidence.SourceHash) || evidence.SourceHash.Length > 128 ||
            !Enum.IsDefined(evidence.EvidenceKind) || !Enum.IsDefined(evidence.Quality) ||
            evidence.RecordedAt.Offset != TimeSpan.Zero || evidence.RecordedAt < DateTimeOffset.UnixEpoch ||
            evidence.RecordedAt > clock.GetUtcNow().AddDays(1) ||
            !ValidDuration(evidence.FullCycleMs) || !ValidDuration(evidence.PartCycleMs) ||
            !double.IsFinite(evidence.TailMs) || evidence.TailMs is < 0 or > 600_000)
            throw new OperationException("request.invalid", "曲长证据格式无效");

        var snapshot = JsonSerializer.Serialize(evidence, ContractJson.Options);
        return database.WriteAsync(tx => tx.Execute("""
            INSERT INTO music_score_timeline_evidence(owner,score_id,part_id,recorded_at,evidence_kind,snapshot)
            VALUES($owner,$score,$part,$recorded,$kind,$snapshot)
            """, ("$owner", owner), ("$score", evidence.ScoreId), ("$part", evidence.PartId),
            ("$recorded", evidence.RecordedAt.ToUnixTimeMilliseconds()),
            ("$kind", evidence.EvidenceKind.ToString()), ("$snapshot", snapshot)), cancellationToken);
    }

    private static bool ValidDuration(double? value) => value is null ||
        double.IsFinite(value.Value) && value is > 0 and <= 86_400_000;

    private static void ValidateScope(string owner, string scoreId, string? partId)
    {
        if (!Regex.IsMatch(owner ?? "", @"\A[A-Za-z0-9._:-]{1,128}\z") ||
            !Regex.IsMatch(scoreId ?? "", @"\A[A-Za-z0-9._:-]{1,128}\z") ||
            partId is { Length: > 128 } || partId is not null && partId.Length == 0)
            throw new OperationException("request.invalid", "曲长查询范围无效");
    }
}
