using MabiTool.Infrastructure;

namespace MabiTool.Host.Tests;

internal sealed class StorageTestWorkspace : IAsyncDisposable
{
    public static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("缺少仓库根");
        }
    }
    public string Root { get; } = Path.Combine(Repository, "artifacts", "tmp", "storage-" + Guid.NewGuid().ToString("N"));
    public StorageClock Clock { get; } = new();
    public VolumeSpaceReservations Space { get; } = new(new StorageSpaceTests.Volumes(1024 * 1024 * 1024));
    public DataRootLease Lease { get; }
    public SqliteStore Database { get; private set; }
    public AssetStore Assets { get; private set; }
    public PhysicalStorageMaintenance Files { get; private set; }
    public PathService Paths => Lease.Paths;
    public long CacheBudget { get; set; }
    public StorageTestWorkspace(long cacheBudget = 0)
    {
        CacheBudget = cacheBudget;
        Directory.CreateDirectory(Root);
        Lease = DataRootLease.Open(new(Path.Combine(Root, "bootstrap"), true, DataRoot: Path.Combine(Root, "data")));
        Database = Open(); Assets = new(Database, Paths, Clock); Files = new(Database, Paths);
    }
    private SqliteStore Open() => SqliteStore.Open(Lease,
        [.. AssetStore.Migrations, TemporaryStorageRegistry.Migration, StorageControlStore.Migration, DataBackupStore.Migration], spaceReservations: Space, clock: Clock);
    public StorageControlStore Control(string epoch = "storage-test", params IStorageMaintenanceParticipant[] participants) => new(Database, Paths,
        participants.Length == 0 ? [Assets, Files, new DataBackupStore(Database, Paths)] : participants, epoch, Clock, () => new Dictionary<string, long>
        { ["cache"] = CacheBudget, ["logs"] = 50 * 1024 * 1024, ["diagnostics"] = 1024 * 1024 * 1024 });
    public string Cache(string name, string content)
    {
        var path = Paths.Resolve("cache", name); File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Clock.GetUtcNow().AddDays(-2).UtcDateTime);
        File.SetLastAccessTimeUtc(path, Clock.GetUtcNow().AddDays(-2).UtcDateTime);
        return path;
    }
    public async Task RestartAsync()
    {
        await Assets.DisposeAsync(); await Database.DisposeAsync();
        Database = Open(); Assets = new(Database, Paths, Clock); Files = new(Database, Paths);
        await Database.TemporaryFiles.RecoverAsync();
    }
    public async ValueTask DisposeAsync()
    {
        await Assets.DisposeAsync(); await Database.DisposeAsync(); Lease.Dispose();
        var allowed = Path.GetFullPath(Path.Combine(Repository, "artifacts", "tmp")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("临时工作区越界");
        Directory.Delete(Root, true);
    }
}
internal sealed class StorageClock : TimeProvider
{
    private DateTimeOffset utc = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private long timestamp;
    public override DateTimeOffset GetUtcNow() => utc;
    public override long GetTimestamp() => timestamp;
    public override long TimestampFrequency => 1000;
    public void Advance(TimeSpan span) { utc += span; timestamp += (long)span.TotalMilliseconds; }
    public void SetUtc(DateTimeOffset value) => utc = value;
}
