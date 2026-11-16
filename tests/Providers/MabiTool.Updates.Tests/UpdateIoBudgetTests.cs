using System.Net;
using System.Security.Cryptography;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Updates.Tests;

public sealed class UpdateIoBudgetTests
{
    [Fact]
    public async Task Concurrent_consumers_share_one_bucket_and_idle_credit_is_capped()
    {
        var clock = new Clock();
        var budget = new UpdateIoBudget(65_536, clock: clock, delay: clock.Delay);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => budget.ConsumeDownloadAsync(UpdateIoBudget.ChunkSize)));
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
        clock.Advance(TimeSpan.FromHours(1));
        var before = clock.Elapsed;
        await budget.ConsumeDownloadAsync(UpdateIoBudget.ChunkSize);
        Assert.Equal(before, clock.Elapsed);
        await budget.ConsumeDownloadAsync(UpdateIoBudget.ChunkSize);
        Assert.Equal(before + TimeSpan.FromSeconds(1), clock.Elapsed);
    }

    [Fact]
    public async Task Unlimited_budget_never_waits_but_still_honours_cancellation()
    {
        var clock = new Clock();
        var budget = new UpdateIoBudget(null, clock: clock, delay: clock.Delay);
        for (var index = 0; index < 64; index++) await budget.ConsumeDownloadAsync(UpdateIoBudget.ChunkSize);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.ConsumeDownloadAsync(1, cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateIoBudget(65_535));
    }

    [Fact]
    public async Task Cancellation_releases_limiter_gate_without_future_bandwidth_debt()
    {
        var clock = new Clock();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = true;
        var budget = new UpdateIoBudget(65_536, clock: clock, delay: async (duration, token) =>
        {
            if (delayed) { waiting.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            else await clock.Delay(duration, token);
        });
        await budget.ConsumeDownloadAsync(UpdateIoBudget.ChunkSize);
        using var cancellation = new CancellationTokenSource();
        var first = budget.ConsumeDownloadAsync(UpdateIoBudget.ChunkSize, cancellation.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        delayed = false;
        await budget.ConsumeDownloadAsync(UpdateIoBudget.ChunkSize);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Elapsed);
    }

    [Theory]
    [InlineData("game.resource")]
    [InlineData("room.ready")]
    public async Task Verification_pauses_for_critical_activity_and_resumes_after_release(string kind)
    {
        var barrier = new ActivityBarrier();
        using var game = barrier.EnterActivity("game", kind);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = new UpdateIoBudget(barrier: barrier, delay: async (_, token) => { waiting.TrySetResult(); await release.Task.WaitAsync(token); });
        var pause = budget.PauseVerificationAsync();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pause.IsCompleted);
        game.Dispose();
        release.TrySetResult();
        await pause.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Parallel_downloader_assets_use_aggregate_budget_and_preserve_maintenance_admission()
    {
        using var workspace = new UpdateTestWorkspace();
        var clock = new Clock();
        var barrier = new ActivityBarrier();
        var budget = new UpdateIoBudget(65_536, barrier, clock, clock.Delay);
        var bytes = new byte[2 * UpdateIoBudget.ChunkSize];
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        using var downloader = new ResumableAssetDownloader(new Handler(() =>
        {
            Assert.Null(barrier.TryEnterMaintenance("installer"));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }), budget: budget);
        var sources = new[] { new AssetDownloadSource("direct", new Uri("https://github.com/asset")) };
        await Task.WhenAll(downloader.DownloadAsync(new(sources, workspace.PathFor("one"), bytes.Length, hash)),
            downloader.DownloadAsync(new(sources, workspace.PathFor("two"), bytes.Length, hash)));
        Assert.Equal(TimeSpan.FromSeconds(3), clock.Elapsed);
        Assert.Empty(barrier.Snapshot().Activities);
        using var maintenance = barrier.TryEnterMaintenance("installer");
        Assert.NotNull(maintenance);
        await Assert.ThrowsAsync<ActivityBarrierException>(() => downloader.DownloadAsync(new(sources, workspace.PathFor("three"), bytes.Length, hash)));
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public TimeSpan Elapsed => TimeSpan.FromTicks(GetTimestamp());
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
        public async Task Delay(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Advance(duration);
            await Task.Yield();
        }
    }
}
