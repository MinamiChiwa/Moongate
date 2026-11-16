using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed class PhysicalStorageMaintenance : IStorageMaintenanceParticipant
{
    private sealed record ManagedRoot(string Path, string Category, string Module, bool Rebuildable);
    private readonly SqliteStore database;
    private readonly IReadOnlyList<ManagedRoot> roots;
    public string Id => "files";
    public IReadOnlyList<string> VolumeRoots => roots.Select(x => x.Path).ToArray();
    public PhysicalStorageMaintenance(SqliteStore database, PathService paths, string? updateCache = null)
    {
        this.database = database;
        roots = new ManagedRoot[]
        {
            new(paths.Cache, "cache", "platform", true), new(paths.Temporary, "temporary", "platform", false),
            new(paths.Settings, "user", "settings", false), new(paths.Resolve("store"), "database", "core", false),
            new(paths.Recovery, "recovery", "core", false)
        }.Concat(updateCache is null ? [] : new[] { new ManagedRoot(StoragePaths.ValidateLocal(updateCache), "cache", "updates", true) }).ToArray();
        for (var index = 0; index < roots.Count; index++)
            foreach (var other in roots.Skip(index + 1))
                if (StoragePaths.IsWithin(roots[index].Path, other.Path) || StoragePaths.IsWithin(other.Path, roots[index].Path))
                    throw new StorageException("storage.pathRejected", "存储巡检根目录不能互相包含");
    }

    public Task<IReadOnlyList<StorageManagedItem>> InspectAsync(CancellationToken ct = default)
    {
        var result = new List<StorageManagedItem>();
        for (var index = 0; index < roots.Count; index++)
        {
            var root = roots[index];
            if (!Directory.Exists(root.Path)) continue;
            foreach (var entry in UpdatePaths.EnumerateFiles(root.Path, ct))
            {
                if (result.Count >= 100000) throw new StorageException("resource.capacityExceeded", "文件占用巡检超过条目预算");
                var path = UpdatePaths.Local(root.Path, entry.Relative); var file = new FileInfo(path);
                var protection = database.Space.Protects(path) ? "activeReservation" : root.Rebuildable ? null : root.Category switch
                { "temporary" => database.TemporaryFiles.State(path).Protection, "backups" => "backupRecovery", "recovery" => "recoveryEvidence", _ => "userData" };
                var module = root.Category == "temporary" ? database.TemporaryFiles.State(path).Module : root.Module;
                var lastUsed = new DateTimeOffset(new[] { file.LastWriteTimeUtc, file.LastAccessTimeUtc }.Max(), TimeSpan.Zero);
                result.Add(new(index + "/" + entry.Relative, root.Category, module, file.Length,
                    StorageFileDeletion.FileRevision(path, file.Length), lastUsed, protection is null,
                    root.Category == "temporary" ? "terminatedTemporary" : "cacheLru", protection is null ? [] : [protection]));
            }
        }
        return Task.FromResult<IReadOnlyList<StorageManagedItem>>(result);
    }
    public async Task<StorageManagedItem> FreezeAsync(StorageManagedItem item, CancellationToken ct = default) =>
        item with { ContentHash = await StorageFileDeletion.HashAsync(Resolve(item.Key), ct) };

    public async Task<StorageDeletion> DeleteAsync(StorageManagedItem expected, ActivityBarrier.MaintenanceLease maintenance, CancellationToken ct = default)
    {
        using var activity = database.Activities.EnterMaintenanceActivity(maintenance, "storage.fileCleanup");
        var path = Resolve(expected.Key);
        if (database.Space.Protects(path)) return new(false, 0, "activeReservation");
        var root = roots[int.Parse(expected.Key[..expected.Key.IndexOf('/')], System.Globalization.CultureInfo.InvariantCulture)];
        if (!root.Rebuildable && (root.Category != "temporary" || !database.TemporaryFiles.State(path).Eligible))
            return new(false, 0, root.Category == "temporary" ? database.TemporaryFiles.State(path).Protection : "userData");
        var result = await StorageFileDeletion.DeleteAsync(path, expected.Bytes, expected.Revision, expected.ContentHash!, ct);
        if (result.Deleted && root.Category == "temporary")
            await database.TemporaryFiles.PruneMissingAsync(CancellationToken.None, maintenance, path);
        return result;
    }
    private string Resolve(string key)
    {
        var separator = key.IndexOf('/');
        if (separator < 1 || !int.TryParse(key.AsSpan(0, separator), out var index) || index < 0 || index >= roots.Count)
            throw new StorageException("storage.pathRejected", "清理候选不属于受控文件根");
        return UpdatePaths.Local(roots[index].Path, key[(separator + 1)..]);
    }
}
