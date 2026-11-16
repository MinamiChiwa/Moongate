using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class ColorWorkerResultsTests
{
    internal static ColorProfile Profile() => new("profile", 1, "offline", ColorStrategy.Precise,
        new[] { "cloth", "trim", "metal" }.Select((id, i) => new ColorRegion(id, "#202020", [],
            ColorMatchMode.ExactHex, 0, i + 1, true)).ToArray(), new(1));

    private static readonly ObservationSource Source = new("offline", "1", "TW", "synthetic");
    private static readonly DateTimeOffset At = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
    private static object Measurement(string color, double delta = 0) => new
    {
        colors = new[] { color, "#202020" }, deltas = new[] { delta, 0 }, observedAt = At,
        accepted = delta == 0, targetExact = delta == 0
    };
    private static WorkerFrame Frame(string kind, object payload) => new(1, "2", new string('b', 32), kind,
        new string('a', 32), "18446744073709551615", "7", JsonSerializer.SerializeToElement(payload));

    [Fact]
    public void HistoryAndCurrentStaySeparateAndKeepStableRegionIdentity()
    {
        var frame = Frame("result", new
        {
            region_count = 2, outcome = "stopped", stop_reason = "interrupted", verified = false,
            measurements = new { bestMeasured = Measurement("#202020"), lastMeasured = Measurement("#222222", 1.5), currentVerified = (object?)null }
        });
        var result = ColorWorkerResults.ReadResult(frame, Profile(), Source, new(ulong.MaxValue));
        Assert.Null(result.CurrentVerified);
        Assert.Equal("#202020", result.BestMeasured!.HexByRegion["cloth"]);
        Assert.Equal("#222222", result.LastMeasured!.HexByRegion["cloth"]);
        Assert.Equal(1.5, result.LastMeasured.DeltaByRegion["cloth"]);
        Assert.DoesNotContain("metal", result.BestMeasured.HexByRegion.Keys);
        Assert.Equal(At, result.LastMeasured.ObservedAt);
        Assert.Equal(Source, result.LastMeasured.Source);
        Assert.Equal(ulong.MaxValue, result.LastMeasured.RunGeneration.Value);
        var roundtrip = JsonSerializer.Deserialize<ColorRunResult>(JsonSerializer.Serialize(result, ContractJson.Options), ContractJson.Options)!;
        Assert.Equal(ulong.MaxValue, roundtrip.LastMeasured!.RunGeneration.Value);
    }

    [Fact]
    public void PredictionAndBareVerifiedFlagNeverBecomeCurrentMeasurement()
    {
        var result = ColorWorkerResults.ReadResult(Frame("result", new
        {
            region_count = 2, outcome = "matched", verified = true, actual_colors = new[] { "#202020", "#202020" },
            candidates = new[] { new { predicted = true, colors = new[] { "#202020", "#202020" } } }
        }), Profile(), Source, new(1));
        Assert.Null(result.CurrentVerified);
        Assert.Null(result.BestMeasured);
        Assert.Equal("unverified", result.Outcome);
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(-1, false)]
    public void CandidateOrderIsOriginalAndExpiryDisablesSelection(double remaining, bool available)
    {
        var rows = new[] { ("second-looking", "#303030", 8d), ("first-looking", "#202020", 0d) }
            .Select(row => new { id = row.Item1, colors = new[] { row.Item2, "#202020" }, deltas = new[] { row.Item3, 0 },
                predicted = true, available = true, current = false, accepted = false, target_exact = false });
        var frame = Frame("event", new { kind = "native_candidate_ready", at_monotonic = 1000,
            data = new { region_count = 2, batch_id = "batch-1", effective_deadline = 1000 + remaining,
                candidates = rows, current_candidate_id = (string?)null,
                interaction = new { revision = "18446744073709551615", batchId = "batch-1", selectionEnabled = true, retryEnabled = true } } });
        var result = ColorWorkerResults.ReadCandidates(frame, Profile(), At);
        Assert.Equal(new[] { "second-looking", "first-looking" }, result.Candidates.Select(row => row.Id));
        Assert.Equal(new[] { 0, 1 }, result.Candidates.Select(row => row.SortIndex));
        Assert.All(result.Candidates, row => Assert.Equal(available, row.Available));
        Assert.Equal(available, result.Interaction.SelectionEnabled);
        Assert.Equal(At.AddSeconds(Math.Max(0, remaining)), result.ExpiresAt);
        Assert.Equal("#303030", result.Candidates[0].HexByRegion["cloth"]);
    }

    [Fact]
    public void RegionStatesAndCandidateSummaryKeepWorkerJudgement()
    {
        var measured = new { colors = new[] { "#202020", "#222222" }, deltas = new[] { 0, 1.5 }, observedAt = At,
            accepted = false, targetExact = false, regionStates = new[] { "met", "unmet" } };
        var result = ColorWorkerResults.ReadResult(Frame("result", new { region_count = 2,
            measurements = new { bestMeasured = measured, lastMeasured = Measurement("#202020"), currentVerified = (object?)null } }), Profile(), Source, new(1));
        Assert.Equal(new ColorRegionJudgement[] { new("cloth", ColorRegionState.Met), new("trim", ColorRegionState.Unmet) }, result.BestMeasured!.Regions);
        Assert.Empty(result.LastMeasured!.Regions);
        var row = new { id = "c1", colors = new[] { "#202020", "#303030" }, deltas = new[] { 0, 8.5 }, predicted = true, available = true,
            current = false, accepted = false, target_exact = false, maximum = 8.5, average = 4.25, exact_matches = 1, exact_total = 2,
            family_consistent = false, region_states = new[] { "met", "unmet" } };
        var batch = ColorWorkerResults.ReadCandidates(Frame("event", new { kind = "native_candidate_ready", at_monotonic = 1000,
            data = new { region_count = 2, batch_id = "b", effective_deadline = 1010, candidates = new[] { row },
                interaction = new { revision = "1", batchId = "b", selectionEnabled = true, retryEnabled = false } } }), Profile(), At);
        var candidate = Assert.Single(batch.Candidates);
        Assert.Equal((8.5, 4.25, 1, 2, false), (candidate.MaximumDelta, candidate.AverageDelta, candidate.ExactMatches, candidate.ExactTotal, candidate.FamilyConsistent));
        Assert.Equal(ColorRegionState.Unmet, candidate.Regions[1].State.KnownValue);
        var unknown = new { measured.colors, measured.deltas, observedAt = At, accepted = false, targetExact = false, regionStates = new[] { "met", "later" } };
        Assert.Equal("worker.resultInvalid", Assert.Throws<WorkerException>(() => ColorWorkerResults.ReadResult(Frame("result", new { region_count = 2,
            measurements = new { bestMeasured = unknown, lastMeasured = (object?)null, currentVerified = (object?)null } }), Profile(), Source, new(1))).Code);
    }

    [Fact]
    public void InvalidRegionCountAndColorEvidenceAreRejected()
    {
        foreach (var count in new[] { 1, 3 })
        {
            var frame = Frame("result", new { region_count = count,
                measurements = new { bestMeasured = Measurement("bad"), lastMeasured = (object?)null, currentVerified = (object?)null } });
            Assert.Equal("worker.resultInvalid", Assert.Throws<WorkerException>(() => ColorWorkerResults.ReadResult(frame, Profile(), Source, new(1))).Code);
        }
    }
}
