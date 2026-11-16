using MabiTool.Contracts;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;

namespace MabiTool.Host.Storage;

/// <summary>受信原生桥登记用户选定的空目录；HTTP 使用主体绑定的短期选择 ID。</summary>
public sealed class StorageDirectorySelectionRegistry(LocalAuthService auth, PathService paths, TimeProvider clock) : BackgroundService
{
    private sealed record Selection(StorageDirectorySelectionSnapshot Snapshot, StorageDirectoryEvidence Evidence, long At, IDisposable Handle);
    private readonly Dictionary<string, Selection> selections = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private bool stopped;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public StorageDirectorySelectionSnapshot Register(AuthSession session, string selectedDirectory)
    {
        Native(session);
        var evidence = StorageDirectoryEvidence.Capture(selectedDirectory);
        if (StoragePathsWithin(evidence.Path, paths.DataRoot) || StoragePathsWithin(evidence.Path, paths.BootstrapDirectory))
            throw new OperationException("storage.pathRejected", "迁移目录须与当前数据根及引导目录独立");
        var handle = evidence.Hold();
        try
        {
            evidence.Verify();
            lock (gate)
            {
                Prune();
                if (stopped) throw new OperationException("storage.unavailable", "目录选择注册器正在关闭");
                if (selections.Count >= 32) throw new OperationException("resource.capacityExceeded", "目录选择数量达到上限");
                var snapshot = new StorageDirectorySelectionSnapshot(Guid.NewGuid().ToString("N"), session.ClientId, Path.GetFileName(evidence.Path), clock.GetUtcNow() + Lifetime);
                selections.Add(snapshot.Id, new(snapshot, evidence, clock.GetTimestamp(), handle));
                handle = null!; return snapshot;
            }
        }
        finally { handle?.Dispose(); }
    }
    public StorageDirectorySelectionSnapshot Get(string id, AuthSession session) => Find(id, session).Snapshot;
    internal StorageDirectoryEvidence Resolve(string id, AuthSession session) => Find(id, session).Evidence;
    private Selection Find(string id, AuthSession session)
    {
        Native(session);
        lock (gate)
        {
            if (stopped || !selections.TryGetValue(id, out var value) || value.Snapshot.Owner != session.ClientId) throw new OperationException("resource.notFound", "目录选择不存在");
            if (clock.GetElapsedTime(value.At) >= Lifetime) throw new OperationException("storage.selectionExpired", "目录选择已到期");
            value.Evidence.Verify(); return value;
        }
    }
    private void Native(AuthSession session)
    {
        if (!session.Owner || session.Origin is not null || !auth.HasPermission(session.ClientId, ApiPermissions.StorageManage))
            throw new OperationException("storage.scopeDenied", "迁移目录须由已授权宿主所有者的原生桌面桥登记");
    }
    private static bool StoragePathsWithin(string first, string second) =>
        first.Equals(second, StringComparison.OrdinalIgnoreCase) || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private void Prune()
    {
        foreach (var entry in selections.Values.Where(x => clock.GetElapsedTime(x.At) >= Lifetime || !auth.HasPermission(x.Snapshot.Owner, ApiPermissions.StorageManage)).ToArray())
        { selections.Remove(entry.Snapshot.Id); entry.Handle.Dispose(); }
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        while (await timer.WaitForNextTickAsync(ct)) lock (gate) Prune();
    }
    public override Task StopAsync(CancellationToken ct)
    {
        lock (gate) { stopped = true; foreach (var entry in selections.Values) entry.Handle.Dispose(); selections.Clear(); }
        return base.StopAsync(ct);
    }
}
