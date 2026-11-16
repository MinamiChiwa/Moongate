using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed class AssetReadLease(AssetSnapshot snapshot, FileStream stream, Func<Task> release,
    IDisposable activity) : IAsyncDisposable
{
    private int disposed;
    public AssetSnapshot Snapshot { get; } = snapshot;
    public Stream Content { get; } = stream;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await stream.DisposeAsync(); }
        finally { try { await release(); } finally { activity.Dispose(); } }
    }
}
