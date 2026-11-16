using System.Threading.Channels;
using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

public sealed record ObservationSnapshot<T>(IReadOnlyList<Observation<T>> Sources, ObservationQuality Quality,
    bool IsFresh, IReadOnlyList<string> ConflictingProviders) where T : notnull;

public sealed class SharedObservationSampler<T> : IAsyncDisposable where T : notnull
{
    private sealed class Subscriber(Channel<Observation<T>> channel, TimeSpan interval)
    {
        public Channel<Observation<T>> Channel { get; } = channel;
        public TimeSpan Interval { get; } = interval;
    }

    private readonly object gate = new();
    private readonly Func<CancellationToken, Task<Observation<T>>> sample;
    private readonly TimeProvider time;
    private readonly TimeSpan minimumInterval;
    private readonly Dictionary<Guid, Subscriber> subscribers = [];
    private CancellationTokenSource? runCancellation;
    private Task? runTask;
    private int sampleCount;
    private bool disposed;
    private Observation<T>? latest;
    private long lastSampleUtcTicks;

    public SharedObservationSampler(string sourceKey, Func<CancellationToken, Task<Observation<T>>> sample,
        TimeSpan minimumInterval, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(sample);
        if (minimumInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        SourceKey = sourceKey;
        this.sample = sample;
        this.minimumInterval = minimumInterval;
        time = timeProvider ?? TimeProvider.System;
    }

    public string SourceKey { get; }
    public int SampleCount => Volatile.Read(ref sampleCount);
    public DateTimeOffset? LastSampleAt => Interlocked.Read(ref lastSampleUtcTicks) is var ticks && ticks > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    public async IAsyncEnumerable<Observation<T>> SubscribeAsync(TimeSpan requestedInterval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (requestedInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestedInterval));
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<Observation<T>>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            subscribers.Add(id, new(channel, requestedInterval));
            if (latest is not null) channel.Writer.TryWrite(latest);
            if (runTask is null || runTask.IsCompleted || runCancellation?.IsCancellationRequested == true)
            {
                runCancellation = new CancellationTokenSource();
                runTask = RunAsync(runCancellation.Token);
            }
        }

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken)) yield return item;
        }
        finally
        {
            lock (gate)
            {
                subscribers.Remove(id);
                if (subscribers.Count == 0) runCancellation?.Cancel();
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan interval;
                lock (gate)
                {
                    if (subscribers.Count == 0) break;
                    interval = subscribers.Values.Min(x => x.Interval);
                }
                var delay = interval < minimumInterval ? minimumInterval : interval;
                try
                {
                    var observation = await sample(cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref sampleCount);
                    Interlocked.Exchange(ref lastSampleUtcTicks, time.GetUtcNow().UtcTicks);
                    Subscriber[] targets;
                    lock (gate)
                    {
                        latest = observation;
                        targets = subscribers.Values.ToArray();
                    }
                    foreach (var target in targets) target.Channel.Writer.TryWrite(observation);
                    await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch
                {
                    await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { }
    }

    public static ObservationSnapshot<T> Snapshot(IEnumerable<Observation<T>> observations,
        string hostClockEpoch, double monotonicMs)
    {
        var current = observations.ToArray();
        var fresh = current.Where(x => x.HostClockEpoch == hostClockEpoch &&
            monotonicMs >= x.MonotonicMs && monotonicMs - x.MonotonicMs <= x.ValidForMs &&
            x.Quality.KnownValue is not (ObservationQuality.Stale or ObservationQuality.Unknown)).ToArray();
        var conflicts = fresh.GroupBy(x => x.Generation.Value)
            .Where(group => group.Select(x => x.Value).Distinct().Skip(1).Any())
            .SelectMany(group => group.Select(x => x.Source.Provider)).Distinct(StringComparer.Ordinal).ToArray();
        var quality = conflicts.Length > 0 ? ObservationQuality.Conflict : fresh.Length == 0 ? ObservationQuality.Stale :
            fresh.Any(x => x.Quality.KnownValue == ObservationQuality.Partial) ? ObservationQuality.Partial :
            fresh.Any(x => x.Quality.KnownValue == ObservationQuality.Estimated) ? ObservationQuality.Estimated : ObservationQuality.Confirmed;
        return new(current, quality, fresh.Length > 0 && conflicts.Length == 0, conflicts);
    }

    public async ValueTask DisposeAsync()
    {
        Task? task;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            runCancellation?.Cancel();
            task = runTask;
            foreach (var subscriber in subscribers.Values) subscriber.Channel.Writer.TryComplete();
            subscribers.Clear();
        }
        if (task is not null) try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        runCancellation?.Dispose();
    }
}
