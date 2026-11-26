using MabiTool.Contracts;
using MabiTool.Modules.Life.Application;
using MabiTool.Modules.Life.Domain;
using Xunit;

namespace MabiTool.Life.Tests;

public sealed class FishingRunnerTests
{
    /// <summary>毫秒计数的手动时钟；识别假源每取一帧推进一个观察周期。</summary>
    private sealed class ManualTime : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    private sealed class ScriptedVision(ManualTime time, Func<int, FishingObservation?> script, int periodMilliseconds = 500) : IFishingVision
    {
        private int index;
        public int Frames => index;
        public ValueTask<FishingObservation> NextAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            time.Milliseconds += periodMilliseconds;
            var observation = script(index++) ?? throw new FishingVisionException("capture.sourceFailed");
            return ValueTask.FromResult(observation);
        }
    }

    private sealed class FakeInput(Func<FishingAction, CancellationToken, Task>? perform = null, Action<int>? renew = null) : IFishingInput
    {
        private int renewals;
        public List<FishingAction> Performed { get; } = [];
        public int Renewals => renewals;
        public Task PerformAsync(FishingAction action, CancellationToken cancellationToken)
        {
            Performed.Add(action);
            return perform?.Invoke(action, cancellationToken) ?? Task.CompletedTask;
        }
        public void RenewLease() => renew?.Invoke(++renewals);
    }

    private static FishingObservation Scene(int index) => Driver.Observation((ulong)index + 1, scene: true);
    private static FishingObservation Round(int index, double gauge = .8) => Driver.Observation((ulong)index + 1, hook: true, gauge: gauge);

    private static FishingRunner Runner(ManualTime time, ScriptedVision vision, FakeInput input, int? duration = null,
        List<FishingProgress>? published = null) =>
        new(new(duration), vision, input, time, progress => published?.Add(progress));

    [Fact]
    public async Task CancelDuringTheCastStopsWithoutFurtherInput()
    {
        var time = new ManualTime();
        var started = new TaskCompletionSource();
        var input = new FakeInput((action, token) =>
        {
            if (action.Kind != FishingActionKind.Cast) return Task.CompletedTask;
            started.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        });
        var runner = Runner(time, new ScriptedVision(time, Scene), input);
        using var cancel = new CancellationTokenSource();
        var run = runner.RunAsync(cancel.Token);
        await started.Task;
        cancel.Cancel();
        var outcome = await run;
        Assert.Equal(FishingEnd.Cancelled, outcome.End);
        Assert.Equal(FishingPoseState.Unconfirmed, outcome.PoseLeft.KnownValue);
        Assert.Equal([FishingActionKind.PrepareMode, FishingActionKind.Cast], input.Performed.Select(a => a.Kind));
        Assert.True(outcome.InputDispatched);
    }

    [Fact]
    public async Task CancelBeforeTheFirstFrameSendsNothing()
    {
        var time = new ManualTime();
        var input = new FakeInput();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var outcome = await Runner(time, new ScriptedVision(time, Scene), input).RunAsync(cancel.Token);
        Assert.Equal(FishingEnd.Cancelled, outcome.End);
        Assert.Empty(input.Performed);
        Assert.False(outcome.InputDispatched);
    }

    [Fact]
    public async Task VisionFailureInterruptsTheRunAndSendsNoMoreInput()
    {
        var time = new ManualTime();
        var input = new FakeInput();
        var vision = new ScriptedVision(time, index => index < 6 ? Scene(index) : null);
        var outcome = await Runner(time, vision, input).RunAsync(CancellationToken.None);
        Assert.Equal(FishingEnd.Interrupted, outcome.End);
        Assert.Equal("capture.sourceFailed", outcome.ProblemCode);
        Assert.Equal(7, vision.Frames);
        Assert.All(input.Performed, action => Assert.True(action.Kind is FishingActionKind.PrepareMode or FishingActionKind.Cast));
    }

    [Theory]
    [InlineData(FishingInputFailure.Fatal, "input.bindingChanged", false)]
    [InlineData(FishingInputFailure.Unknown, "input.deviceFailure", true)]
    public async Task FatalOrUnknownInputFailureEndsTheRunWithoutReplay(FishingInputFailure failure, string code, bool unknown)
    {
        var time = new ManualTime();
        var input = new FakeInput((action, _) => action.Kind == FishingActionKind.Cast
            ? Task.FromException(new FishingInputException(code, failure)) : Task.CompletedTask);
        var outcome = await Runner(time, new ScriptedVision(time, Scene), input).RunAsync(CancellationToken.None);
        Assert.Equal(FishingEnd.Interrupted, outcome.End);
        Assert.Equal(code, outcome.ProblemCode);
        Assert.Equal(unknown, outcome.EffectUnknown);
        Assert.Single(input.Performed, a => a.Kind == FishingActionKind.Cast);
    }

    [Fact]
    public async Task RejectedCastsAreNotCountedAsRoundsAndStopAfterThree()
    {
        var time = new ManualTime();
        var input = new FakeInput((action, _) => action.Kind == FishingActionKind.Cast
            ? Task.FromException(new FishingInputException("input.backgroundRejected", FishingInputFailure.Rejected)) : Task.CompletedTask);
        var outcome = await Runner(time, new ScriptedVision(time, Scene), input).RunAsync(CancellationToken.None);
        Assert.Equal(FishingEnd.Failed, outcome.End);
        Assert.Equal(LifeFishingErrors.CastPreflightRetryLimit, outcome.ProblemCode);
        Assert.Equal(3, input.Performed.Count(a => a.Kind == FishingActionKind.Cast));
        Assert.Equal(0, outcome.Final.Rounds.Submitted);
    }

    [Fact]
    public async Task GracefulStopLeavesThePoseAndRecordsTheEvidence()
    {
        var time = new ManualTime();
        var left = false;
        FishingRunner? runner = null;
        var input = new FakeInput((action, _) =>
        {
            if (action.Kind == FishingActionKind.Leave) left = true;
            return Task.CompletedTask;
        });
        var vision = new ScriptedVision(time, index =>
        {
            if (index == 40) runner!.RequestStop();
            return left || index < 4 ? Scene(index) : Round(index);
        });
        runner = Runner(time, vision, input);
        var outcome = await runner.RunAsync(CancellationToken.None);
        Assert.Equal(FishingEnd.Succeeded, outcome.End);
        Assert.Equal("user-stop", outcome.StopReason);
        Assert.Equal(FishingPoseState.Observed, outcome.PoseLeft.KnownValue);
        Assert.Single(input.Performed, a => a.Kind == FishingActionKind.Leave);
    }

    [Fact]
    public async Task LeaseIsRenewedAndALostLeaseInterruptsTheRun()
    {
        var time = new ManualTime();
        var input = new FakeInput(renew: count =>
        {
            if (count == 2) throw new FishingInputException("input.leaseInvalid", FishingInputFailure.Fatal);
        });
        var outcome = await Runner(time, new ScriptedVision(time, index => index < 4 ? Scene(index) : Round(index), 1000), input)
            .RunAsync(CancellationToken.None);
        Assert.Equal(2, input.Renewals);
        Assert.Equal(FishingEnd.Interrupted, outcome.End);
        Assert.Equal("input.leaseInvalid", outcome.ProblemCode);
        Assert.InRange(time.Milliseconds, 120_000, 130_000);
    }

    [Fact]
    public async Task ProgressIsPublishedOnChangeNotOnEveryFrame()
    {
        var time = new ManualTime();
        var published = new List<FishingProgress>();
        var vision = new ScriptedVision(time, index => index < 4 ? Scene(index) : Round(index), 100);
        var runner = Runner(time, vision, new FakeInput(), duration: 30, published);
        FishingOutcome? outcome = null;
        var run = runner.RunAsync(CancellationToken.None);
        // 30 秒目标：结束时离开姿态，场景帧由终态之前的脚本无法提供，这里只关心发布频率。
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        if (finished == run) outcome = await run;
        Assert.NotNull(outcome);
        Assert.True(vision.Frames > 100);
        Assert.True(published.Count < vision.Frames / 5, $"published {published.Count} of {vision.Frames} frames");
        Assert.Equal("fishing", published.First(p => p.Phase == "fishing").Phase);
    }
}
