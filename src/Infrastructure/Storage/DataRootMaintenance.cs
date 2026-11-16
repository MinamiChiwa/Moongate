using System.Runtime.Versioning;
using MabiTool.ModuleSdk;

namespace MabiTool.Infrastructure;

public sealed partial class DataRootLease
{
    [SupportedOSPlatform("windows")]
    internal static string ResolveAuthorityBootstrap(StorageOptions options)
    {
        var bootstrap = StoragePaths.ValidateLocal(options.BootstrapDirectory);
        if (DataMaintenanceJournal.HasPending(bootstrap)) return bootstrap;
        var configFile = Path.Combine(bootstrap, "data-root.json");
        var config = File.Exists(configFile) ? Read<BootstrapConfiguration>(configFile) : null;
        if (config is null && !options.Initialize) return bootstrap;
        if (config is not null && (config.Version != 1 || config.Product != "MabiTool" || !Guid.TryParseExact(config.RootId, "N", out _)))
            throw new StorageException("storage.configurationInvalid", "数据根配置版本、产品或身份无效");
        var requested = options.PortableRoot is not null ? StoragePaths.ValidateLocal(Path.Combine(StoragePaths.ValidateLocal(options.PortableRoot), "data")) :
            options.DataRoot is not null ? StoragePaths.ValidateLocal(options.DataRoot) : null;
        if (config is not null && requested is not null && !SamePath(requested, config.DataRoot) && !SamePath(requested, config.LaunchRoot))
            throw new StorageException("storage.configurationConflict", "启动路径与稳定数据根配置不一致");
        var root = StoragePaths.ValidateLocal(config?.DataRoot ?? requested ?? StorageOptions.DefaultDataRoot(bootstrap));
        var markerFile = Path.Combine(root, MarkerName);
        if (!File.Exists(markerFile)) return bootstrap;
        var marker = Read<DataRootIdentity>(markerFile);
        VerifyIdentity(marker, config?.RootId);
        return AuthorityDirectory(marker, root, bootstrap);
    }

    [SupportedOSPlatform("windows")]
    private static string VerifyActiveRoot(DataRootIdentity marker, BootstrapConfiguration? config, string root, string bootstrap)
    {
        VerifyIdentity(marker, config?.RootId);
        if (marker.State != "active") throw new StorageException("storage.rootRetained", "数据根是迁移候选或保留副本，不能作为业务写根");
        var authority = AuthorityDirectory(marker, root, bootstrap);
        if (DataMaintenanceJournal.HasPending(authority))
            throw new StorageException("storage.maintenanceRequired", "数据根的权威维护尚未完成，须先按持久日志恢复");
        var authorityFile = Path.Combine(authority, "data-root.json");
        var authorityConfig = SamePath(authority, bootstrap) ? config :
            File.Exists(authorityFile) ? Read<BootstrapConfiguration>(authorityFile) :
            throw new StorageException("storage.notInitialized", "数据根的权威引导配置尚未完成初始化");
        if (authorityConfig is not null && (authorityConfig.Version != 1 || authorityConfig.Product != "MabiTool" ||
            authorityConfig.RootId != marker.RootId || !SamePath(authorityConfig.DataRoot, root)))
            throw new StorageException("storage.identityMismatch", "数据根与权威引导指针不一致");
        return authority;
    }

    [SupportedOSPlatform("windows")]
    private static string AuthorityDirectory(DataRootIdentity marker, string root, string bootstrap)
    {
        if (marker.AuthorityBootstrap is null) return bootstrap;
        var authority = StoragePaths.ValidateLocal(marker.AuthorityBootstrap);
        if (StoragePaths.IsWithin(root, authority) || StoragePaths.IsWithin(authority, root) || !Directory.Exists(authority))
            throw new StorageException("storage.configurationInvalid", "数据根的权威引导目录无效或不可用");
        LocalDiscoverySecurity.EnsureDirectory(authority);
        return authority;
    }

    private static void VerifyIdentity(DataRootIdentity marker, string? expectedRootId)
    {
        if (marker.Version is not (1 or 2) || marker.Product != "MabiTool" || !Guid.TryParseExact(marker.RootId, "N", out _) ||
            marker.DatabaseId is not null && !Guid.TryParseExact(marker.DatabaseId, "N", out _) ||
            expectedRootId is not null && marker.RootId != expectedRootId || marker.State is not ("active" or "candidate" or "retained"))
            throw new StorageException("storage.identityMismatch", "数据根身份与稳定配置不一致");
    }

    private static bool SamePath(string? left, string? right) => left is not null && right is not null &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    internal static DataRootLease OpenForMaintenance(string bootstrapDirectory, string rootPath, string rootId,
        string databaseId, string transactionId, StorageDirectoryEvidence? initializeCandidate = null)
    {
        var bootstrap = StoragePaths.ValidateLocal(bootstrapDirectory); var root = StoragePaths.ValidateLocal(rootPath);
        if (StoragePaths.IsWithin(root, bootstrap) || StoragePaths.IsWithin(bootstrap, root) || Path.GetPathRoot(root) == root)
            throw new StorageException("storage.pathRejected", "数据维护根与引导目录须独立");
        EnsurePrivateDirectory(bootstrap, false);
        if (!Directory.Exists(root)) throw new StorageException("storage.rootMissing", "数据维护根不可用");
        var markerPath = Path.Combine(root, MarkerName);
        if (!File.Exists(markerPath))
        {
            if (initializeCandidate is null) throw new StorageException("storage.identityMismatch", "数据维护根缺少身份标记");
            initializeCandidate.Verify(); EnsurePrivateDirectory(root, true);
            Publish(markerPath, new DataRootIdentity(2, "MabiTool", rootId, databaseId, "candidate", transactionId, bootstrap), false);
        }
        var marker = Read<DataRootIdentity>(markerPath);
        if (marker.Version is not (1 or 2) || marker.Product != "MabiTool" || marker.RootId != rootId || marker.DatabaseId != databaseId ||
            marker.State is not ("active" or "candidate" or "retained") || marker.State != "active" && marker.MaintenanceId != transactionId)
            throw new StorageException("storage.identityMismatch", "数据维护根不属于固定事务");
        var writer = Lock(Path.Combine(root, "writer.lock"), "storage.writerBusy");
        try
        {
            marker = Read<DataRootIdentity>(markerPath);
            VerifyIdentity(marker, rootId);
            if (marker.DatabaseId != databaseId || marker.State != "active" && marker.MaintenanceId != transactionId ||
                marker.AuthorityBootstrap is not null && !SamePath(marker.AuthorityBootstrap, bootstrap))
                throw new StorageException("storage.identityMismatch", "取得写锁后维护根的权威或事务身份不匹配");
            var config = Read<BootstrapConfiguration>(Path.Combine(bootstrap, "data-root.json"));
            if (config.Version != 1 || config.Product != "MabiTool" || config.RootId != rootId)
                throw new StorageException("storage.identityMismatch", "维护引导配置不属于固定数据根");
            if (marker.Version != 2 || marker.AuthorityBootstrap is null)
                Publish(markerPath, marker with { Version = 2, AuthorityBootstrap = bootstrap }, true);
            var paths = new PathService(root, rootId, bootstrap, config.Portable);
            foreach (var child in new[] { "store", "assets", "settings", "cache", "logs", "diagnostics", "temp", "backups", "recovery" })
                LocalDiscoverySecurity.EnsureDirectory(paths.Resolve(child));
            return new(paths, writer, databaseId);
        }
        catch { writer.Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    internal void SetAuthority(string state, string transactionId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (state is not ("active" or "candidate" or "retained")) throw new ArgumentException("数据根权威状态无效");
        var path = Path.Combine(Paths.DataRoot, MarkerName); var marker = Read<DataRootIdentity>(path);
        if (marker.RootId != Paths.RootId || marker.DatabaseId != DatabaseId || marker.State != "active" && marker.MaintenanceId != transactionId ||
            !SamePath(marker.AuthorityBootstrap, Paths.BootstrapDirectory))
            throw new StorageException("storage.identityMismatch", "数据根权威变更缺少固定事务身份");
        Publish(path, marker with { Version = 2, State = state, MaintenanceId = state == "active" ? null : transactionId }, true);
    }

    [SupportedOSPlatform("windows")]
    internal static void SwitchAuthorityPointer(string bootstrap, string rootId, string originalRoot, string targetRoot)
    {
        using var guard = Lock(Path.Combine(bootstrap, "bootstrap.lock"), "storage.bootstrapBusy");
        var file = Path.Combine(bootstrap, "data-root.json"); var previous = Read<BootstrapConfiguration>(file);
        if (previous.Version != 1 || previous.Product != "MabiTool" || previous.RootId != rootId ||
            !string.Equals(previous.DataRoot, originalRoot, StringComparison.OrdinalIgnoreCase) && !string.Equals(previous.DataRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            throw new StorageException("storage.identityMismatch", "引导指针已偏离固定迁移事务");
        if (!string.Equals(previous.DataRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            Publish(file, previous with { DataRoot = targetRoot, LaunchRoot = previous.LaunchRoot ?? previous.DataRoot }, true);
    }
}
