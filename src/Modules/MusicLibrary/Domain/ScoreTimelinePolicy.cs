using MabiTool.Contracts;

namespace MabiTool.Modules.MusicLibrary.Domain;

public static class ScoreTimelinePolicy
{
    public static ScoreTimeline Resolve(string scoreId, string? selectedPartId,
        IReadOnlyList<ScoreTimelineEvidence> evidence)
    {
        if (string.IsNullOrWhiteSpace(scoreId)) throw new ArgumentException("乐谱 ID 不能为空", nameof(scoreId));
        if (evidence.Count > 256) throw new ArgumentException("单曲时间线证据超过 256 项", nameof(evidence));
        var candidates = evidence.Where(x => x.ScoreId == scoreId && IsValid(x)).ToArray();
        var full = Select(candidates.Where(x =>
            (x.PartId is null || selectedPartId is not null && x.PartId == selectedPartId) && x.FullCycleMs is not null), x => x.FullCycleMs);
        var part = selectedPartId is null ? default : Select(
            candidates.Where(x => x.PartId == selectedPartId && x.PartCycleMs is not null), x => x.PartCycleMs);
        if (full is null && part is null)
            return new(scoreId, null, null, false, 0, "none", "", ObservationQuality.Unknown);

        var selected = full?.Best ?? part!.Value.Best;
        var conflict = full?.Conflict == true || part?.Conflict == true;
        return new(scoreId, full is { Conflict: false } ? full.Value.Value : null,
            part is { Conflict: false } ? part.Value.Value : null, false, conflict ? 0 : selected.TailMs,
            selected.CalculatorVersion, selected.SourceHash,
            conflict ? ObservationQuality.Conflict : selected.Quality, selected.EvidenceKind,
            selectedPartId, selected.RecordedAt);
    }

    public static PerformanceStopDecision EvaluateStop(string mode, ScoreTimeline? timeline,
        bool matchedActualStart, double elapsedMs, double legacyAdvanceMarginMs = 0)
    {
        if (!double.IsFinite(elapsedMs) || elapsedMs < 0 || !double.IsFinite(legacyAdvanceMarginMs) ||
            legacyAdvanceMarginMs < 0 || legacyAdvanceMarginMs > 30_000)
            throw new ArgumentOutOfRangeException(nameof(elapsedMs), "播放时长或提前切换边距无效");
        if (!matchedActualStart) return new(false, null, "performance.startEvidenceMissing");
        if (timeline is null || timeline.Quality != ObservationQuality.Confirmed ||
            timeline.EvidenceKind != TimelineEvidenceKind.PlaybackObservation || timeline.FullCycleMs is not > 0)
            return new(false, null, "music.timelineUnconfirmed");

        var margin = string.Equals(mode, "solo", StringComparison.OrdinalIgnoreCase)
            ? legacyAdvanceMarginMs : 0;
        var threshold = Math.Max(0, timeline.FullCycleMs.Value + timeline.TailMs - margin);
        return new(elapsedMs >= threshold, threshold, "performance.timelineThreshold");
    }

    private static bool IsValid(ScoreTimelineEvidence value) =>
        !string.IsNullOrWhiteSpace(value.SourceId) && !string.IsNullOrWhiteSpace(value.CalculatorVersion) &&
        !string.IsNullOrWhiteSpace(value.SourceHash) &&
        ValidDuration(value.FullCycleMs) && ValidDuration(value.PartCycleMs) &&
        double.IsFinite(value.TailMs) && value.TailMs is >= 0 and <= 600_000 &&
        Enum.IsDefined(value.EvidenceKind) && Enum.IsDefined(value.Quality);

    private static bool ValidDuration(double? value) => value is null || double.IsFinite(value.Value) && value is > 0 and <= 86_400_000;
    private static int EvidenceRank(TimelineEvidenceKind kind) => kind switch
    {
        TimelineEvidenceKind.PlaybackObservation => 3,
        TimelineEvidenceKind.ManualEstimate => 2,
        TimelineEvidenceKind.ImportedHistoryEstimate or TimelineEvidenceKind.ImportedMmlEstimate => 1,
        _ => 0
    };
    private static Selection? Select(IEnumerable<ScoreTimelineEvidence> source, Func<ScoreTimelineEvidence, double?> duration)
    {
        var candidates = source.ToArray();
        if (candidates.Length == 0) return null;
        var rank = candidates.Max(x => EvidenceRank(x.EvidenceKind));
        var strongest = candidates.Where(x => EvidenceRank(x.EvidenceKind) == rank)
            .OrderByDescending(x => x.RecordedAt).ToArray();
        var best = strongest[0];
        var conflict = strongest.Any(x => Different(duration(x), duration(best)));
        return new(duration(best), best, conflict);
    }
    private readonly record struct Selection(double? Value, ScoreTimelineEvidence Best, bool Conflict);
    private static bool Different(double? left, double? right) => (left is null) != (right is null) ||
        left is not null && right is not null && Math.Abs(left.Value - right.Value) > 1;
}
