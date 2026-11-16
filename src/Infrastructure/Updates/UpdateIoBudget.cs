using System.Security.Cryptography;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure.Updates;

/// <summary>同一更新服务的下载共用带宽桶；校验与解压在每个块边界让出游戏关键活动。</summary>
public sealed class UpdateIoBudget
{
    public const int ChunkSize = 64 * 1024;
    private readonly SemaphoreSlim bandwidthGate = new(1);
    private readonly TimeProvider clock;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly ActivityBarrier? barrier;
    private readonly int? bytesPerSecond;
    private double tokens = ChunkSize;
    private long lastTimestamp;

    public UpdateIoBudget(int? maximumBytesPerSecond = null, ActivityBarrier? barrier = null,
        TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (maximumBytesPerSecond is < 65_536 or > 67_108_864) // null 为不限速
            throw new ArgumentOutOfRangeException(nameof(maximumBytesPerSecond));
        bytesPerSecond = maximumBytesPerSecond;
        this.barrier = barrier;
        this.clock = clock ?? TimeProvider.System;
        this.delay = delay ?? Task.Delay;
        lastTimestamp = this.clock.GetTimestamp();
    }

    internal IDisposable? EnterActivity(string kind) => barrier?.EnterActivity("platform-updates", kind);

    public async Task ConsumeDownloadAsync(int count, CancellationToken token = default)
    {
        if (count is < 0 or > ChunkSize) throw new ArgumentOutOfRangeException(nameof(count));
        if (bytesPerSecond is not { } rate) { token.ThrowIfCancellationRequested(); return; }
        await bandwidthGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var now = clock.GetTimestamp();
                tokens = Math.Min(ChunkSize, tokens + clock.GetElapsedTime(lastTimestamp, now).TotalSeconds * rate);
                lastTimestamp = now;
                if (tokens >= count) { tokens -= count; return; }
                await delay(TimeSpan.FromSeconds((count - tokens) / rate), token).ConfigureAwait(false);
            }
        }
        finally { bandwidthGate.Release(); }
    }

    public async Task PauseVerificationAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        while (barrier?.Snapshot().Activities.Any(item => item.Kind is "game.resource" or "room.ready") == true)
        {
            await delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
    }

    internal async Task<(long Length, string Hash)> CopyAndHashAsync(Stream input, Stream? output, long maximumLength,
        CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        long length = 0;
        while (true)
        {
            await PauseVerificationAsync(token).ConfigureAwait(false);
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (read > maximumLength - length) throw new FileTreeException("update.assetLengthMismatch");
            length += read;
            hash.AppendData(buffer, 0, read);
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
