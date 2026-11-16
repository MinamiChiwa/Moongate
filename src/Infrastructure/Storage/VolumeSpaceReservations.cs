using System.Security.Cryptography;
using System.Text;
using MabiTool.Contracts;

namespace MabiTool.Infrastructure;

public sealed record StorageVolumeState(string Id, long AvailableBytes);
public interface IStorageVolumeSource
{
    StorageVolumeState Read(string path);
}
public sealed record VolumeSpaceRequirement(string Path, long PeakAdditionalBytes, long? AvailableLimit = null);

/// <summary>同一卷的峰值空间统一准入；多卷请求全部成功后才建立预留。</summary>
public sealed class VolumeSpaceReservations(IStorageVolumeSource? source = null)
{
    public static VolumeSpaceReservations Shared { get; } = new();
    public const int MaximumReservations = 4096;
    private readonly IStorageVolumeSource volumes = source ?? new LocalStorageVolumeSource();
    private readonly object gate = new();
    private readonly Dictionary<string, Reservation> reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> reserved = new(StringComparer.Ordinal);
    private sealed record Reservation(string Owner, IReadOnlyList<FixedRequirement> Requirements);
    private sealed record FixedRequirement(string Path, string VolumeId, long Bytes);


    public SpaceReservation Reserve(string owner, params VolumeSpaceRequirement[] requirements)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 || owner.Any(char.IsControl) ||
            requirements is not { Length: > 0 and <= 32 } || requirements.Any(x => x is null ||
                !System.IO.Path.IsPathFullyQualified(x.Path) || x.PeakAdditionalBytes < 0 || x.AvailableLimit < 0))
            throw new StorageException("storage.reservationInvalid", "空间预留主体、路径或峰值预算无效");
        lock (gate)
        {
            if (reservations.Count >= MaximumReservations)
                throw new StorageException("resource.capacityExceeded", "并发空间预留数量达到上限");
            var fixedItems = new List<FixedRequirement>();
            var demands = new Dictionary<string, (long Bytes, long Available)>(StringComparer.Ordinal);
            try
            {
                foreach (var requirement in requirements)
                {
                    var volume = volumes.Read(requirement.Path);
                    if (string.IsNullOrWhiteSpace(volume.Id) || volume.AvailableBytes < 0)
                        throw new StorageException("storage.volumeUnavailable", "目标卷不可访问或空间状态无效");
                    var available = Math.Min(volume.AvailableBytes, requirement.AvailableLimit ?? long.MaxValue);
                    var previous = demands.GetValueOrDefault(volume.Id, (0, long.MaxValue));
                    demands[volume.Id] = (checked(previous.Bytes + requirement.PeakAdditionalBytes), Math.Min(previous.Available, available));
                    fixedItems.Add(new(System.IO.Path.GetFullPath(requirement.Path), volume.Id, requirement.PeakAdditionalBytes));
                }
                foreach (var (id, demand) in demands)
                    if (demand.Bytes > demand.Available - reserved.GetValueOrDefault(id))
                        throw new StorageException("storage.diskSpaceInsufficient", "目标卷剩余空间扣除活动预留后不足以容纳本次峰值写入");
                var key = Guid.NewGuid().ToString("N");
                foreach (var (id, demand) in demands) reserved[id] = checked(reserved.GetValueOrDefault(id) + demand.Bytes);
                reservations.Add(key, new(owner, fixedItems));
                return new(this, key);
            }
            catch (OverflowException error) { throw new StorageException("storage.reservationInvalid", "空间预留总量超出数值范围", error); }
            catch (Exception error) when (error is not StorageException && error is IOException or UnauthorizedAccessException)
            { throw new StorageException("storage.volumeUnavailable", "目标卷在空间准入时不可访问", error); }
        }
    }

    public IReadOnlyList<StorageVolumeUsage> Snapshot(params string[] paths)
    {
        lock (gate)
        {
            var known = paths.Concat(reservations.Values.SelectMany(x => x.Requirements.Select(r => r.Path)))
                .Select(volumes.Read).GroupBy(x => x.Id, StringComparer.Ordinal);
            return known.Select(items => new StorageVolumeUsage(items.Key,
                new((ulong)Math.Max(0, items.Min(x => x.AvailableBytes))), new((ulong)reserved.GetValueOrDefault(items.Key)),
                reservations.Values.Count(x => x.Requirements.Any(r => r.VolumeId == items.Key)))).ToArray();
        }
    }

    public bool Protects(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        lock (gate) return reservations.Values.SelectMany(x => x.Requirements).Any(x =>
            StoragePaths.IsWithin(full, x.Path) || StoragePaths.IsWithin(x.Path, full));
    }

    private void Release(string key)
    {
        lock (gate)
        {
            if (!reservations.Remove(key, out var reservation)) return;
            foreach (var item in reservation.Requirements)
            {
                reserved[item.VolumeId] -= item.Bytes;
                if (reserved[item.VolumeId] == 0) reserved.Remove(item.VolumeId);
            }
        }
    }

    public sealed class SpaceReservation : IDisposable
    {
        private VolumeSpaceReservations? owner;
        private readonly string key;
        internal SpaceReservation(VolumeSpaceReservations owner, string key) { this.owner = owner; this.key = key; }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(key);
    }

    private sealed class LocalStorageVolumeSource : IStorageVolumeSource
    {
        public StorageVolumeState Read(string path)
        {
            var full = StoragePaths.ValidateLocal(path);
            var root = System.IO.Path.GetPathRoot(full)!;
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16];
            return new(id, new DriveInfo(root).AvailableFreeSpace);
        }
    }
}
