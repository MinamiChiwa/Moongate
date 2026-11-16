using System.Text.Json;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;
using MabiTool.Infrastructure.Updates;

namespace MabiTool.Infrastructure;

public sealed partial class DataRootLease : IDisposable
{
    private const string MarkerName = ".mabitool-data-root.json";
    private FileStream? writerLock;
    private readonly object lifetimeGate = new();
    private bool disposed;
    private int databaseOwners;
    public PathService Paths { get; }
    public string? DatabaseId { get; private set; }

    private DataRootLease(PathService paths, FileStream writerLock, string? databaseId)
    {
        Paths = paths;
        this.writerLock = writerLock;
        DatabaseId = databaseId;
    }

    public static DataRootLease Open(StorageOptions options)
        => OpenCoreChecked(options, null, null);

    internal static DataRootLease OpenForUpdate(StorageOptions options, string transactionId, string intentHash)
        => OpenCoreChecked(options, transactionId, intentHash);

    private static DataRootLease OpenCoreChecked(StorageOptions options, string? transactionId, string? intentHash)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("数据根当前支持 Windows");
        try { return OpenCore(options, transactionId, intentHash); }
        catch (StorageException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new StorageException("storage.unavailable", "数据根或引导配置不可访问、权限不符或格式无效", error);
        }
    }

    [SupportedOSPlatform("windows")]
    private static DataRootLease OpenCore(StorageOptions options, string? transactionId, string? intentHash)
    {
        var bootstrap = StoragePaths.ValidateLocal(options.BootstrapDirectory);
        if (options.DataRoot is not null && options.PortableRoot is not null)
            throw new StorageException("storage.configurationConflict", "自定义数据根与便携模式不能同时指定");
        if (!Directory.Exists(bootstrap) && !options.Initialize)
            throw new StorageException("storage.notInitialized", "首次启动须通过 --initialize-data 显式初始化数据根");
        EnsurePrivateDirectory(bootstrap, options.Initialize);
        var configPath = Path.Combine(bootstrap, "data-root.json");
        LocalDiscoverySecurity.RejectLinks(configPath);
        using var bootstrapLock = Lock(Path.Combine(bootstrap, "bootstrap.lock"), "storage.bootstrapBusy");
        if (DataMaintenanceJournal.HasPending(bootstrap)) throw new StorageException("storage.maintenanceRequired", "数据维护尚未完成，须先按持久日志恢复");
        var config = File.Exists(configPath) ? Read<BootstrapConfiguration>(configPath) : null;
        if (config is null && !options.Initialize)
            throw new StorageException("storage.notInitialized", "缺少稳定数据根配置；须显式初始化或关联原有数据根");
        var requested = options.PortableRoot is not null ? StoragePaths.ValidateLocal(Path.Combine(StoragePaths.ValidateLocal(options.PortableRoot), "data")) :
            options.DataRoot is not null ? StoragePaths.ValidateLocal(options.DataRoot) : null;
        if (config is not null && (config.Version != 1 || config.Product != "MabiTool" || !Guid.TryParseExact(config.RootId, "N", out _)))
            throw new StorageException("storage.configurationInvalid", "数据根配置版本、产品或身份无效");
        if (config is not null && (requested is not null && !string.Equals(requested, config.DataRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(requested, config.LaunchRoot, StringComparison.OrdinalIgnoreCase) ||
            options.PortableRoot is not null && !config.Portable))
            throw new StorageException("storage.configurationConflict", "已配置的数据根只能通过数据迁移流程更改");
        var root = StoragePaths.ValidateLocal(config?.DataRoot ?? requested ?? StorageOptions.DefaultDataRoot(bootstrap));
        if (StoragePaths.IsWithin(root, bootstrap) || StoragePaths.IsWithin(bootstrap, root) || Path.GetPathRoot(root) == root)
            throw new StorageException("storage.pathInvalid", "数据根与引导目录须独立，且不能使用磁盘根目录");
        if (config is not null && !Directory.Exists(root))
            throw new StorageException("storage.rootMissing", "已配置的数据根不存在；请恢复原目录或执行数据迁移");
        if (config is null && Directory.Exists(root) && !File.Exists(Path.Combine(root, MarkerName)) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new StorageException("storage.rootNotEmpty", "初始化须使用空目录，或显式关联带有效身份标记的原数据根");
        if (config is not null && !File.Exists(Path.Combine(root, MarkerName)))
            throw new StorageException("storage.identityMismatch", "已配置的数据根缺少身份标记");
        EnsurePrivateDirectory(root, config is null && !File.Exists(Path.Combine(root, MarkerName)));
        var markerPath = Path.Combine(root, MarkerName);
        LocalDiscoverySecurity.RejectLinks(markerPath);
        var marker = File.Exists(markerPath) ? Read<DataRootIdentity>(markerPath) : null;
        if (marker is not null) VerifyActiveRoot(marker, config, root, bootstrap);
        UpdateInstallGate.RequireOpen(root, transactionId, intentHash, marker?.RootId);
        var lease = Lock(Path.Combine(root, "writer.lock"), "storage.writerBusy");
        try
        {
            var lockedMarker = File.Exists(markerPath) ? Read<DataRootIdentity>(markerPath) : null;
            if (marker is not null && (lockedMarker is null || lockedMarker.RootId != marker.RootId))
                throw new StorageException("storage.identityMismatch", "取得写锁时数据根身份已改变");
            marker = lockedMarker ?? new(2, "MabiTool", Guid.NewGuid().ToString("N"), AuthorityBootstrap: bootstrap);
            var authority = VerifyActiveRoot(marker, config, root, bootstrap);
            UpdateInstallGate.RequireOpen(root, transactionId, intentHash, marker.RootId);
            marker = marker with { Version = 2, AuthorityBootstrap = authority };
            var authorityConfig = string.Equals(authority, bootstrap, StringComparison.OrdinalIgnoreCase) ? config :
                Read<BootstrapConfiguration>(Path.Combine(authority, "data-root.json"));
            var portable = authorityConfig?.Portable ?? options.PortableRoot is not null;
            var paths = new PathService(root, marker.RootId, authority, portable);
            if (lockedMarker is null) Publish(markerPath, marker, overwrite: false);
            else if (lockedMarker != marker) Publish(markerPath, marker, overwrite: true);
            foreach (var child in new[] { "store", "assets", "settings", "cache", "logs", "diagnostics", "temp", "backups", "recovery" })
                LocalDiscoverySecurity.EnsureDirectory(paths.Resolve(child));
            if (config is null)
                Publish(configPath, new BootstrapConfiguration(1, "MabiTool", root, marker.RootId, portable), overwrite: false);
            return new(paths, lease, marker.DatabaseId);
        }
        catch { lease.Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsurePrivateDirectory(string path, bool adoptEmpty)
    {
        LocalDiscoverySecurity.RejectLinks(path);
        if (!Directory.Exists(path))
        {
            // 所在文件夹不可写（例如程序放在 Program Files）时明确报错，不改用其他位置。
            try { LocalDiscoverySecurity.EnsureDirectory(path); }
            catch (UnauthorizedAccessException error)
            {
                throw new StorageException("storage.permissionDenied", ToolRoot.NotWritableMessage(Path.GetDirectoryName(path) ?? path), error);
            }
            return;
        }
        if (adoptEmpty && Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            var directory = new DirectoryInfo(path);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法取得当前 Windows 用户");
            var security = directory.GetAccessControl(AccessControlSections.Owner);
            if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier))))
                throw new StorageException("storage.permissionDenied", "初始化空目录须由当前 Windows 用户拥有");
            var privateAccess = directory.GetAccessControl(AccessControlSections.Access);
            privateAccess.SetAccessRuleProtection(true, false);
            foreach (FileSystemAccessRule rule in privateAccess.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                privateAccess.RemoveAccessRuleSpecific(rule);
            privateAccess.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(privateAccess);
        }
        LocalDiscoverySecurity.EnsureDirectory(path);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream Lock(string path, string code)
    {
        LocalDiscoverySecurity.RejectLinks(path);
        try
        {
            var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { LocalDiscoverySecurity.ValidateFile(path); return file; }
            catch { file.Dispose(); throw; }
        }
        catch (IOException error) { throw new StorageException(code, "数据或引导目录已被其他宿主占用，或无法取得独占写锁", error); }
    }

    [SupportedOSPlatform("windows")]
    private static T Read<T>(string path) where T : class
    {
        LocalDiscoverySecurity.ValidateFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        if (file.Length is <= 0 or > 16384) throw new StorageException("storage.configurationInvalid", "数据根配置大小无效");
        return JsonSerializer.Deserialize<T>(file, ContractJson.Options) ?? throw new JsonException("数据根配置为空");
    }

    private static void Publish<T>(string path, T value, bool overwrite)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, ContractJson.Options);
                file.Flush(true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose()
    {
        lock (lifetimeGate)
        {
            disposed = true;
            ReleaseRootIfUnused();
        }
    }

    // 宿主与离线恢复器共用此锁；子所有权结束前保留数据根独占句柄。
    internal DatabaseOwnership AcquireDatabaseOwnership()
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var file = Lock(Paths.Resolve("store", "database.lock"), "resource.busy");
            databaseOwners++;
            return new DatabaseOwnership(this, file);
        }
    }

    private void ReleaseRootIfUnused()
    {
        if (!disposed || databaseOwners != 0) return;
        writerLock?.Dispose();
        writerLock = null;
    }

    internal sealed class DatabaseOwnership : IDisposable
    {
        private readonly DataRootLease owner;
        private readonly FileStream file;
        private bool released;
        private bool closed;
        private int users;

        internal DatabaseOwnership(DataRootLease owner, FileStream file) { this.owner = owner; this.file = file; }

        internal IDisposable Use(DataRootLease expected)
        {
            lock (owner.lifetimeGate)
            {
                if (!ReferenceEquals(owner, expected)) throw new ArgumentException("数据库所有权属于其他数据根", nameof(expected));
                ObjectDisposedException.ThrowIf(released, this);
                if (users != 0) throw new StorageException("resource.busy", "数据库所有权已有活动恢复步骤");
                users++;
                return new Usage(this);
            }
        }

        public void Dispose()
        {
            lock (owner.lifetimeGate)
            {
                released = true;
                CloseIfUnused();
            }
        }

        private void CloseIfUnused()
        {
            if (!released || users != 0 || closed) return;
            closed = true;
            file.Dispose();
            owner.databaseOwners--;
            owner.ReleaseRootIfUnused();
        }

        private sealed class Usage(DatabaseOwnership ownership) : IDisposable
        {
            private int released;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                lock (ownership.owner.lifetimeGate)
                {
                    ownership.users--;
                    ownership.CloseIfUnused();
                }
            }
        }
    }
    internal void SealDatabaseId(string databaseId)
    {
        ObjectDisposedException.ThrowIf(writerLock is null, this);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = Path.Combine(Paths.DataRoot, MarkerName);
        var marker = Read<DataRootIdentity>(path);
        if (marker.RootId != Paths.RootId || marker.DatabaseId is not null && marker.DatabaseId != databaseId)
            throw new StorageException("storage.databaseIdentityMismatch", "数据库身份与数据根标记不一致");
        if (marker.DatabaseId is null) Publish(path, marker with { DatabaseId = databaseId }, overwrite: true);
        DatabaseId = databaseId;
    }
    private sealed record BootstrapConfiguration(int Version, string Product, string DataRoot, string RootId, bool Portable, string? LaunchRoot = null);
    private sealed record DataRootIdentity(int Version, string Product, string RootId, string? DatabaseId = null, string State = "active",
        string? MaintenanceId = null, string? AuthorityBootstrap = null);
}
