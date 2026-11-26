using MabiTool.Contracts;
using MabiTool.Modules.Life.Domain;
using Xunit;

namespace MabiTool.Life.Tests;

public sealed class FishingMachineTests
{
    private static readonly double[] RoundWithRebound = [.9, .6, .25, .2, .4];
    private static readonly double[] RoundDepleted = [.9, .6, .3, .1, 0];

    [Fact]
    public void FirstCastPreparesModeThenTapsDirectionAndSpace()
    {
        var driver = new Driver();
        var cast = driver.FirstCast();
        Assert.Equal([FishingActionKind.PrepareMode, FishingActionKind.Cast], driver.Actions.Select(a => a.Kind));
        Assert.Equal(new VisionPoint(1120, 801), driver.Actions[0].Point);
        Assert.Equal(FishingKey.W, cast.Move);
        Assert.True(driver.Machine.Progress.ClockStarted);
        Assert.Equal("waiting-bite", driver.Machine.Progress.Phase);
    }

    [Fact]
    public void FocusedDialogIsConfirmedAndLeisureDialogCancelled()
    {
        var driver = new Driver();
        driver.Scene(.3);
        driver.Frame(.1, dialog: "focused"); driver.Frame(.1, dialog: "focused");
        Assert.Equal(FishingActionKind.ConfirmMode, driver.Actions.Last().Kind);
        Assert.Equal(new VisionPoint(752, 864), driver.Actions.Last().Point);
        driver.Scene(1.5);
        Assert.Contains(driver.Actions, a => a.Kind == FishingActionKind.Cast);

        var other = new Driver();
        other.Scene(.3);
        other.Frame(.1, dialog: "leisure"); other.Frame(.1, dialog: "leisure"); other.Frame(.1, dialog: "leisure");
        Assert.Single(other.Actions, a => a.Kind == FishingActionKind.CancelMode);
        Assert.Equal(new VisionPoint(530, 864), other.Actions.Single(a => a.Kind == FishingActionKind.CancelMode).Point);
    }

    [Fact]
    public void NoResponseForTwentySecondsRetriesWithOppositeDirection()
    {
        var driver = new Driver();
        var first = driver.FirstCast();
        var casts = driver.Actions.Count;
        for (var i = 0; i < 39; i++) driver.Frame(.5, scene: true);
        Assert.Equal(casts, driver.Actions.Count);
        Assert.Equal(0, driver.Machine.Progress.Rounds.NoResponseRetries);
        driver.Frame(.5, scene: true);
        Assert.Equal(1, driver.Machine.Progress.Rounds.NoResponseRetries);
        Assert.Equal("cast_retry_no_response", driver.Machine.Progress.Reason);
        var retry = driver.Actions.Last();
        Assert.Equal(FishingActionKind.Cast, retry.Kind);
        Assert.Equal(FishingKey.S, retry.Move);
        Assert.NotEqual(first.Move, retry.Move);
    }

    [Fact]
    public void HookOnTheTwentiethSecondFrameTakesPriorityOverRetry()
    {
        var driver = new Driver();
        driver.FirstCast();
        for (var i = 0; i < 39; i++) driver.Frame(.5, scene: true);
        driver.Frame(.5, hook: true, gauge: .9);
        driver.Frame(.1, hook: true, gauge: .9);
        Assert.Equal(0, driver.Machine.Progress.Rounds.NoResponseRetries);
        Assert.Equal(1, driver.Machine.Progress.Rounds.Submitted);
        Assert.Equal("fishing", driver.Machine.Progress.Phase);
    }

    [Fact]
    public void ReboundAfterLowPointReelsOnceThenRecastsInPlaceInsideTheWindow()
    {
        var driver = new Driver();
        driver.FirstCast();
        driver.Round(RoundWithRebound);
        Assert.Single(driver.Actions, a => a.Kind == FishingActionKind.Reel);
        driver.Round([.8, .85, .9]);
        Assert.Single(driver.Actions, a => a.Kind == FishingActionKind.Reel);
        Assert.Equal(1, driver.Machine.Progress.Rounds.Reeled);
        // 信号消失后 HUD 隐藏三秒（收竿动画），恢复后仍在两秒续抛窗口内：只按空格，不轻移。
        driver.Vanish(3.0, 1.0);
        var recast = driver.Actions.Last();
        Assert.Equal(FishingActionKind.Cast, recast.Kind);
        Assert.Equal(FishingKey.None, recast.Move);
        Assert.Equal(0, driver.Machine.Progress.Rounds.DepletedWithoutRebound);
    }

    [Fact]
    public void GaugeDepletingWithoutReboundEndsTheRoundAndCountsIt()
    {
        var driver = new Driver();
        driver.FirstCast();
        driver.Round(RoundDepleted);
        Assert.DoesNotContain(driver.Actions, a => a.Kind == FishingActionKind.Reel);
        driver.Vanish(3.0, 1.0);
        Assert.Equal(1, driver.Machine.Progress.Rounds.DepletedWithoutRebound);
        Assert.Equal(0, driver.Machine.Progress.Rounds.Reeled);
        Assert.Equal(FishingKey.None, driver.Actions.Last().Move);
    }

    [Fact]
    public void RecastAfterTheWindowMovesInTheOtherDirection()
    {
        var driver = new Driver();
        var first = driver.FirstCast();
        driver.Round(RoundDepleted);
        driver.Vanish(7.0, 1.0);
        var recast = driver.Actions.Last();
        Assert.Equal(FishingActionKind.Cast, recast.Kind);
        Assert.NotEqual(FishingKey.None, recast.Move);
        Assert.NotEqual(first.Move, recast.Move);
    }

    [Fact]
    public void MountedCharacterDismountsOnceThenFailsAfterFifteenSeconds()
    {
        var driver = new Driver();
        for (var i = 0; i < 6; i++) driver.Frame(.5, dismount: true);
        Assert.Equal([FishingActionKind.Dismount], driver.Actions.Select(a => a.Kind));
        Assert.Equal(new VisionPoint(1120, 800), driver.Actions[0].Point);
        for (var i = 0; i < 40; i++) driver.Frame(.5, dismount: true);
        Assert.Equal(FishingEnd.Failed, driver.Terminal?.End);
        Assert.Equal(LifeFishingErrors.DismountNotObserved, driver.Terminal?.ProblemCode);
    }

    [Fact]
    public void NothingIsCastRightAfterTheMountIconDisappears()
    {
        var driver = new Driver();
        driver.Scene(.6);
        driver.Frame(.1, dismount: true); driver.Frame(.1, dismount: true);
        var before = driver.Actions.Count;
        driver.Frame(.1, scene: true); driver.Frame(.1, scene: true); driver.Frame(.1, scene: true);
        Assert.Equal(before, driver.Actions.Count);
        driver.Scene(1.2);
        Assert.Contains(driver.Actions.Skip(before), a => a.Kind == FishingActionKind.Cast);
    }

    [Fact]
    public void BackpackFullLeavesThePoseThenFailsWithEvidence()
    {
        var driver = new Driver();
        driver.FirstCast();
        driver.Round([.9, .8]);
        driver.Frame(.2, hook: true, gauge: .7, full: true);
        driver.Frame(.2, hook: true, gauge: .7, full: true);
        Assert.Equal(FishingActionKind.Leave, driver.Actions.Last().Kind);
        Assert.Null(driver.Terminal);
        driver.Scene(.5);
        Assert.Equal(FishingEnd.Failed, driver.Terminal?.End);
        Assert.Equal(LifeFishingErrors.BackpackFull, driver.Terminal?.ProblemCode);
        Assert.Equal(FishingPoseState.Observed, driver.Terminal?.PoseLeft.KnownValue);
    }

    [Fact]
    public void DurationEndLeavesAndRecordsObservedPose()
    {
        var driver = new Driver(60);
        driver.FirstCast();
        for (var i = 0; i < 70 && driver.Actions.Last().Kind != FishingActionKind.Leave; i++) driver.Frame(1, hook: true, gauge: .9);
        Assert.Equal(FishingActionKind.Leave, driver.Actions.Last().Kind);
        Assert.True(driver.Machine.Progress.ElapsedSeconds >= 60);
        driver.Scene(.5);
        Assert.Equal(FishingEnd.Succeeded, driver.Terminal?.End);
        Assert.Equal("duration-reached", driver.Terminal?.StopReason);
        Assert.Equal(FishingPoseState.Observed, driver.Terminal?.PoseLeft.KnownValue);
    }

    [Fact]
    public void LeaveWithoutSceneEvidenceIsUnconfirmedAfterFiveSeconds()
    {
        var driver = new Driver();
        driver.FirstCast();
        driver.Round([.9, .8]);
        driver.Machine.RequestStop();
        driver.Frame(.2, hook: true, gauge: .8);
        Assert.Equal(FishingActionKind.Leave, driver.Actions.Last().Kind);
        for (var i = 0; i < 30 && driver.Terminal is null; i++) driver.Frame(.5, hook: true, gauge: .8);
        Assert.Equal(FishingEnd.Succeeded, driver.Terminal?.End);
        Assert.Equal("user-stop", driver.Terminal?.StopReason);
        Assert.Equal(FishingPoseState.Unconfirmed, driver.Terminal?.PoseLeft.KnownValue);
    }

    [Fact]
    public void StopWhileIdleFinishesWithoutAnyInput()
    {
        var driver = new Driver();
        driver.Machine.RequestStop();
        driver.Scene(.5);
        Assert.Empty(driver.Actions);
        Assert.Equal(FishingEnd.Succeeded, driver.Terminal?.End);
        Assert.Equal(FishingPoseState.NotNeeded, driver.Terminal?.PoseLeft.KnownValue);
    }

    [Fact]
    public void ThreeRejectedCastsFailWithPreflightRetryLimit()
    {
        var driver = new Driver { RejectWhen = action => action.Kind == FishingActionKind.Cast };
        var reasons = new List<string?>();
        for (var i = 0; i < 12 && driver.Terminal is null; i++)
        {
            driver.Frame(.5, scene: true);
            reasons.Add(driver.Machine.Progress.Reason);
        }
        Assert.Equal(FishingEnd.Failed, driver.Terminal?.End);
        Assert.Equal(LifeFishingErrors.CastPreflightRetryLimit, driver.Terminal?.ProblemCode);
        Assert.Contains("target_changed_before_input", reasons);
        Assert.Contains("cast_preflight_retry_limit", reasons);
        Assert.Equal(0, driver.Machine.Progress.Rounds.Submitted);
    }

    [Fact]
    public void FiveRoundsWithoutAHookFailTheSelfCheck()
    {
        var driver = new Driver();
        driver.FirstCast();
        for (var round = 0; round < 5 && driver.Terminal is null; round++)
        {
            driver.Round(RoundDepleted, hook: false);
            driver.Vanish(3.0, 6.0);
            if (driver.Terminal is null && round < 4) driver.Scene(1.0);
        }
        Assert.Equal(FishingEnd.Failed, driver.Terminal?.End);
        Assert.Equal(LifeFishingErrors.HookNotRecognized, driver.Terminal?.ProblemCode);
        Assert.Equal(5, driver.Machine.Progress.ConsecutiveRoundsWithoutHook);
    }

    [Fact]
    public void ARoundWithAHookResetsTheSelfCheckCounter()
    {
        var driver = new Driver();
        driver.FirstCast();
        driver.Round(RoundDepleted, hook: false);
        driver.Vanish(3.0, 3.0);
        Assert.Equal(1, driver.Machine.Progress.ConsecutiveRoundsWithoutHook);
        driver.Round(RoundDepleted, hook: true);
        driver.Vanish(3.0, 3.0);
        Assert.Equal(0, driver.Machine.Progress.ConsecutiveRoundsWithoutHook);
    }

    [Fact]
    public void UnrecognizedInterfaceFailsAfterFortyFiveSeconds()
    {
        var driver = new Driver();
        driver.Scene(.3);
        for (var i = 0; i < 88; i++) driver.Frame(.5);
        Assert.Null(driver.Terminal);
        for (var i = 0; i < 4; i++) driver.Frame(.5);
        Assert.Equal(LifeFishingErrors.InterfaceUnrecognized, driver.Terminal?.ProblemCode);
    }

    [Fact]
    public void ElapsedTimeStaysUnknownUntilTheFirstCast()
    {
        var driver = new Driver(300);
        driver.Scene(.3);
        Assert.False(driver.Machine.Progress.ClockStarted);
        Assert.Null(driver.Machine.Progress.ElapsedSeconds);
    }
}
