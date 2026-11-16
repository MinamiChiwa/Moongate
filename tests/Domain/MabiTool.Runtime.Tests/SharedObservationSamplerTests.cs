using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class SharedObservationSamplerTests
{
    [Fact]
    public async Task SubscribersShareOneUpstreamSampleAndLastSubscriberStopsIt()
    {
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var observation = Create("value", "provider", 1, 1, 30_000, "epoch-a");
        await using var sampler = new SharedObservationSampler<string>("game-a:performance", ct =>
        {
            Interlocked.Increment(ref calls);
            ct.Register(() => upstreamCancelled.TrySetResult());
            sampled.TrySetResult();
            return Task.FromResult(observation);
        }, TimeSpan.FromHours(1));
        using var cancellation = new CancellationTokenSource();
        var first = sampler.SubscribeAsync(TimeSpan.FromHours(1), cancellation.Token).GetAsyncEnumerator();
        var second = sampler.SubscribeAsync(TimeSpan.FromHours(1), cancellation.Token).GetAsyncEnumerator();

        var firstRead = first.MoveNextAsync().AsTask();
        var secondRead = second.MoveNextAsync().AsTask();
        await sampled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await firstRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await secondRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, calls);
        Assert.Equal(1, sampler.SampleCount);
        Assert.NotNull(sampler.LastSampleAt);

        cancellation.Cancel();
        await first.DisposeAsync();
        await second.DisposeAsync();
        await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SnapshotKeepsSourceGenerationsAndMarksConflictAndExpiredData()
    {
        var first = Create("alpha", "provider-a", 7, 1, 1_000, "epoch-a");
        var second = Create("beta", "provider-b", 7, 2, 1_000, "epoch-a");
        var conflict = SharedObservationSampler<string>.Snapshot([first, second], "epoch-a", 100);
        Assert.Equal(2, conflict.Sources.Count);
        Assert.Equal(ObservationQuality.Conflict, conflict.Quality);
        Assert.False(conflict.IsFresh);
        Assert.Equal(2, conflict.ConflictingProviders.Count);

        var expired = SharedObservationSampler<string>.Snapshot([first], "epoch-b", 100);
        Assert.Equal(ObservationQuality.Stale, expired.Quality);
        Assert.False(expired.IsFresh);
    }

    private static Observation<string> Create(string value, string provider, ulong generation, ulong sequence,
        double validForMs, string epoch) => new(value,
        new(provider, "1", "TW", "build-a"), DateTimeOffset.UtcNow, epoch, 0, validForMs,
        new(generation), new(sequence), ObservationQuality.Confirmed);
}
