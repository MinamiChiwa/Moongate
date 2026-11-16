using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Runtime.Tests;

public sealed class RuntimeTests
{
    private static ModuleRegistration Register(string id, Func<CancellationToken, ValueTask<IAsyncDisposable>>? factory = null,
        ModuleDependency[]? dependencies = null, Version? minimum = null, int capacity = 2) =>
        new(new(id, new(0, 1, 0), minimum ?? new(1, 0), new(3, 0), dependencies ?? [],
            MaximumConcurrentDemands: capacity, IdleRetentionSeconds: 5),
            factory ?? (_ => ValueTask.FromResult<IAsyncDisposable>(new Resource())));

    [Fact]
    public async Task EnumerationDoesNotStartModulesAndExplainsIncompatibility()
    {
        var starts = 0;
        var catalog = new ModuleCatalog(new(1, 0), [
            Register("lazy", _ => { starts++; return ValueTask.FromResult<IAsyncDisposable>(new Resource()); }),
            Register("future", minimum: new(2, 0)),
            Register("missing", dependencies: [new("absent", "0.1.0")]),
            Register("old-dependency", dependencies: [new("lazy", "1.0.0")])
        ]);
        await using var runtime = new ModuleRuntime(catalog);
        var (modules, _) = await runtime.SnapshotAsync();
        Assert.Equal(0, starts);
        Assert.Equal("idle", modules.Single(x => x.Id == "lazy").State);
        Assert.Contains(modules.Single(x => x.Id == "future").Problems, p => p.Code == "module.hostApiIncompatible");
        Assert.Contains(modules.Single(x => x.Id == "missing").Problems, p => p.DependencyId == "absent");
        Assert.Contains(modules.Single(x => x.Id == "old-dependency").Problems, p => p.Code == "module.dependencyVersionIncompatible");
        await Assert.ThrowsAsync<ModuleRuntimeException>(() => runtime.AcquireAsync("future", "test"));
        await Assert.ThrowsAsync<ModuleRuntimeException>(() => runtime.AcquireAsync("missing", "test"));
    }

    [Fact]
    public async Task ConcurrentDemandStartsOnceAndIdleRetentionReleases()
    {
        var starts = 0;
        var resource = new Resource();
        var time = new ManualClock();
        var catalog = new ModuleCatalog(new(1, 0), [Register("lazy",
            _ => { starts++; return ValueTask.FromResult<IAsyncDisposable>(resource); }, capacity: 20)]);
        await using var runtime = new ModuleRuntime(catalog, time);
        var leases = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => runtime.AcquireAsync("lazy", "op-" + i)));
        Assert.Equal(1, starts);
        time.Advance(10);
        await runtime.CollectIdleAsync();
        Assert.Equal(0, resource.Disposals);
        foreach (var lease in leases) await lease.DisposeAsync();
        time.Advance(4);
        await runtime.CollectIdleAsync();
        Assert.Equal(0, resource.Disposals);
        time.Advance(1);
        await runtime.CollectIdleAsync();
        Assert.Equal(1, resource.Disposals);
        await leases[0].DisposeAsync();
        await using var next = await runtime.AcquireAsync("lazy", "new");
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task InUseDependencyCannotBeDisabledAndCapacityIsBounded()
    {
        var catalog = new ModuleCatalog(new(1, 0), [
            Register("capture", capacity: 1),
            Register("combat", dependencies: [new("capture", "0.1.0")])]);
        await using var runtime = new ModuleRuntime(catalog);
        var lease = await runtime.AcquireAsync("combat", "encounter-1");
        var conflict = await Assert.ThrowsAsync<ModuleRuntimeException>(() => runtime.SetEnabledAsync("capture", false));
        Assert.Equal("module.inUse", conflict.Code);
        Assert.Equal(["encounter-1"], conflict.OperationIds);
        var capacity = await Assert.ThrowsAsync<ModuleRuntimeException>(() => runtime.AcquireAsync("capture", "other"));
        Assert.Equal("module.capacityExceeded", capacity.Code);
        await lease.DisposeAsync();
        await runtime.SetEnabledAsync("capture", false);
        var disabled = await Assert.ThrowsAsync<ModuleRuntimeException>(() => runtime.AcquireAsync("combat", "new"));
        Assert.Equal("module.dependencyDisabled", disabled.Code);
        await runtime.SetEnabledAsync("capture", true);
        await using var next = await runtime.AcquireAsync("combat", "new");
    }

    [Fact]
    public async Task CycleIsUnavailableAndStartupFailureRequiresExplicitReset()
    {
        var catalog = new ModuleCatalog(new(1, 0), [
            Register("a", dependencies: [new("b", "0.1.0")]),
            Register("b", dependencies: [new("a", "0.1.0")]),
            Register("broken", _ => ValueTask.FromException<IAsyncDisposable>(new IOException("test")))]);
        await using var runtime = new ModuleRuntime(catalog);
        var cycle = await Assert.ThrowsAsync<ModuleRuntimeException>(() => runtime.AcquireAsync("a", "test"));
        Assert.Equal("module.dependencyCycle", cycle.Code);
        await Assert.ThrowsAsync<IOException>(() => runtime.AcquireAsync("broken", "test"));
        var snapshot = await runtime.SnapshotAsync();
        Assert.Contains(snapshot.Items.Single(x => x.Id == "broken").Problems, x => x.Code == "module.startFailed");
        await runtime.SetEnabledAsync("broken", false);
        await runtime.SetEnabledAsync("broken", true);
        await Assert.ThrowsAsync<IOException>(() => runtime.AcquireAsync("broken", "new"));
    }

    [Fact]
    public async Task MultipleModulesUseOneSharedDependencyReservationAndReleaseTogether()
    {
        var time = new ManualClock();
        var resource = new Resource();
        var catalog = new ModuleCatalog(new(1, 0), [
            Register("shared", _ => ValueTask.FromResult<IAsyncDisposable>(resource), capacity: 1),
            Register("left", dependencies: [new("shared", "0.1.0")], capacity: 1),
            Register("right", dependencies: [new("shared", "0.1.0")], capacity: 1)]);
        await using var runtime = new ModuleRuntime(catalog, time);
        var demand = await runtime.AcquireManyAsync(["left", "right", "shared", "left"], "health");
        Assert.All((await runtime.SnapshotAsync()).Items, item => Assert.Equal(["health"], item.ActiveOperationIds));
        var rejected = await Assert.ThrowsAsync<ModuleRuntimeException>(() => runtime.AcquireAsync("shared", "other"));
        Assert.Equal("module.capacityExceeded", rejected.Code);
        await demand.DisposeAsync();
        Assert.All((await runtime.SnapshotAsync()).Items, item => Assert.Empty(item.ActiveOperationIds));
        time.Advance(5); await runtime.CollectIdleAsync();
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public async Task CancelledWaitCannotCreateDemand()
    {
        await using var runtime = new ModuleRuntime(new(new(1, 0), [Register("lazy")]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.AcquireAsync("lazy", "cancelled", cancelled.Token));
        Assert.Empty((await runtime.SnapshotAsync()).Items[0].ActiveOperationIds);
    }

    [Fact]
    public async Task CancellationAfterFactoryDoesNotPublishDemandAndStillReclaimsResources()
    {
        using var cancellation = new CancellationTokenSource();
        var resource = new Resource();
        var time = new ManualClock();
        await using var runtime = new ModuleRuntime(new(new(1, 0), [Register("lazy", _ =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult<IAsyncDisposable>(resource);
        })]), time);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.AcquireAsync("lazy", "cancelled", cancellation.Token));
        Assert.Empty((await runtime.SnapshotAsync()).Items[0].ActiveOperationIds);
        time.Advance(5);
        await runtime.CollectIdleAsync();
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public async Task FailedDependencyStartupRollsBackDemandAndShutdownUsesDependencyOrder()
    {
        var stopped = new List<string>();
        var runtime = new ModuleRuntime(new(new(1, 0), [
            Register("consumer", _ => ValueTask.FromResult<IAsyncDisposable>(new CallbackResource(() => stopped.Add("consumer"))),
                dependencies: [new("base", "0.1.0")]),
            Register("base", _ => ValueTask.FromResult<IAsyncDisposable>(new CallbackResource(() => stopped.Add("base")))),
            Register("broken", _ => ValueTask.FromException<IAsyncDisposable>(new IOException()),
                dependencies: [new("base", "0.1.0")])
        ]));
        await Assert.ThrowsAsync<IOException>(() => runtime.AcquireAsync("broken", "failed"));
        Assert.All((await runtime.SnapshotAsync()).Items, x => Assert.Empty(x.ActiveOperationIds));
        var lease = await runtime.AcquireAsync("consumer", "active");
        await runtime.DisposeAsync();
        Assert.Equal(["consumer", "base"], stopped);
        await lease.DisposeAsync();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task DependencyStartupFailureIsVisibleToConsumersUntilReset()
    {
        var attempts = 0;
        await using var runtime = new ModuleRuntime(new(new(1, 0), [
            Register("base", _ =>
            {
                attempts++;
                return ValueTask.FromException<IAsyncDisposable>(new IOException());
            }),
            Register("consumer", dependencies: [new("base", "0.1.0")])
        ]));
        await Assert.ThrowsAsync<IOException>(() => runtime.AcquireAsync("consumer", "first"));
        var blocked = await Assert.ThrowsAsync<ModuleRuntimeException>(() => runtime.AcquireAsync("consumer", "second"));
        Assert.Equal("module.dependencyFailed", blocked.Code);
        Assert.Equal(1, attempts);
        var snapshot = await runtime.SnapshotAsync();
        Assert.Equal("unavailable", snapshot.Items.Single(x => x.Id == "consumer").State);
    }

    private sealed class CallbackResource(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { onDispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Resource : IAsyncDisposable
    {
        public int Disposals;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public void Advance(int seconds) => timestamp += seconds * 1000L;
    }
}
