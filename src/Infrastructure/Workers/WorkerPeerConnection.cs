using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MabiTool.Infrastructure.Workers;

/// <summary>C# Worker 核对实际父进程、创建身份、管道服务器和一次性凭据。</summary>
[SupportedOSPlatform("windows")]
public sealed class WorkerPeerConnection : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly Process host;
    private readonly SemaphoreSlim writes = new(1);
    private ulong incoming;
    private ulong outgoing;
    private WorkerPeerConnection(NamedPipeClientStream pipe, Process host) { this.pipe = pipe; this.host = host; }

    public static async Task<WorkerPeerConnection> ConnectFromEnvironmentAsync(string workerId, CancellationToken ct = default)
    {
        string Read(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
            return value ?? throw new WorkerException("worker.handshakeInvalid");
        }
        var name = Read("MABITOOL_WORKER_PIPE");
        var secret = Read("MABITOOL_WORKER_TOKEN");
        var pidText = Read("MABITOOL_WORKER_HOST_PID");
        var startText = Read("MABITOOL_WORKER_HOST_START");
        if (!name.StartsWith("mabitool-worker-", StringComparison.Ordinal) || name.Length != 48 ||
            secret.Length != 64 || secret.Any(c => !char.IsAsciiHexDigit(c)) ||
            !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            !long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            throw new WorkerException("worker.handshakeInvalid");
        var host = Process.GetProcessById(pid);
        NamedPipeClientStream? pipe = null;
        try
        {
            _ = host.Handle;
            if (host.HasExited || host.StartTime.ToUniversalTime().Ticks != ticks) throw new WorkerException("worker.peerInvalid");
            pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualPid) || actualPid != pid || host.HasExited)
                throw new WorkerException("worker.peerInvalid");
            var result = new WorkerPeerConnection(pipe, host);
            await result.SendAsync("hello", new { workerId, pid = Environment.ProcessId, token = secret,
                minimumVersion = 1, maximumVersion = 1 }, null, deadline.Token).ConfigureAwait(false);
            var ready = await result.ReadAsync(deadline.Token).ConfigureAwait(false);
            if (ready.Kind != "ready" || ready.Payload.EnumerateObject().Count() != 1 ||
                ready.Payload.GetProperty("workerId").GetString() != workerId) throw new WorkerException("worker.handshakeInvalid");
            return result;
        }
        catch { pipe?.Dispose(); host.Dispose(); throw; }
    }
    public async Task<WorkerFrame> ReadAsync(CancellationToken ct = default)
    {
        if (host.HasExited) throw new WorkerException("worker.peerInvalid");
        var frame = await WorkerProtocol.ReadAsync(pipe, ct).ConfigureAwait(false);
        if (frame.MessageId != checked(++incoming).ToString(CultureInfo.InvariantCulture))
            throw new WorkerException("worker.sequenceInvalid");
        return frame;
    }
    public async Task SendAsync(string kind, object payload, WorkerFrame? scope, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await writes.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            var frame = new WorkerFrame(1, checked(++outgoing).ToString(CultureInfo.InvariantCulture), scope?.RequestId,
                kind, scope?.RunId, scope?.BindingRevision, scope?.FencingToken,
                payload is JsonElement json ? json : JsonSerializer.SerializeToElement(payload, WorkerProtocol.Json));
            await WorkerProtocol.WriteAsync(pipe, frame, deadline.Token).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }
    /// <summary>发送正文为已编码 UTF-8 JSON 对象的一条消息（原样拼入帧，不再解析与重新编码）。</summary>
    public async Task SendEncodedAsync(string kind, ReadOnlyMemory<byte> payload, WorkerFrame? scope, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await writes.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            var envelope = new WorkerFrame(1, checked(++outgoing).ToString(CultureInfo.InvariantCulture), scope?.RequestId,
                kind, scope?.RunId, scope?.BindingRevision, scope?.FencingToken, default);
            await WorkerProtocol.WriteAsync(pipe, envelope, payload, deadline.Token).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }
    public ValueTask DisposeAsync() { pipe.Dispose(); host.Dispose(); writes.Dispose(); return ValueTask.CompletedTask; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
