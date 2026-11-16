using MabiTool.Infrastructure;
using Xunit;

namespace MabiTool.Host.Tests;

[Collection("Persistent backend")]
public sealed class StorageSpaceTests
{
    [Fact]
    public void SameVolumeRequestsAggregateAndReleaseOnce()
    {
        var pool = new VolumeSpaceReservations(new Volumes(1000));
        using var first = pool.Reserve("import", new VolumeSpaceRequirement(@"D:\imports", 600));
        Assert.Equal("storage.diskSpaceInsufficient", Assert.Throws<StorageException>(() =>
            pool.Reserve("download", new VolumeSpaceRequirement(@"D:\cache", 401))).Code);
        using var second = pool.Reserve("backup", new VolumeSpaceRequirement(@"D:\backups", 400));
        var snapshot = Assert.Single(pool.Snapshot(@"D:\data"));
        Assert.Equal(1000UL, snapshot.ReservedBytes.Value); Assert.Equal(2, snapshot.Reservations);
        second.Dispose(); second.Dispose();
        Assert.Equal(600UL, Assert.Single(pool.Snapshot(@"D:\data")).ReservedBytes.Value);
        Assert.True(pool.Protects(@"D:\imports\active.part")); Assert.False(pool.Protects(@"D:\unrelated"));
    }

    [Fact]
    public void FailedMultiVolumeAdmissionLeavesEveryVolumeAvailable()
    {
        var pool = new VolumeSpaceReservations(new Volumes(1000));
        using var existing = pool.Reserve("backup", new VolumeSpaceRequirement(@"C:\backup", 900));
        Assert.Equal("storage.diskSpaceInsufficient", Assert.Throws<StorageException>(() => pool.Reserve("migration",
            new VolumeSpaceRequirement(@"D:\target", 800), new VolumeSpaceRequirement(@"C:\temporary", 200))).Code);
        using var other = pool.Reserve("download", new VolumeSpaceRequirement(@"D:\target", 1000));
        Assert.Equal(900UL, pool.Snapshot(@"C:\backup").Single(x => x.VolumeId == "C:").ReservedBytes.Value);
        Assert.Equal(1000UL, pool.Snapshot(@"D:\target").Single(x => x.VolumeId == "D:").ReservedBytes.Value);
    }

    [Fact]
    public void ConcurrentImportsCannotIndividuallyPassTheSameFreeSpaceCheck()
    {
        var pool = new VolumeSpaceReservations(new Volumes(1000));
        var held = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
        var rejected = 0;
        Parallel.For(0, 16, _ =>
        {
            try { held.Add(pool.Reserve("import", new VolumeSpaceRequirement(@"D:\data", 100))); }
            catch (StorageException error) when (error.Code == "storage.diskSpaceInsufficient") { Interlocked.Increment(ref rejected); }
        });
        Assert.Equal(10, held.Count); Assert.Equal(6, rejected);
        foreach (var lease in held) lease.Dispose();
        Assert.Equal(0UL, Assert.Single(pool.Snapshot(@"D:\data")).ReservedBytes.Value);
    }

    [Fact]
    public void LowerSourceLimitAndChangingPhysicalFreeSpaceBothApply()
    {
        var volumes = new Volumes(1000); var pool = new VolumeSpaceReservations(volumes);
        Assert.Equal("storage.diskSpaceInsufficient", Assert.Throws<StorageException>(() =>
            pool.Reserve("download", new VolumeSpaceRequirement(@"D:\cache", 501, 500))).Code);
        using var first = pool.Reserve("import", new VolumeSpaceRequirement(@"D:\data", 400));
        volumes.Free = 500;
        Assert.Equal("storage.diskSpaceInsufficient", Assert.Throws<StorageException>(() =>
            pool.Reserve("download", new VolumeSpaceRequirement(@"D:\data", 101))).Code);
        using var second = pool.Reserve("download", new VolumeSpaceRequirement(@"D:\data", 100));
    }

    [Fact]
    public void UnavailableSecondVolumeDoesNotClaimFirstVolume()
    {
        var source = new Volumes(1000) { Missing = "C:" }; var pool = new VolumeSpaceReservations(source);
        Assert.Equal("storage.volumeUnavailable", Assert.Throws<StorageException>(() => pool.Reserve("migration",
            new VolumeSpaceRequirement(@"D:\target", 500), new VolumeSpaceRequirement(@"C:\backup", 100))).Code);
        using var admitted = pool.Reserve("import", new VolumeSpaceRequirement(@"D:\target", 1000));
    }

    internal sealed class Volumes(long free) : IStorageVolumeSource
    {
        public long Free = free;
        public string? Missing;
        public StorageVolumeState Read(string path)
        {
            var id = path[..2].ToUpperInvariant();
            if (id == Missing) throw new IOException("removed volume");
            return new(id, Free);
        }
    }
}
