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
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("数据根当前支持 Windows");
        try { return OpenCore(options); }
        catch (StorageException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new StorageException("storage.unavailable", "数据根或引导配置不可访问、权限不符或格式无效：" + error.Message, error);
        }
    }

    [SupportedOSPlatform("windows")]
    private static DataRootLease OpenCore(StorageOptions options)
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
        // 首次初始化在发布身份标记前中断（所有者校验失败、崩溃、断电、被结束）只会留下初始化自身的内容，按未完成的初始化续做；
        // 有任何其他内容时拒绝并指出是哪一项。
        var resuming = false;
        if (config is null && Directory.Exists(root) && !File.Exists(Path.Combine(root, MarkerName)) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            if (ForeignEntry(root) is { } foreign)
                throw new StorageException("storage.rootNotEmpty",
                    $"数据目录“{root}”里的“{foreign}”不是 Moongate 初始化产生的内容，无法确认来源，未自动初始化。" +
                    "如果它属于另一份 Moongate 数据，请放回原来的程序文件夹；不需要时把它移出该目录后重新打开 Moongate");
            resuming = true;
        }
        if (config is not null && !File.Exists(Path.Combine(root, MarkerName)))
            throw new StorageException("storage.identityMismatch", "已配置的数据根缺少身份标记");
        EnsurePrivateDirectory(root, config is null && !File.Exists(Path.Combine(root, MarkerName)));
        var markerPath = Path.Combine(root, MarkerName);
        LocalDiscoverySecurity.RejectLinks(markerPath);
        var marker = File.Exists(markerPath) ? Read<DataRootIdentity>(markerPath) : null;
        // 身份标记已发布、引导配置尚未写出时中断，且程序文件夹随后被整体搬走：标记里的权威引导目录已不存在，续做时改为当前位置。
        var relocate = config is null && marker is not null && UnfinishedDefaultLayout(marker, root, bootstrap);
        if (marker is not null && !relocate) VerifyActiveRoot(marker, config, root, bootstrap);
        var lease = Lock(Path.Combine(root, "writer.lock"), "storage.writerBusy");
        try
        {
            var lockedMarker = File.Exists(markerPath) ? Read<DataRootIdentity>(markerPath) : null;
            if (marker is not null && (lockedMarker is null || lockedMarker.RootId != marker.RootId))
                throw new StorageException("storage.identityMismatch", "取得写锁时数据根身份已改变");
            if (resuming)
            {
                if (lockedMarker is not null || ForeignEntry(root) is not null)
                    throw new StorageException("storage.identityMismatch", "取得写锁时数据根内容已改变");
                // 上次发布身份标记时留下的临时文件，以及空的标准子目录（下面按当前用户独占重新建出）。
                foreach (var temporary in Directory.EnumerateFiles(root, "*.tmp")) File.Delete(temporary);
                foreach (var child in StandardChildren)
                    if (Directory.Exists(Path.Combine(root, child))) Directory.Delete(Path.Combine(root, child), false);
            }
            if (relocate && lockedMarker is not null)
            {
                lockedMarker = lockedMarker with { Version = 2, AuthorityBootstrap = bootstrap };
                Publish(markerPath, lockedMarker, overwrite: true);
            }
            marker = lockedMarker ?? new(2, "MabiTool", Guid.NewGuid().ToString("N"), AuthorityBootstrap: bootstrap);
            var authority = VerifyActiveRoot(marker, config, root, bootstrap);
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

    /// <summary>初始化发布身份标记前会在数据根里产生的内容：写锁与发布标记时的临时文件。</summary>
    private static readonly System.Text.RegularExpressions.Regex PublishTemporary = new(@"\A[0-9a-f]{32}\.tmp\z");
    /// <summary>初始化在身份标记之后创建的标准子目录；目录为空时视为初始化内容。</summary>
    private static readonly string[] StandardChildren = ["store", "assets", "settings", "cache", "logs", "diagnostics", "temp", "backups", "recovery"];

    /// <summary>
    /// 没有身份标记的数据根里第一个不属于初始化内容的项（相对数据根的名称）；全部属于初始化内容时为空。
    /// 初始化内容只有：<c>writer.lock</c>、发布标记的临时文件 <c>&lt;32 位十六进制&gt;.tmp</c>（不超过 16 KiB）、空的标准子目录。
    /// 数据库、设置、资产等任何文件，以及其他名称或非空的子目录都不是。
    /// </summary>
    internal static string? ForeignEntry(string root)
    {
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return entry.Name;
            if (entry is FileInfo file)
            {
                if (file.Name.Equals("writer.lock", StringComparison.OrdinalIgnoreCase)) continue;
                if (PublishTemporary.IsMatch(file.Name) && file.Length <= 16384) continue;
                return file.Name;
            }
            if (StandardChildren.Contains(entry.Name, StringComparer.OrdinalIgnoreCase) &&
                !((DirectoryInfo)entry).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).Any()) continue;
            return entry.Name + Path.DirectorySeparatorChar;
        }
        return null;
    }

    /// <summary>
    /// 引导配置尚未写出、数据根是当前引导目录的同级 data，身份标记为活动且没有数据库身份，记录的权威引导目录是已不存在的另一个
    /// <c>bootstrap</c>（初始化中断后程序文件夹被搬走）：续做时把权威引导目录改为当前位置。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool UnfinishedDefaultLayout(DataRootIdentity marker, string root, string bootstrap, bool requireNoDatabase = true) =>
        marker.State == "active" && (!requireNoDatabase || marker.DatabaseId is null) && marker.AuthorityBootstrap is { } authority &&
        !SamePath(authority, bootstrap) && !Directory.Exists(authority) &&
        Path.GetFileName(Path.TrimEndingDirectorySeparator(authority)).Equals(ToolRoot.BootstrapName, StringComparison.OrdinalIgnoreCase) &&
        SamePath(root, StorageOptions.DefaultDataRoot(bootstrap));

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
        if (adoptEmpty && Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any() && LocalDiscoverySecurity.SupportsAccessControl(path))
        {
            var directory = new DirectoryInfo(path);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法取得当前 Windows 用户");
            var security = directory.GetAccessControl(AccessControlSections.Owner);
            if (!LocalDiscoverySecurity.IsAcceptedOwner(security.GetOwner(typeof(SecurityIdentifier)), user))
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
        FileStream file;
        // 只有共享冲突与锁冲突说明另一个宿主正在使用；其余失败（文件只读、写入被拦截、权限不符）按存储不可用报告具体原因。
        try { file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            throw new StorageException(code, "数据或引导目录已被其他宿主占用，或无法取得独占写锁", error);
        }
        try { LocalDiscoverySecurity.ValidateFile(path); return file; }
        catch (IOException error)
        {
            file.Dispose();
            throw new StorageException("storage.permissionDenied", $"{Path.GetFileName(path)} 的所有者或访问权限不符合当前 Windows 用户独占要求：{error.Message}", error);
        }
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
