using System.Text.Json;
using System.Threading.Channels;
using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.Tunnels;

namespace MabiTool.Host.Rooms;

public sealed class RoomServerOperations : BackgroundService
{
    private sealed class ServerLease
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource stop = new();
        private Task? disposal;
        private bool disposed;
        private readonly IDisposable activity;
        public string Owner { get; }
        public RoomServerMode Mode { get; }
        public CancellationToken Token { get; }
        public ServerLease(string owner, RoomServerMode mode, IDisposable activity)
        { Owner = owner; Mode = mode; this.activity = activity; Token = stop.Token; }
        public IAsyncDisposable? Module;
        public IRoomServerListener? Listener;
        public QuickTunnelSession? Tunnel;
        public void Cancel() { lock (gate) if (!disposed) stop.Cancel(); }
        public Task ReleaseAsync() { lock (gate) return disposal ??= ReleaseCoreAsync(); }
        private async Task ReleaseCoreAsync()
        {
            List<Exception> failures = [];
            try { Cancel(); } catch (Exception error) { failures.Add(error); }
            foreach (var resource in new IAsyncDisposable?[] { Tunnel, Listener, Module })
                if (resource is not null)
                    try { await resource.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            try { activity.Dispose(); } catch (Exception error) { failures.Add(error); }
            lock (gate) { disposed = true; stop.Dispose(); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }
    private sealed class Job(string id, bool start, ServerLease lease)
    {
        public string Id { get; } = id;
        public bool Start { get; } = start;
        public ServerLease Lease { get; } = lease;
        public bool Cancelled;
    }
    private readonly OperationStore operations;
    private readonly ActivityBarrier barrier;
    private readonly ModuleRuntime modules;
    private readonly StateEventHub events;
    private readonly TimeProvider time;
    private readonly QuickTunnelFactory tunnels;
    private readonly IRoomServerListenerFactory listeners;
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(2) { SingleReader = true });
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private RoomServerSnapshot state;
    private ServerLease? active;
    private bool accepting;

    public RoomServerOperations(OperationStore operations, ActivityBarrier barrier, ModuleRuntime modules, StateEventHub events,
        SystemInfo info, TimeProvider time, QuickTunnelFactory tunnels, IRoomServerListenerFactory? listeners = null)
    {
        this.operations = operations; this.barrier = barrier; this.modules = modules; this.events = events;
        this.time = time; this.tunnels = tunnels; this.listeners = listeners ?? new RoomServerListenerFactory(time);
        state = new(info.InstanceId, new(1), RoomServerState.Stopped, null, null, null, null, null, [],
            new(RoomTunnelState.NotRequested, null, null), null);
    }
    public RoomServerSnapshot Get() => Volatile.Read(ref state);
    public override async Task StartAsync(CancellationToken ct)
    {
        await PublishAsync(state, ct); accepting = true; await base.StartAsync(ct);
    }

    public async Task<OperationSnapshot> StartServerAsync(AuthSession session, RoomServerStartRequest request,
        string? key, string? ifMatch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        IDisposable? activity = null;
        await admission.WaitAsync(ct);
        try
        {
            var accepted = await operations.AdmitRoomServerAsync(session.ClientId, true, request, key, ifMatch,
                OperationStore.RoomServerETag(state), () =>
                {
                    if (!accepting) throw new OperationException("room.serverUnavailable", "建房服务正在关闭");
                    if (active is not null) throw new OperationException("resource.busy", "本机已有建房服务");
                    if (jobs.Count >= 2) throw new OperationException("resource.capacityExceeded", "建房服务队列已满");
                    try { activity = barrier.EnterActivity(session.ClientId, "room.server"); }
                    catch (ActivityBarrierException error) { throw new OperationException(error.Code, "建房服务无法进入活动状态"); }
                }, ct);
            if (accepted.Created)
            {
                active = new(session.ClientId, request.Mode.KnownValue!.Value, activity!); activity = null;
                var job = new Job(accepted.Snapshot.Id, true, active); jobs.Add(job.Id, job);
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("已受理建房服务未能进入保留队列");
                await ChangeAsync(state with { State = RoomServerState.Starting, Mode = request.Mode,
                    Owner = session.ClientId, OperationId = job.Id, Problem = null,
                    Tunnel = new(request.Mode.KnownValue == RoomServerMode.Public ? RoomTunnelState.Starting : RoomTunnelState.NotRequested, null, null) });
            }
            return accepted.Snapshot;
        }
        finally { activity?.Dispose(); admission.Release(); }
    }

    public async Task<OperationSnapshot> StopServerAsync(AuthSession session, string? key, string? ifMatch, CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try
        {
            var accepted = await operations.AdmitRoomServerAsync(session.ClientId, false, null, key, ifMatch,
                OperationStore.RoomServerETag(state), () =>
                {
                    if (!accepting) throw new OperationException("room.serverUnavailable", "建房服务正在关闭");
                    if (active is null || state.State.KnownValue == RoomServerState.Stopping)
                        throw new OperationException("resource.busy", "建房服务已停止或正在关闭");
                    if (!session.Owner && active.Owner != session.ClientId)
                        throw new OperationException("permission.denied", "关闭建房服务需要启动客户端或宿主所有者权限");
                    if (jobs.Count >= 2) throw new OperationException("resource.capacityExceeded", "建房服务队列已满");
                }, ct);
            if (accepted.Created)
            {
                var job = new Job(accepted.Snapshot.Id, false, active!); jobs.Add(job.Id, job);
                active!.Cancel();
                if (!queue.Writer.TryWrite(job)) throw new InvalidOperationException("已受理关闭未能进入保留队列");
                await ChangeAsync(state with { State = RoomServerState.Stopping, OperationId = job.Id,
                    Tunnel = state.Tunnel with { Endpoint = null } });
            }
            return accepted.Snapshot;
        }
        finally { admission.Release(); }
    }

    public async Task<OperationSnapshot> CancelAsync(string id, AuthSession session, string? reason, CancellationToken ct)
    {
        await admission.WaitAsync(ct);
        try
        {
            var previous = operations.Get(id, session.ClientId, session.Owner);
            if (!OperationStore.IsRoomServerOperation(previous.Kind)) throw new OperationException("resource.notFound", "建房服务操作不存在");
            var result = await operations.CancelAsync(id, session.ClientId, session.Owner, reason, ct);
            if (!SqliteStore.IsTerminal(result.State.KnownValue) && jobs.TryGetValue(id, out var job))
            {
                job.Cancelled = true; job.Lease.Cancel();
                await ChangeAsync(state with { State = RoomServerState.Stopping, Tunnel = state.Tunnel with { Endpoint = null } });
            }
            return result;
        }
        finally { admission.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync())
            {
                using var hostStop = stoppingToken.Register(job.Lease.Cancel);
                try
                {
                    if (job.Start) await ExecuteStartAsync(job, stoppingToken);
                    else await ExecuteStopAsync(job, stoppingToken);
                }
                finally
                {
                    await admission.WaitAsync();
                    try { jobs.Remove(job.Id); }
                    finally { admission.Release(); }
                }
            }
        }
        finally
        {
            ServerLease? remaining;
            await admission.WaitAsync();
            try { accepting = false; remaining = active; }
            finally { admission.Release(); }
            if (remaining is not null) await ReleaseAsync(remaining);
        }
    }
    private async Task ExecuteStartAsync(Job job, CancellationToken hostStop)
    {
        try
        {
            var operation = await operations.BeginRoomServerAsync(job.Id);
            if (operation is null) { await ReleaseAsync(job.Lease); return; }
            job.Lease.Token.ThrowIfCancellationRequested();
            job.Lease.Module = await modules.AcquireAsync("rooms", job.Id, job.Lease.Token);
            job.Lease.Listener = await listeners.StartAsync(job.Lease.Mode, job.Lease.Token);
            await admission.WaitAsync();
            try
            {
                job.Lease.Token.ThrowIfCancellationRequested();
                await ChangeAsync(state with { State = job.Lease.Mode == RoomServerMode.Public ? RoomServerState.Starting : RoomServerState.Running,
                    ServerEpoch = job.Lease.Listener.Epoch, LocalEndpoint = job.Lease.Listener.LocalEndpoint,
                    LanEndpoints = job.Lease.Listener.LanEndpoints });
            }
            finally { admission.Release(); }
            RoomTunnelSnapshot? tunnel = null;
            if (job.Lease.Mode == RoomServerMode.Public)
            {
                job.Lease.Tunnel = tunnels.Open(job.Lease.Listener.Port, job.Lease.Listener.Epoch,
                    value => TunnelChangedAsync(job.Lease, value));
                tunnel = await job.Lease.Tunnel.Ready.WaitAsync(job.Lease.Token);
            }
            await admission.WaitAsync();
            try
            {
                job.Lease.Token.ThrowIfCancellationRequested();
                await operations.FinishRoomServerAsync(job.Id, state, tunnel?.Problem);
            }
            finally { admission.Release(); }
        }
        catch (OperationCanceledException) when (job.Lease.Token.IsCancellationRequested)
        {
            var problem = await ReleaseAsync(job.Lease);
            await operations.FinishRoomServerAsync(job.Id, Get(), problem, cancelled: problem is null && !hostStop.IsCancellationRequested,
                interrupted: hostStop.IsCancellationRequested);
        }
        catch (Exception error)
        {
            var problem = await ReleaseAsync(job.Lease, Problem(error, job.Id));
            await operations.FinishRoomServerAsync(job.Id, Get(), problem);
        }
    }
    private async Task ExecuteStopAsync(Job job, CancellationToken hostStop)
    {
        await operations.BeginRoomServerAsync(job.Id);
        var problem = await ReleaseAsync(job.Lease);
        await operations.FinishRoomServerAsync(job.Id, Get(), problem, cancelled: problem is null && job.Cancelled && !hostStop.IsCancellationRequested,
            interrupted: hostStop.IsCancellationRequested);
    }
    private async Task TunnelChangedAsync(ServerLease lease, RoomTunnelSnapshot value)
    {
        await admission.WaitAsync();
        try
        {
            if (!ReferenceEquals(active, lease) || lease.Token.IsCancellationRequested) return;
            await ChangeAsync(state with { State = RoomServerState.Running, Tunnel = value, Problem = value.Problem });
        }
        finally { admission.Release(); }
    }
    private async Task<ApiProblem?> ReleaseAsync(ServerLease lease, ApiProblem? problem = null)
    {
        try { await lease.ReleaseAsync(); }
        catch (Exception error) { problem ??= Problem(error, "room-server"); }
        finally
        {
            await admission.WaitAsync();
            try
            {
                if (ReferenceEquals(active, lease))
                {
                    active = null;
                    await ChangeAsync(state with { State = problem is null ? RoomServerState.Stopped : RoomServerState.Failed,
                        Mode = null, Owner = null, ServerEpoch = null, LocalEndpoint = null, LanEndpoints = [],
                        Tunnel = new(RoomTunnelState.NotRequested, null, null), Problem = problem });
                }
            }
            finally { admission.Release(); }
        }
        return problem;
    }
    private async Task ChangeAsync(RoomServerSnapshot value)
    {
        value = value with { Revision = new(checked(state.Revision.Value + 1)) };
        Volatile.Write(ref state, value); await PublishAsync(value);
    }
    private Task PublishAsync(RoomServerSnapshot value, CancellationToken ct = default) => events.PublishVolatileAsync(
        new("room-server-" + value.Revision, "room-server", "local", value.Revision, "room.server.changed",
            JsonSerializer.SerializeToElement(value, ContractJson.Options)), time.GetUtcNow(), ct);
    private static ApiProblem Problem(Exception error, string id) => ApiErrors.Create(error switch
    {
        OperationException known => known.Code, ModuleRuntimeException known => known.Code,
        TunnelException known => known.Code, _ => "room.serverUnavailable"
    }, "建房服务未完成", id);

    public override async Task StopAsync(CancellationToken ct)
    {
        await admission.WaitAsync();
        try
        {
            accepting = false; queue.Writer.TryComplete(); active?.Cancel();
            foreach (var job in jobs.Values) job.Lease.Cancel();
        }
        finally { admission.Release(); }
        await base.StopAsync(CancellationToken.None);
    }
}
