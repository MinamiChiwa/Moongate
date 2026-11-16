using System.Diagnostics;
using System.Runtime.Versioning;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure.Workers;

/// <summary>串行复用健康计算通道；每次租用重新绑定故障清理和运行身份。</summary>
public sealed class ColorWorkerPool : IAsyncDisposable
{
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource lifetime = new();
    private Task collector = Task.CompletedTask;
    private ColorWorkerSession? worker;
    private Rental? current;
    private string workerEpoch = "";
    private long idleAt;
    private int uses;
    private ulong starts;
    private bool stopped;
    private Task? disposal;
    public ColorWorkerOptions? Options { get; }
    public bool IsBusy { get { lock (gate) return current is not null; } }
    public int? ProcessId { get { lock (gate) return OperatingSystem.IsWindows() && worker?.IsAlive == true ? worker.ProcessId : null; } }

    public ColorWorkerPool(ColorWorkerOptions? options, TimeProvider? timeProvider = null)
    {
        if (options is not null && (options.IdleRetentionSeconds is < 0 or > 300 || options.MaximumParallelism is < 1 or > 4))
            throw new ArgumentException("染色保温期为 0–300 秒，并行度为 1–4");
        Options = options; clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>不查询进程内存的池状态，供运行状态总览使用；状态码与 Snapshot 一致。</summary>
    public (string State, ulong Starts, int Uses) DiagnosticState()
    {
        if (!OperatingSystem.IsWindows()) return ("unconfigured", 0, 0);
        lock (gate)
        {
            var alive = worker?.IsAlive == true;
            return (stopped ? "stopped" : Options is null ? "unconfigured" : current is not null ? alive ? "busy" : "starting" : alive ? "warm" : "idle", starts, uses);
        }
    }

    public ColorEngineRuntime Snapshot()
    {
        if (!OperatingSystem.IsWindows()) return new("unconfigured", 0, 60, 1, new(0), null);
        lock (gate)
        {
            var alive = worker?.IsAlive == true;
            ulong? bytes = null;
            if (alive)
                try { using var p = Process.GetProcessById(worker!.ProcessId); bytes = checked((ulong)p.WorkingSet64); }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
            return new(stopped ? "stopped" : Options is null ? "unconfigured" : current is not null ? alive ? "busy" : "starting" : alive ? "warm" : "idle",
                Options?.MaximumParallelism ?? 0, Options?.IdleRetentionSeconds ?? 60, 1, new(starts), bytes is { } b ? new(b) : null);
        }
    }

    public async Task<Rental> RentAsync(string runId, Action<WorkerFailure> cleanup, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new WorkerException("worker.platformUnsupported");
        if (Options is null) throw new WorkerException("worker.unconfigured");
        Rental rental;
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(stopped, this);
            if (current is not null) throw new WorkerException("worker.busy");
            rental = new(this, runId, cleanup); current = rental;
        }
        var entered = false;
        try
        {
            // 先预留业务租用，再等待空闲回收结束；活动租用仍立即返回占用。
            await mutation.WaitAsync(ct); entered = true;
            lock (gate) ObjectDisposedException.ThrowIf(stopped, this);
            if (worker is not null && (!worker.IsAlive || uses >= 2048)) await DropAsync();
            if (worker is null)
            {
                using var start = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
                var epoch = Guid.NewGuid().ToString("N");
                lock (gate) workerEpoch = epoch;
                var created = await ColorWorkerSession.StartAsync(Options, failure =>
                {
                    Action<WorkerFailure>? callback;
                    lock (gate) callback = workerEpoch == epoch && current is { } loan &&
                        (failure.RunId is null || failure.RunId == loan.RunId) ? loan.Cleanup : null;
                    callback?.Invoke(failure);
                }, start.Token);
                lock (gate)
                {
                    worker = created; starts++; uses = 0;
                    if (collector.IsCompleted) collector = CollectLoopAsync();
                }
            }
            ct.ThrowIfCancellationRequested();
            rental.Worker = worker!;
            return rental;
        }
        catch
        {
            if (entered) await DropAsync();
            lock (gate) if (ReferenceEquals(current, rental)) current = null;
            throw;
        }
        finally { if (entered) mutation.Release(); }
    }

    private async ValueTask ReturnAsync(Rental rental)
    {
        if (!OperatingSystem.IsWindows()) return;
        await mutation.WaitAsync();
        try
        {
            lock (gate) if (!ReferenceEquals(current, rental)) return;
            // 收尾端口不能跨租用保留；未完成调用及故障通道立即回收。
            if (rental.Call?.Completion.IsCompletedSuccessfully != true || rental.Call.PortPending ||
                worker?.IsAlive != true || Options!.IdleRetentionSeconds == 0 || stopped)
                await DropAsync();
            lock (gate) { current = null; idleAt = clock.GetTimestamp(); uses++; }
        }
        finally { mutation.Release(); }
    }

    public async Task CollectIdleAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!await mutation.WaitAsync(0, ct)) return;
        try
        {
            bool expired;
            lock (gate) expired = current is null && worker is not null &&
                (!worker.IsAlive || clock.GetElapsedTime(idleAt) >= TimeSpan.FromSeconds(Options!.IdleRetentionSeconds));
            if (expired) await DropAsync();
        }
        finally { mutation.Release(); }
    }
    private async Task DropAsync()
    {
        ColorWorkerSession? old;
        lock (gate) { old = worker; worker = null; workerEpoch = ""; }
        if (OperatingSystem.IsWindows() && old is not null) await old.DisposeAsync();
    }
    private async Task CollectLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) await CollectIdleAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        lock (gate) stopped = true;
        lifetime.Cancel();
        await collector;
        await mutation.WaitAsync();
        try
        {
            Rental? loan;
            lock (gate) loan = current;
            try { if (loan is not null) loan.Cleanup(new("worker.stopped", loan.RunId)); }
            finally { await DropAsync(); lock (gate) current = null; }
        }
        finally { mutation.Release(); lifetime.Dispose(); }
    }
    public sealed class Rental(ColorWorkerPool owner, string runId, Action<WorkerFailure> cleanup) : IAsyncDisposable
    {
        private ColorWorkerPool? pool = owner;
        public string RunId { get; } = runId;
        internal Action<WorkerFailure> Cleanup { get; } = cleanup;
        public ColorWorkerSession Worker { get; internal set; } = null!;
        public WorkerCall? Call { get; set; }
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref pool, null)?.ReturnAsync(this) ?? ValueTask.CompletedTask;
    }
}
