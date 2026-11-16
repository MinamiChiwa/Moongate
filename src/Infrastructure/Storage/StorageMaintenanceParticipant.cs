using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed record StorageManagedItem(string Key, string Category, string Module, long Bytes, string Revision,
    DateTimeOffset LastUsedAt, bool Eligible, string CleanupReason, IReadOnlyList<string> ProtectionReasons,
    string? ContentHash = null);
public sealed record StorageDeletion(bool Deleted, long ReclaimedBytes, string? Reason = null);

/// <summary>各存储所有者提供占用与条件删除；平台用不透明候选 ID 建立清理计划。</summary>
public interface IStorageMaintenanceParticipant
{
    string Id { get; }
    IReadOnlyList<string> VolumeRoots => [];
    Task<IReadOnlyList<StorageManagedItem>> InspectAsync(CancellationToken ct = default);
    Task<StorageManagedItem> FreezeAsync(StorageManagedItem item, CancellationToken ct = default) => Task.FromResult(item);
    Task<StorageDeletion> DeleteAsync(StorageManagedItem expected, ActivityBarrier.MaintenanceLease maintenance,
        CancellationToken ct = default);
}
