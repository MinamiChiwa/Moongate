using MabiTool.Infrastructure;

namespace MabiTool.Host.Color;

/// <summary>在运行锁内使用单调时间维护交互主体；读取状态不续约。</summary>
internal sealed class ColorInteractionPresenceTracker(TimeProvider clock)
{
    public const int LifetimeMs = 5000;
    private readonly Dictionary<string, long> clients = new(StringComparer.Ordinal);

    public void Begin(string principal) => clients[principal] = clock.GetTimestamp();

    public bool HasLiveClient(Func<string, bool> authorized)
    {
        Prune(authorized);
        return clients.Count != 0;
    }

    public void Renew(string principal, Func<string, bool> authorized)
    {
        Prune(authorized);
        if (clients.Count == 0) throw new OperationException("color.runInactive", "交互租约已过期");
        if (!clients.ContainsKey(principal) && clients.Count >= 8)
            throw new OperationException("resource.capacityExceeded", "交互续约主体最多八个");
        clients[principal] = clock.GetTimestamp();
    }

    private void Prune(Func<string, bool> authorized)
    {
        foreach (var entry in clients.ToArray())
            if (clock.GetElapsedTime(entry.Value) >= TimeSpan.FromMilliseconds(LifetimeMs) || !authorized(entry.Key))
                clients.Remove(entry.Key);
    }
}
