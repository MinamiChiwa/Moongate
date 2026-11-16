using System.Text.Json;
using System.Runtime.Versioning;
using MabiTool.Infrastructure.Updates;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class UpdateHandoffProgressTests
{
    [Fact]
    public async Task OuterCancellationKeepsStageBeforeLateExchangeCleanup()
    {
        var clock = new Clock();
        var progress = new UpdateHandoffProgress(clock);
        using var deadline = new CancellationTokenSource(UpdateHandoffProtocol.Timeout, clock);
        using var registration = deadline.Token.Register(() => progress.Freeze());
        progress.SetStage("recordHealthy.previousTree");
        var exchange = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = exchange.Task.WaitAsync(deadline.Token);
        clock.Advance(61_000);
        Assert.False(waiting.IsCompleted);
        clock.Advance(59_000);
        Assert.True(deadline.IsCancellationRequested);
        var outer = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        clock.Advance(5_000);
        progress.SetStage("candidateExit");
        var late = new IOException();
        late.Data["handoffStage"] = "candidateExit";
        late.Data["handoffMessage"] = "receive:healthResult";
        exchange.SetException(late);
        await Assert.ThrowsAsync<IOException>(() => exchange.Task);
        UpdateHandoffProgress.MergeFailureDetails(outer, late);
        progress.Freeze().Apply(outer);
        Assert.Equal("recordHealthy.previousTree", outer.Data["handoffStage"]);
        Assert.Equal(120_000L, outer.Data["handoffElapsedMs"]);
        Assert.Equal("receive:healthResult", outer.Data["handoffMessage"]);
    }

    [Fact]
    public void DurationsUseMonotonicClockAndFreezeIsImmutable()
    {
        var clock = new Clock();
        var progress = new UpdateHandoffProgress(clock);
        clock.Advance(7);
        progress.SetStage("connect");
        clock.Advance(13);
        progress.SetStage("commit.persist");
        clock.Advance(11);
        progress.SetStage("candidateExit");
        clock.Advance(3);
        var snapshot = progress.Freeze();
        Assert.Equal(34, snapshot.ElapsedMs);
        Assert.Equal(new long[] { 7, 13, 11, 3 }, snapshot.Timings.Select(item => item.ElapsedMs));
        Assert.Equal(new[] { "candidateStart", "connect", "commit.persist", "candidateExit" }, snapshot.Timings.Select(item => item.Stage));
        clock.Advance(100);
        progress.SetStage("exchange");
        Assert.Same(snapshot, progress.Freeze());
        Assert.Throws<NotSupportedException>(() => ((IList<UpdateHandoffTiming>)snapshot.Timings)[0] = new("connect", 0));
    }

    [Fact]
    public void TransitionHistoryIsBoundedAndRejectsUnlistedStages()
    {
        var clock = new Clock();
        var progress = new UpdateHandoffProgress(clock);
        for (var index = 0; index < 100; index++)
        {
            clock.Advance(1);
            progress.SetStage(index % 2 == 0 ? "connect" : "exchange");
        }
        Assert.Throws<ArgumentException>(() => progress.SetStage("private-path"));
        var snapshot = progress.Freeze();
        Assert.Equal(UpdateHandoffProgress.MaximumTimings, snapshot.Timings.Count);
        Assert.Equal(100, snapshot.ElapsedMs);
        Assert.Equal("exchange", snapshot.Stage);
    }

    [Fact]
    public async Task FailureLogReadsLegacyEntriesAndAddsFixedTimings()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var state = Path.Combine(workspace.InstallRoot, "update-state");
        Directory.CreateDirectory(state);
        var path = Path.Combine(state, UpdateFailureLog.FileName);
        await File.WriteAllTextAsync(path, """
            [{"code":"update.handoffTimeout","phase":"--update-candidate","peerCheck":null,"transactionCallback":null,"transactionElapsedMs":null,"peerOpenError":null,"exceptionType":"UpdatePlanException","hResult":0,"handoffStage":null,"handoffMessage":null}]
            """);
        var progress = new UpdateHandoffProgress();
        progress.SetStage("challenge.candidateTree");
        var error = new UpdatePlanException("update.handoffTimeout");
        progress.Freeze().Apply(error);
        error.Data["handoffMessage"] = "receive:healthResult";
        error.Data["secret"] = "private-message";
        await UpdateFailureLog.RecordAsync(state, error, "--update-candidate");
        var bytes = await File.ReadAllBytesAsync(path);
        var entries = JsonSerializer.Deserialize<UpdateFailureEntry[]>(bytes, UpdateHandoffProtocol.Json)!;
        Assert.Equal(2, entries.Length);
        Assert.Null(entries[0].HandoffTimings);
        Assert.Equal("challenge.candidateTree", entries[1].HandoffStage);
        Assert.Equal("receive:healthResult", entries[1].HandoffMessage);
        Assert.Contains(entries[1].HandoffTimings!, item => item.Stage == "challenge.candidateTree");
        Assert.DoesNotContain("private-message", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task FailureLogRejectsUnlistedStageMessageAndTimingsAndBoundsEntries()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var state = Path.Combine(workspace.InstallRoot, "update-state");
        Directory.CreateDirectory(state);
        for (var index = 0; index < 10; index++)
        {
            var error = new UpdatePlanException("update.handoffTimeout");
            error.Data["handoffStage"] = "private-path";
            error.Data["handoffMessage"] = "receive:private-payload";
            error.Data["handoffTimings"] = new[] { new UpdateHandoffTiming("private-path", 1) };
            await UpdateFailureLog.RecordAsync(state, error, "--update-candidate");
        }
        var bytes = await File.ReadAllBytesAsync(Path.Combine(state, UpdateFailureLog.FileName));
        var entries = JsonSerializer.Deserialize<UpdateFailureEntry[]>(bytes, UpdateHandoffProtocol.Json)!;
        Assert.Equal(UpdateFailureLog.MaximumEntries, entries.Length);
        Assert.True(bytes.Length <= UpdateFailureLog.MaximumBytes);
        Assert.All(entries, item => { Assert.Null(item.HandoffStage); Assert.Null(item.HandoffMessage); Assert.Null(item.HandoffTimings); });
    }

    [Fact]
    public async Task SuccessfulHandoffExportsCommitAndExitSeparately()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var state = Path.Combine(workspace.InstallRoot, "update-state");
        Directory.CreateDirectory(state);
        var clock = new Clock();
        var progress = new UpdateHandoffProgress(clock);
        progress.SetStage("commit.persist"); clock.Advance(10);
        progress.SetStage("committed"); clock.Advance(3);
        progress.SetStage("candidateExit"); clock.Advance(4);
        var snapshot = progress.Freeze();
        var transactionId = new string('a', 32);
        for (var index = 0; index < 10; index++)
            await UpdateFailureLog.RecordHandoffAsync(state, "--update-candidate", snapshot, transactionId);
        var path = Path.Combine(state, UpdateFailureLog.HandoffFileName);
        var bytes = await File.ReadAllBytesAsync(path);
        var entries = JsonSerializer.Deserialize<UpdateHandoffEntry[]>(bytes, UpdateHandoffProtocol.Json)!;
        Assert.Equal(UpdateFailureLog.MaximumEntries, entries.Length);
        Assert.True(bytes.Length <= UpdateFailureLog.MaximumBytes);
        var result = entries[^1].Progress;
        Assert.Equal(transactionId, entries[^1].TransactionId);
        Assert.Contains(result.Timings, item => item.Stage == "commit.persist" && item.ElapsedMs == 10);
        Assert.Contains(result.Timings, item => item.Stage == "candidateExit" && item.ElapsedMs == 4);
        Assert.Equal(17, result.ElapsedMs);
    }

    [Fact]
    public async Task PackageEvidenceExportsExactRecordsAndReportsMissingOlderDiagnostics()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var state = Path.Combine(workspace.InstallRoot, "update-state");
        var evidence = Path.Combine(workspace.Root, "evidence");
        Directory.CreateDirectory(state);
        Assert.Null(await PackagedReleaseInstallTests.ExportHandoffDiagnosticsAsync(state, evidence, "0.4.5"));
        var progress = new UpdateHandoffProgress();
        progress.SetStage("candidateExit");
        await UpdateFailureLog.RecordHandoffAsync(state, "--update-candidate", progress.Freeze());
        var original = await File.ReadAllBytesAsync(Path.Combine(state, UpdateFailureLog.HandoffFileName));
        var name = await PackagedReleaseInstallTests.ExportHandoffDiagnosticsAsync(state, evidence, "0.4.5");
        Assert.Equal("0.4.5.install-handoffs.json", name);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(evidence, name!)));
    }

    [Fact]
    public async Task PackageEvidenceRejectsOversizedDiagnosticsBeforeExport()
    {
        using var workspace = new InstallTransactionTests.Workspace();
        var state = Path.Combine(workspace.InstallRoot, "update-state");
        var evidence = Path.Combine(workspace.Root, "evidence");
        Directory.CreateDirectory(state);
        await File.WriteAllBytesAsync(Path.Combine(state, UpdateFailureLog.HandoffFileName), new byte[UpdateFailureLog.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => PackagedReleaseInstallTests.ExportHandoffDiagnosticsAsync(state, evidence, "0.4.5.rollback"));
        Assert.False(Directory.Exists(evidence));
    }

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        private long? dueAt;
        private Action? expire;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(UpdateHandoffProtocol.Timeout, dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            dueAt = timestamp + dueTime.Ticks;
            expire = () => callback(state);
            return new TimerHandle(() => { dueAt = null; expire = null; });
        }
        internal void Advance(long milliseconds)
        {
            timestamp += TimeSpan.FromMilliseconds(milliseconds).Ticks;
            if (dueAt is not null && timestamp >= dueAt)
            { dueAt = null; expire!(); }
        }
        private sealed class TimerHandle(Action dispose) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => dispose();
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
