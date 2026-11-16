using MabiTool.Contracts;

namespace MabiTool.ModuleSdk;

/// <summary>串行管理生命周期；工厂和释放方法必须及时响应取消，不运行游戏业务。</summary>
public sealed class ModuleRuntime : IAsyncDisposable
{
    private sealed class Entry(bool enabled)
    {
        public bool Enabled = enabled;
        public IAsyncDisposable? Instance;
        public readonly Dictionary<string, string> Demands = new(StringComparer.Ordinal);
        public long IdleAt;
        public bool HasIdleTimestamp;
        public string? Failure;
    }

    private readonly ModuleCatalog catalog;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Entry> entries;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> activeOperations = new Dictionary<string, IReadOnlyList<string>>();
    private long revision;
    private bool disposed;

    public IReadOnlyList<string> ActiveOperations(string id) => Volatile.Read(ref activeOperations).TryGetValue(id, out var values) ? values : [];

    private void PublishActiveOperations() => Volatile.Write(ref activeOperations,
        entries.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)Array.AsReadOnly(x.Value.Demands.Values.Distinct().ToArray()), StringComparer.Ordinal));

    public ModuleRuntime(ModuleCatalog catalog, TimeProvider? timeProvider = null)
    {
        this.catalog = catalog;
        time = timeProvider ?? TimeProvider.System;
        entries = catalog.Registrations.ToDictionary(x => x.Key,
            x => new Entry(x.Value.Definition.EnabledByDefault), StringComparer.Ordinal);
    }

    public async Task<(IReadOnlyList<ModuleSnapshot> Items, string Revision)> SnapshotAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = catalog.Registrations.OrderBy(x => x.Key, StringComparer.Ordinal).Select(pair =>
            {
                var d = pair.Value.Definition;
                var e = entries[d.Id];
                var problems = Problems(d.Id);
                var state = !e.Enabled ? "disabled" : problems.Count > 0 ? "unavailable" :
                    e.Instance is not null ? "running" : "idle";
                return new ModuleSnapshot(d.Id, d.Version.ToString(), d.MinimumHostApi.ToString(),
                    d.MaximumHostApiExclusive.ToString(), d.Dependencies, e.Enabled, state, problems,
                    e.Demands.Values.Distinct().Order(StringComparer.Ordinal).ToArray(),
                    d.MaximumConcurrentDemands, d.IdleRetentionSeconds, d.SettingsSchema, d.Risk, d.DisplayNames, d.Descriptions, d.Essential);
            }).ToArray();
            return (result, revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { gate.Release(); }
    }

    public Task<IAsyncDisposable> AcquireAsync(string id, string operationId, CancellationToken ct = default) =>
        AcquireManyAsync([id], operationId, ct);

    /// <summary>一次操作对多个模块的需求共用依赖，准入及释放覆盖整个依赖闭包。</summary>
    public async Task<IAsyncDisposable> AcquireManyAsync(IReadOnlyCollection<string> moduleIds, string operationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(moduleIds);
        if (moduleIds.Count == 0) throw new ArgumentException("模块需求不能为空", nameof(moduleIds));
        var requested = moduleIds.Distinct(StringComparer.Ordinal).ToArray();
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in requested)
            {
                if (id is null || !entries.ContainsKey(id)) throw new ModuleRuntimeException("module.notFound", "模块不存在");
                var problems = Problems(id);
                if (problems.Count > 0) throw new ModuleRuntimeException(problems[0].Code, problems[0].Message);
                foreach (var dependency in DependencyOrder(id))
                    if (seen.Add(dependency)) ids.Add(dependency);
            }
            foreach (var moduleId in ids)
            {
                var e = entries[moduleId];
                if (!e.Enabled) throw new ModuleRuntimeException("module.disabled", "模块未启用：" + moduleId);
                if (e.Demands.Count >= catalog.Registrations[moduleId].Definition.MaximumConcurrentDemands)
                    throw new ModuleRuntimeException("module.capacityExceeded", "模块并发需求达到上限：" + moduleId);
            }
            var started = new List<string>();
            try
            {
                foreach (var moduleId in ids)
                {
                    ct.ThrowIfCancellationRequested();
                    var e = entries[moduleId];
                    if (e.Instance is not null) continue;
                    try
                    {
                        e.Instance = await catalog.Registrations[moduleId].Factory(ct)
                            ?? throw new InvalidOperationException("模块工厂未返回实例");
                        e.Failure = null;
                        started.Add(moduleId);
                        revision++;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch
                    {
                        e.Failure = "module.startFailed";
                        revision++;
                        throw;
                    }
                }
                ct.ThrowIfCancellationRequested();
            }
            catch
            {
                // 部分依赖已启动时进入空闲回收，保留仍被其他需求引用的实例。
                foreach (var moduleId in started) MarkIdle(entries[moduleId]);
                throw;
            }
            var leaseId = Guid.NewGuid().ToString("N");
            foreach (var moduleId in ids) entries[moduleId].Demands.Add(leaseId, operationId);
            PublishActiveOperations();
            revision++;
            return new Demand(this, ids, leaseId);
        }
        finally { gate.Release(); }
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(id, out var e)) throw new ModuleRuntimeException("module.notFound", "模块不存在");
            if (e.Enabled == enabled) return;
            if (!enabled && e.Demands.Count > 0)
                throw new ModuleRuntimeException("module.inUse", "模块仍有活动操作",
                    e.Demands.Values.Distinct().ToArray());
            if (!enabled && e.Instance is not null) await StopAsync(e);
            e.Enabled = enabled;
            e.Failure = null;
            revision++;
        }
        finally { gate.Release(); }
    }

    public async Task CollectIdleAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var id in catalog.Registrations.Keys)
            {
                var e = entries[id];
                if (e.Instance is not null && e.Demands.Count == 0 && e.HasIdleTimestamp &&
                    time.GetElapsedTime(e.IdleAt) >= TimeSpan.FromSeconds(catalog.Registrations[id].Definition.IdleRetentionSeconds))
                {
                    await StopAsync(e);
                    revision++;
                }
            }
        }
        finally { gate.Release(); }
    }

    private List<ModuleProblem> Problems(string id)
    {
        var problems = catalog.GetProblems(id).ToList();
        if (entries[id].Failure is { } failure) problems.Add(new(failure, "上次模块启动失败；重新启用后可重试"));
        foreach (var dependencyId in DependencyOrderSafe(id))
        {
            if (!entries[dependencyId].Enabled) problems.Add(new("module.dependencyDisabled", "依赖未启用", dependencyId));
            if (entries[dependencyId].Failure is not null)
                problems.Add(new("module.dependencyFailed", "依赖启动失败；重新启用依赖后可重试", dependencyId));
        }
        return problems;
    }

    private IEnumerable<string> DependencyOrderSafe(string id) =>
        catalog.GetProblems(id).Count == 0 ? DependencyOrder(id).Where(x => x != id) : [];

    private List<string> DependencyOrder(string id)
    {
        var order = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string current)
        {
            if (!visited.Add(current)) return;
            foreach (var dependency in catalog.Registrations[current].Definition.Dependencies) Visit(dependency.Id);
            order.Add(current);
        }
        Visit(id);
        return order;
    }

    private void MarkIdle(Entry e)
    {
        if (e.Demands.Count > 0) return;
        e.IdleAt = time.GetTimestamp();
        e.HasIdleTimestamp = true;
    }

    private async ValueTask ReleaseAsync(IReadOnlyList<string> ids, string leaseId)
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            foreach (var id in ids)
            {
                entries[id].Demands.Remove(leaseId);
                MarkIdle(entries[id]);
            }
            PublishActiveOperations();
            revision++;
        }
        finally { gate.Release(); }
    }

    private static async ValueTask StopAsync(Entry e)
    {
        // 释放失败时保留实例引用，后续回收或关闭可继续清理。
        await e.Instance!.DisposeAsync();
        e.Instance = null;
        e.HasIdleTimestamp = false;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            List<Exception> failures = [];
            // 正常依赖图按逆拓扑释放；不可用的循环图不会产生实例。
            var stopOrder = catalog.Registrations.Keys
                .Where(id => catalog.GetProblems(id).Count == 0)
                .SelectMany(DependencyOrder).Distinct().Reverse();
            foreach (var id in stopOrder)
                if (entries[id].Instance is not null)
                    try { await StopAsync(entries[id]); }
                    catch (Exception error) { failures.Add(error); }
            disposed = failures.Count == 0;
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { gate.Release(); }
    }

    private sealed class Demand(ModuleRuntime owner, IReadOnlyList<string> ids, string leaseId) : IAsyncDisposable
    {
        private ModuleRuntime? runtime = owner;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref runtime, null)?.ReleaseAsync(ids, leaseId)
            ?? ValueTask.CompletedTask;
    }
}
