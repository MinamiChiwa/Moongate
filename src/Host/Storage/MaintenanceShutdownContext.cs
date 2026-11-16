using MabiTool.ModuleSdk;

namespace MabiTool.Host.Storage;

/// <summary>固定维护意图后，将同一有效租约传给宿主的停机收尾服务。</summary>
public sealed class MaintenanceShutdownContext
{
    private ActivityBarrier.MaintenanceLease? lease;
    public ActivityBarrier.MaintenanceLease? Lease => Volatile.Read(ref lease);
    internal void Publish(ActivityBarrier.MaintenanceLease value) => Volatile.Write(ref lease, value);
}
