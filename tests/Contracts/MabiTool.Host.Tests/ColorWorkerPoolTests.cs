using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using Xunit;

namespace MabiTool.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class ColorWorkerPoolTests : IDisposable
{
    private readonly string repository;
    private readonly string root;
    private readonly string python;
    private readonly ConcurrentQueue<WorkerFailure> failures = new();
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
    }

    public ColorWorkerPoolTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
        repository = directory!.FullName;
        root = Path.Combine(repository, "artifacts", "tmp", "color-pool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("import sys; print(sys.executable)");
        using var process = Process.Start(start)!;
        python = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); Assert.Equal(0, process.ExitCode);
    }
    private ColorWorkerOptions Options(string? mode = "pool", int retention = 60) => new(python,
        Path.Combine(repository, "workers", "color-engine", mode is null ? "" : "tests"), root)
    {
        EntryModule = mode is null ? "mabitool_color.worker" : "fault_peer", Arguments = mode is null ? [] : [mode],
        IdleRetentionSeconds = retention, MaximumParallelism = 2,
        HeartbeatInterval = TimeSpan.FromMilliseconds(100), HeartbeatTimeout = TimeSpan.FromSeconds(3)
    };
    private static WorkerRequest Request(string id) => new("color.evaluate", JsonSerializer.SerializeToElement(new
    {
        profile = new { schema = 1, search_strategy = "screenshot", priority_order = new[] { 0, 1, 2 },
            regions = Enumerable.Range(0, 3).Select(_ => new { enabled = true, target = "#202020", alt = "", mode = "精准 HEX", tolerance = 8 }) },
        colors = new[] { "#202020", "#202020" }
    }), id, "0", "0");
    private async Task<WorkerFrame> Evaluate(ColorWorkerPool.Rental rental)
    {
        rental.Call = await rental.Worker.BeginAsync(Request(rental.RunId));
        var terminal = await rental.Call.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        await foreach (var _ in rental.Call.Events.ReadAllAsync()) { }
        return terminal;
    }

    [Fact]
    public async Task OriginalAlgorithmLoadsOnDemandAndTwoRentalsReuseTheSameProcess()
    {
        await using var pool = new ColorWorkerPool(Options(null));
        Assert.Equal("idle", pool.Snapshot().State); Assert.Null(pool.ProcessId);
        int first;
        await using (var rental = await pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue))
        {
            first = rental.Worker.ProcessId;
            Assert.True((await Evaluate(rental)).Payload.GetProperty("result").GetProperty("accepted").GetBoolean());
            Assert.Equal("busy", pool.Snapshot().State);
            var busy = await Assert.ThrowsAsync<WorkerException>(() => pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue));
            Assert.Equal("worker.busy", busy.Code); Assert.True(rental.Worker.IsAlive);
        }
        Assert.Equal("warm", pool.Snapshot().State);
        await using (var rental = await pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue))
        {
            Assert.Equal(first, rental.Worker.ProcessId);
            Assert.Equal("result", (await Evaluate(rental)).Kind);
        }
        Assert.Equal(new UInt64String(1), pool.Snapshot().StartedWorkers); Assert.Empty(failures);
    }

    [Fact]
    public async Task IdleRetentionUsesMonotonicTimeAndWorkerReceivesTheThreadBudget()
    {
        var clock = new Clock();
        await using var pool = new ColorWorkerPool(Options(), clock);
        await using (var rental = await pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue))
            Assert.All((await Evaluate(rental)).Payload.GetProperty("threads").EnumerateObject(), row => Assert.Equal("2", row.Value.GetString()));
        clock.Ms = 59999; await pool.CollectIdleAsync(); Assert.NotNull(pool.ProcessId);
        clock.Ms = 60000; await pool.CollectIdleAsync(); Assert.Null(pool.ProcessId); Assert.Equal("idle", pool.Snapshot().State);
        await using (var rental = await pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue)) await Evaluate(rental);
        Assert.Equal(new UInt64String(2), pool.Snapshot().StartedWorkers);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ZeroRetentionAndUnfinishedCallsAreNeverKeptWarm()
    {
        await using var noRetention = new ColorWorkerPool(Options(retention: 0));
        await using (var rental = await noRetention.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue)) await Evaluate(rental);
        Assert.Null(noRetention.ProcessId);
        await using var pool = new ColorWorkerPool(Options("cancel"));
        await using (var rental = await pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue))
        {
            rental.Call = await rental.Worker.BeginAsync(Request(rental.RunId));
            await rental.Call.Events.ReadAsync();
        }
        Assert.Null(pool.ProcessId); Assert.Equal("idle", pool.Snapshot().State);
    }

    [Fact]
    public async Task FaultedWorkerIsRebuiltAndShutdownCleansActiveRentalBeforeExit()
    {
        await using var pool = new ColorWorkerPool(Options("crash"));
        int first;
        await using (var rental = await pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue))
        {
            first = rental.Worker.ProcessId;
            await Assert.ThrowsAsync<WorkerException>(() => Evaluate(rental));
        }
        var shutdown = new ConcurrentQueue<WorkerFailure>();
        await using var next = await pool.RentAsync(Guid.NewGuid().ToString("N"), shutdown.Enqueue);
        Assert.NotEqual(first, next.Worker.ProcessId); Assert.Empty(shutdown);
        await pool.DisposeAsync();
        Assert.Equal("worker.stopped", Assert.Single(shutdown).Code);
        Assert.Null(pool.ProcessId); Assert.Equal("stopped", pool.Snapshot().State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue));
    }

    [Fact]
    public async Task PendingOldPortIsCleanedBeforeAnotherRentalCanStart()
    {
        await using var pool = new ColorWorkerPool(Options("port-late"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rental = await pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue);
        var first = rental.Worker.ProcessId;
        rental.Call = await rental.Worker.BeginAsync(Request(rental.RunId), portHandler: async (_, _) =>
        { entered.SetResult(); await release.Task; return JsonSerializer.SerializeToElement(new { }); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rental.Worker.CancelAsync(rental.Call);
        Assert.Equal("cancelled", (await rental.Call.Completion).Kind);
        var returning = rental.DisposeAsync().AsTask();
        try
        {
            Assert.Null(pool.ProcessId);
            Assert.False(returning.IsCompleted);
            Assert.Equal("worker.busy", (await Assert.ThrowsAsync<WorkerException>(() => pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue))).Code);
        }
        finally { release.TrySetResult(); await returning; }
        await using var next = await pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue);
        Assert.NotEqual(first, next.Worker.ProcessId); Assert.True(next.Worker.IsAlive);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task PoolRotatesAfterItsBoundedRequestHistory()
    {
        await using var pool = new ColorWorkerPool(Options());
        int first = 0;
        for (var i = 0; i < 2049; i++)
        {
            await using var rental = await pool.RentAsync(Guid.NewGuid().ToString("N"), failures.Enqueue);
            if (i == 0) first = rental.Worker.ProcessId;
            Assert.Equal(i < 2048, first == rental.Worker.ProcessId);
            Assert.Equal("result", (await Evaluate(rental)).Kind);
            // 保持在真实管道每秒 256 帧预算内，同时覆盖整段身份历史。
            await Task.Delay(12);
        }
        Assert.Equal(new UInt64String(2), pool.Snapshot().StartedWorkers); Assert.Empty(failures);
    }

    public void Dispose()
    {
        var allowed = Path.GetFullPath(Path.Combine(repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(root, true);
    }
}
