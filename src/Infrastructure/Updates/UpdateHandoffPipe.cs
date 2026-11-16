using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace MabiTool.Infrastructure.Updates;

/// <summary>一次性交接监听；接受后，连接接管管道及总期限。</summary>
[SupportedOSPlatform("windows")]
internal sealed class UpdateHandoffListener : IDisposable
{
    internal const string Prefix = "mabitool-update-";
    internal string SessionId { get; } = Guid.NewGuid().ToString("N");
    internal string PipeName => Prefix + SessionId;
    private NamedPipeServerStream? pipe;
    private CancellationTokenSource? deadline;
    private readonly string transactionId;
    private readonly object lifetime = new();
    private int accepted;

    internal UpdateHandoffListener(string transactionId, TimeProvider? time = null)
    {
        if (!UpdateHandoffProtocol.Id(transactionId)) throw UpdateHandoffProtocol.Invalid();
        this.transactionId = transactionId;
        var user = WindowsIdentity.GetCurrent().User ?? throw UpdateHandoffProtocol.Invalid();
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        deadline = new(UpdateHandoffProtocol.Timeout, time ?? TimeProvider.System);
    }

    internal async Task<UpdateHandoffConnection> AcceptAsync(UpdatePeerIdentity identity, CancellationToken token = default)
    {
        NamedPipeServerStream currentPipe;
        CancellationTokenSource currentDeadline;
        lock (lifetime)
        {
            if (accepted != 0 || pipe is null || deadline is null) throw UpdateHandoffProtocol.Invalid();
            accepted = 1;
            currentPipe = pipe;
            currentDeadline = deadline;
        }
        PinnedUpdatePeer? peer = null;
        try
        {
            peer = new(identity);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, currentDeadline.Token);
            await currentPipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
            peer.VerifyPipe(currentPipe, server: true);
            linked.Token.ThrowIfCancellationRequested();
            lock (lifetime)
            {
                if (!ReferenceEquals(pipe, currentPipe) || !ReferenceEquals(deadline, currentDeadline)) throw UpdateHandoffProtocol.Invalid();
                var connection = new UpdateHandoffConnection(currentPipe, peer, currentDeadline, SessionId, transactionId);
                pipe = null; deadline = null; peer = null;
                return connection;
            }
        }
        catch (OperationCanceledException) when (currentDeadline.IsCancellationRequested && !token.IsCancellationRequested)
        { throw new UpdatePlanException("update.handoffTimeout"); }
        finally
        {
            peer?.Dispose();
            // 成功时所有权已转移，失败时关闭未认证的管道。
            Dispose();
        }
    }

    public void Dispose()
    {
        lock (lifetime)
        {
            deadline?.Cancel();
            pipe?.Dispose(); pipe = null;
            // 已进入 Accept 的等待持有独立引用，取消后可安全读取其取消状态。
            deadline?.Dispose(); deadline = null;
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class UpdateHandoffConnection : IDisposable
{
    private readonly PipeStream pipe;
    private readonly PinnedUpdatePeer peer;
    private readonly CancellationTokenSource deadline;
    private readonly string sessionId;
    private readonly string transactionId;
    private readonly SemaphoreSlim reading = new(1, 1);
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly object lifetime = new();
    private int uses;
    private int received, sent, disposed;
    internal UpdatePeerIdentity Peer => peer.Identity;
    internal string TransactionId => transactionId;

    internal UpdateHandoffConnection(PipeStream pipe, PinnedUpdatePeer peer, CancellationTokenSource deadline,
        string sessionId, string transactionId)
    { this.pipe = pipe; this.peer = peer; this.deadline = deadline; this.sessionId = sessionId; this.transactionId = transactionId; }

    internal static async Task<UpdateHandoffConnection> ConnectAsync(string pipeName, UpdatePeerIdentity identity,
        string transactionId, CancellationToken token = default, TimeProvider? time = null)
    {
        if (pipeName is null || !pipeName.StartsWith(UpdateHandoffListener.Prefix, StringComparison.Ordinal) ||
            !UpdateHandoffProtocol.Id(pipeName[UpdateHandoffListener.Prefix.Length..]) || !UpdateHandoffProtocol.Id(transactionId))
            throw UpdateHandoffProtocol.Invalid();
        var peer = new PinnedUpdatePeer(identity);
        var deadline = new CancellationTokenSource(UpdateHandoffProtocol.Timeout, time ?? TimeProvider.System);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Anonymous);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            var user = WindowsIdentity.GetCurrent().User ?? throw UpdateHandoffProtocol.Invalid();
            if (!user.Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)))) throw UpdateHandoffProtocol.Invalid();
            peer.VerifyPipe(pipe, server: false);
            linked.Token.ThrowIfCancellationRequested();
            return new(pipe, peer, deadline, pipeName[UpdateHandoffListener.Prefix.Length..], transactionId);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        { pipe.Dispose(); peer.Dispose(); deadline.Dispose(); throw new UpdatePlanException("update.handoffTimeout"); }
        catch { pipe.Dispose(); peer.Dispose(); deadline.Dispose(); throw; }
    }

    internal Task SendAsync<T>(string kind, T payload, CancellationToken token = default) => TrackAsync("send:" + kind, UseAsync(writing, async ct =>
    {
        var envelope = new UpdateHandoffEnvelope(UpdateHandoffProtocol.Version, sessionId, transactionId, sent + 1,
            kind, JsonSerializer.SerializeToElement(payload, UpdateHandoffProtocol.Json));
        await UpdateHandoffProtocol.WriteAsync(pipe, envelope, ct).ConfigureAwait(false);
        sent++;
        return true;
    }, token));

    internal Task<T> ReceiveAsync<T>(string expectedKind, CancellationToken token = default) => TrackAsync("receive:" + expectedKind, UseAsync(reading, async ct =>
    {
        var envelope = await UpdateHandoffProtocol.ReadAsync(pipe, ct).ConfigureAwait(false);
        if (envelope.SessionId != sessionId || envelope.TransactionId != transactionId || envelope.Sequence != received + 1 ||
            envelope.Kind != expectedKind) throw UpdateHandoffProtocol.Invalid();
        var payload = envelope.Payload.Deserialize<T>(UpdateHandoffProtocol.Json) ?? throw UpdateHandoffProtocol.Invalid();
        received++;
        return payload;
    }, token));

    private static async Task<T> TrackAsync<T>(string message, Task<T> pending)
    {
        try { return await pending.ConfigureAwait(false); }
        catch (Exception error) { error.Data["handoffMessage"] = message; throw; }
    }

    internal Task ConfirmPeerClosedAsync(CancellationToken token = default) => UseAsync(reading, async ct =>
    {
        var trailing = new byte[1];
        if (await pipe.ReadAsync(trailing, ct).ConfigureAwait(false) != 0) throw UpdateHandoffProtocol.Invalid();
        return true;
    }, token);

    private async Task<T> UseAsync<T>(SemaphoreSlim gate, Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        lock (lifetime)
        {
            if (disposed != 0) throw UpdateHandoffProtocol.Invalid();
            uses++;
        }
        var entered = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            peer.VerifyAlive();
            var result = await action(linked.Token).ConfigureAwait(false);
            peer.VerifyAlive();
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        { Dispose(); throw new UpdatePlanException("update.handoffTimeout"); }
        catch (JsonException) { Dispose(); throw UpdateHandoffProtocol.Invalid(); }
        catch { Dispose(); throw; }
        finally
        {
            if (entered) gate.Release();
            lock (lifetime)
            {
                uses--;
                if (disposed != 0 && uses == 0) ReleaseResources();
            }
        }
    }

    public void Dispose()
    {
        lock (lifetime)
        {
            if (disposed != 0) return;
            disposed = 1;
            deadline.Cancel();
            pipe.Dispose();
            peer.Dispose();
            if (uses == 0) ReleaseResources();
        }
    }

    private void ReleaseResources() { deadline.Dispose(); reading.Dispose(); writing.Dispose(); }
}
