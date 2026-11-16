namespace MabiTool.ModuleSdk;

public sealed record ActivitySnapshot(string Id, string Owner, string Kind);
public sealed record ActivityBarrierSnapshot(string? MaintenanceOwner, IReadOnlyList<ActivitySnapshot> Activities);

public sealed class ActivityBarrierException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>活动登记和维护准入使用同一把锁；等待安装不会占用维护权。</summary>
public sealed class ActivityBarrier
{
    private const int MaximumActivities = 4096;
    private readonly object gate = new();
    private readonly Dictionary<string, ActivitySnapshot> activities = new(StringComparer.Ordinal);
    private MaintenanceLease? maintenance;
    private bool maintenanceClosing;

    public IDisposable EnterActivity(string owner, string kind)
    {
        Validate(owner, nameof(owner));
        Validate(kind, nameof(kind));
        lock (gate)
        {
            if (maintenance is not null) throw new ActivityBarrierException("update.maintenanceActive");
            return Register(owner, kind);
        }
    }

    public MaintenanceLease? TryEnterMaintenance(string owner)
    {
        Validate(owner, nameof(owner));
        lock (gate)
        {
            if (maintenance is not null || activities.Count != 0) return null;
            return maintenance = new MaintenanceLease(this, owner);
        }
    }

    public IDisposable EnterMaintenanceActivity(MaintenanceLease lease, string kind)
    {
        Validate(kind, nameof(kind));
        lock (gate)
        {
            if (!ReferenceEquals(maintenance, lease) || lease is null || lease.IsDisposed || maintenanceClosing)
                throw new ActivityBarrierException("update.maintenanceLeaseInvalid");
            return Register(lease.Owner, kind);
        }
    }

    private IDisposable Register(string owner, string kind)
    {
        if (activities.Count >= MaximumActivities) throw new ActivityBarrierException("resource.capacityExceeded");
        var id = Guid.NewGuid().ToString("N");
        activities.Add(id, new(id, owner, kind));
        return new Registration(() =>
        {
            lock (gate)
            {
                activities.Remove(id);
                FinishMaintenanceIfDrained();
            }
        });
    }

    private void ReleaseMaintenance(MaintenanceLease lease)
    {
        lock (gate)
        {
            if (!ReferenceEquals(maintenance, lease)) return;
            maintenanceClosing = true;
            FinishMaintenanceIfDrained();
        }
    }

    private void FinishMaintenanceIfDrained()
    {
        if (maintenanceClosing && activities.Count == 0)
        {
            maintenance = null;
            maintenanceClosing = false;
        }
    }

    public ActivityBarrierSnapshot Snapshot()
    {
        lock (gate) return new(maintenance?.Owner, activities.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    private static void Validate(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("活动主体及类型须为 1–256 字符且不能包含控制字符", parameter);
    }

    private sealed class Registration(Action release) : IDisposable
    {
        private Action? releaseOnce = release;
        public void Dispose() => Interlocked.Exchange(ref releaseOnce, null)?.Invoke();
    }

    public sealed class MaintenanceLease : IDisposable
    {
        private readonly ActivityBarrier barrier;
        private int disposed;
        public string Owner { get; }
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
        internal MaintenanceLease(ActivityBarrier barrier, string owner) { this.barrier = barrier; Owner = owner; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) barrier.ReleaseMaintenance(this);
        }
    }
}
