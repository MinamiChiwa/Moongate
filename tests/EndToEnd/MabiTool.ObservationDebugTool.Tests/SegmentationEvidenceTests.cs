using MabiTool.ObservationDebugTool;
using Xunit;

namespace MabiTool.ObservationDebugTool.Tests;

public sealed class SegmentationEvidenceTests
{
    private static readonly SegmentOutcome Manual = new("first-damage", "manual-split", true);
    private static readonly SegmentOutcome AfterManual = new("manual-split", "active", true);

    private static SegmentationEvidence Evidence() => new(new SegmentationOptions(RequiredQuietSeconds: 30));

    [Fact]
    public void QuietSpanInsideOneSegmentIsRecordedWithItsStart()
    {
        var evidence = Evidence();
        evidence.Observe(10_000, "e1", true);
        evidence.Observe(20_000, "e1", false);
        evidence.Observe(45_000, "e1", true);
        evidence.Observe(50_000, "e1", true);
        Assert.Equal(35_000, evidence.LongestQuietMs);
        Assert.Equal(10_000, evidence.LongestQuietFromMs);
        Assert.True(evidence.Evaluate([Manual, AfterManual])["quietHeldWithoutSplit"]);
    }

    [Fact]
    public void QuietSpanAcrossDifferentSegmentsIsNotCounted()
    {
        var evidence = Evidence();
        evidence.Observe(10_000, "e1", true);
        evidence.Observe(80_000, "e2", true);
        evidence.Observe(82_000, "e2", true);
        Assert.Equal(2_000, evidence.LongestQuietMs);
        Assert.False(evidence.Evaluate([Manual, AfterManual])["quietHeldWithoutSplit"]);
    }

    [Fact]
    public void ShortGapsDoNotSatisfyTheRequiredQuietSpan()
    {
        var evidence = Evidence();
        evidence.Observe(0, "e1", true);
        evidence.Observe(29_999, "e1", true);
        Assert.False(evidence.Evaluate([Manual, AfterManual])["quietHeldWithoutSplit"]);
        evidence.Observe(60_000, "e1", true);
        Assert.True(evidence.Evaluate([Manual, AfterManual])["quietHeldWithoutSplit"]);
    }

    [Fact]
    public void IdleTimeoutBoundaryFailsOnlyThatCheck()
    {
        var evidence = Evidence();
        evidence.Observe(0, "e1", true); evidence.Observe(40_000, "e1", true);
        evidence.RecordSplitAttempt(140_000, true);
        var checks = evidence.Evaluate([new("first-damage", "idle-timeout", true), Manual, AfterManual]);
        Assert.False(checks["noIdleTimeoutBoundary"]);
        Assert.True(checks["manualSplitApplied"] && checks["manualSplitClosedSegment"] && checks["manualSplitStartedSegment"]);
    }

    [Fact]
    public void ManualSplitChecksFailWithoutEffectOrWithoutBoundarySegments()
    {
        var evidence = Evidence();
        evidence.RecordSplitAttempt(140_000, false);
        evidence.RecordSplitAttempt(140_100, false);
        Assert.Equal(2, evidence.SplitAttempts);
        Assert.Null(evidence.SplitAppliedAtMs);
        var none = evidence.Evaluate([new("first-damage", "active", true)]);
        Assert.False(none["manualSplitApplied"]);
        Assert.False(none["manualSplitClosedSegment"]);
        Assert.False(none["manualSplitStartedSegment"]);

        evidence.RecordSplitAttempt(140_200, true);
        evidence.RecordSplitAttempt(140_300, true);
        Assert.Equal(140_200, evidence.SplitAppliedAtMs);
        var noDamage = evidence.Evaluate([Manual, new("manual-split", "active", false)]);
        Assert.True(noDamage["manualSplitApplied"] && noDamage["manualSplitClosedSegment"]);
        Assert.False(noDamage["manualSplitStartedSegment"]);
    }

    [Fact]
    public void AllChecksHoldForTheExpectedSession()
    {
        var evidence = Evidence();
        evidence.Observe(0, "e1", true); evidence.Observe(40_000, "e1", true);
        evidence.Observe(100_000, "e1", true);
        evidence.RecordSplitAttempt(140_000, true);
        evidence.Observe(141_000, "e2", true);
        var checks = evidence.Evaluate([Manual, AfterManual]);
        Assert.Equal(["quietHeldWithoutSplit", "noIdleTimeoutBoundary", "manualSplitApplied", "manualSplitClosedSegment", "manualSplitStartedSegment"], checks.Keys);
        Assert.All(checks, pair => Assert.True(pair.Value, pair.Key));
    }

    [Fact]
    public void PanelPhasesFollowTheSchedule()
    {
        var options = new SegmentationOptions();
        Assert.Equal("持续攻击", options.Phase(10, false, 170).Stage);
        Assert.Equal("停手", options.Phase(40, false, 140).Stage);
        Assert.Equal("恢复攻击", options.Phase(100, false, 80).Stage);
        Assert.Equal("等待拆段", options.Phase(140, false, 40).Stage);
        Assert.Equal("拆段后攻击", options.Phase(141, true, 39).Stage);
    }
}
