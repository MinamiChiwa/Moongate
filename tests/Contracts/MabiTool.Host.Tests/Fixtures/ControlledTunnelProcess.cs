using System.Collections.Concurrent;
using System.Threading.Channels;
using MabiTool.Providers.Tunnels;

namespace MabiTool.Host.Tests;

internal sealed class ControlledTunnelProcessFactory : ITunnelProcessFactory
{
    public ConcurrentQueue<IReadOnlyList<string>> Arguments { get; } = new();
    public string Version { get; set; } = "2026.10.0";
    public ControlledTunnelProcess Tunnel { get; } = new();
    public ITunnelProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        Arguments.Enqueue(arguments.ToArray());
        return arguments.SequenceEqual(["--version"]) ? new VersionProcess(Version) : Tunnel;
    }
    private sealed class VersionProcess(string version) : ITunnelProcess
    {
        public TextReader StandardOutput { get; } = new StringReader("cloudflared version " + version + " (test)\n");
        public TextReader StandardError { get; } = new StringReader("");
        public Task<int> WaitForExitAsync(CancellationToken ct = default) => Task.FromResult(0);
        public void Terminate() { }
        public ValueTask DisposeAsync() { StandardOutput.Dispose(); StandardError.Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class ControlledTunnelProcess : ITunnelProcess
{
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ChunkReader Output { get; } = new();
    public ChunkReader Error { get; } = new();
    public TextReader StandardOutput => Output;
    public TextReader StandardError => Error;
    public bool Terminated { get; private set; }
    public bool Disposed { get; private set; }
    public bool FailTermination { get; set; }
    public bool FailDisposal { get; set; }
    public Task<int> WaitForExitAsync(CancellationToken ct = default) => exit.Task.WaitAsync(ct);
    public void Terminate()
    {
        Terminated = true; Output.Complete(); Error.Complete(); exit.TrySetResult(1);
        if (FailTermination) throw new InvalidOperationException("测试进程终止失败");
    }
    public ValueTask DisposeAsync()
    {
        Output.Dispose(); Error.Dispose(); Disposed = true; exit.TrySetResult(1);
        if (FailDisposal) throw new InvalidOperationException("测试进程释放失败");
        return ValueTask.CompletedTask;
    }

    internal sealed class ChunkReader : TextReader
    {
        private readonly Channel<string> chunks = Channel.CreateBounded<string>(8);
        private string current = "";
        private int offset;
        public void Write(string value) { if (!chunks.Writer.TryWrite(value)) throw new InvalidOperationException("测试输出队列已满"); }
        public void Complete() => chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            while (offset == current.Length)
            {
                if (!await chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (chunks.Reader.TryRead(out var next)) { current = next; offset = 0; }
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
        }
        protected override void Dispose(bool disposing) { Complete(); base.Dispose(disposing); }
    }
}

internal sealed class ControlledTunnelProbe(Func<Uri, string, CancellationToken, Task<bool>> verify) : IRoomTunnelProbe
{
    public Task<bool> VerifyAsync(Uri endpoint, string epoch, CancellationToken ct) => verify(endpoint, epoch, ct);
}
