using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Extensions;

/// <summary>一个可以运行的扩展：启用、完好且声明了运行入口。<see cref="Grants"/> 是当前授予的权限。</summary>
public sealed record RunnableExtension(string Id, string Version, string Directory, ExtensionManifest Manifest,
    IReadOnlySet<string> Grants, bool Development);

/// <summary>
/// 管理数据根下的 extensions/。纯数据扩展只装清单、图片与数据文件；带代码的扩展另须声明运行入口 runtime，
/// 安装时不执行包内任何内容，程序只在启用后由 <see cref="ExtensionRuntime"/> 按入口启动。
/// 安装先生成预览，确认后在 staging/ 解出、复核内容树哈希，再以同卷改名发布到 &lt;标识&gt;/&lt;版本&gt;/；
/// 进程中断留下的暂存与未完成发布由 <see cref="Initialize"/> 在下次启动时收尾。
/// 确认安装即同意清单里的普通权限，高风险权限默认关闭；授权记录在安装记录里，升级保留仍声明的高风险授权。
/// </summary>
public sealed class ExtensionInstaller : IDisposable
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);
    public const int MaximumPreviews = 16, MaximumListed = 64;
    public const string CodeNotSupportedMessage = "包含程序或声明了需要程序的字段，但清单没有声明运行入口 runtime";
    public const string InboxName = "inbox", StagingName = "staging";
    public const string DataName = "data", LogsName = "logs", TempName = "temp";
    private const string StateFile = "state.json", MarkerFile = "installing.json", DevelopmentFile = "development.json";
    private static readonly string DevelopmentTree = new('0', 64);

    /// <summary>扩展安装、启停、授权或卸载后通知运行时；参数为扩展标识。</summary>
    public event Action<string>? Changed;
    /// <summary>发布新版本或卸载前由运行时停止该扩展的进程，释放其打开的文件。</summary>
    public Func<string, CancellationToken, Task>? BeforeReplace { get; set; }
    /// <summary>运行时提供的当前运行状态；没有运行时（嵌入测试宿主）时为空。</summary>
    public Func<string, ExtensionRuntimeState?>? RuntimeState { get; set; }

    private static readonly HashSet<string> DataSuffixes = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".txt", ".md", ".csv", ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9-]{0,31}\\.[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options) { WriteIndented = true };

    private sealed record Preview(ExtensionInstallPreview Snapshot, string PackagePath, ExtensionPackageInspection Inspection, long CreatedAt);
    private sealed record InstallRecord(int SchemaVersion, string Version, bool Enabled, string TreeSha256, DateTimeOffset InstalledAt,
        Dictionary<string, bool>? Grants = null, string? DevelopmentDirectory = null);
    private sealed record DevelopmentSetting(int SchemaVersion, bool Enabled);
    private sealed record InstallingMarker(int SchemaVersion, string Version);

    private readonly string root, inbox, staging;
    private readonly Version hostApi;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object previewGate = new();
    private readonly Dictionary<string, Preview> previews = new(StringComparer.Ordinal);

    public ExtensionInstaller(string root, Version hostApi, TimeProvider? clock = null)
    {
        this.root = Path.GetFullPath(root);
        inbox = Path.Combine(this.root, InboxName);
        staging = Path.Combine(this.root, StagingName);
        this.hostApi = hostApi;
        time = clock ?? TimeProvider.System;
    }

    public string Root => root;
    public string Inbox => inbox;

    /// <summary>
    /// 宿主启动时调用：保证 extensions/ 与 inbox/ 存在，清空 staging/，回滚未完成的发布，删除不被安装记录引用的版本目录。
    /// 损坏的扩展保留原样，由 <see cref="Catalog"/> 列出原因。
    /// </summary>
    public void Initialize()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(inbox);
        if (Directory.Exists(staging)) DeleteDirectory(staging);
        Directory.CreateDirectory(staging);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!IdPattern.IsMatch(Path.GetFileName(directory))) continue;
            var record = ReadRecord(directory);
            var markerPath = Path.Combine(directory, MarkerFile);
            if (File.Exists(markerPath))
            {
                var marker = TryRead<InstallingMarker>(markerPath);
                if (marker is not null && VersionPattern.IsMatch(marker.Version) && marker.Version != record?.Version)
                    DeleteDirectory(Path.Combine(directory, marker.Version));
                File.Delete(markerPath);
            }
            // 没有可读记录时不清理版本目录：记录损坏的扩展原样保留，由用户卸载。
            if (record is null) continue;
            foreach (var version in Directory.EnumerateDirectories(directory))
                if (VersionPattern.IsMatch(Path.GetFileName(version)) && Path.GetFileName(version) != record.Version)
                    DeleteDirectory(version);
        }
    }

    /// <summary>已安装扩展与投放位置里的待安装包；<paramref name="includePaths"/> 为真时附带本机路径（只给原生桌面客户端）。</summary>
    public ExtensionCatalog Catalog(bool includePaths)
    {
        var installed = new List<InstalledExtension>();
        if (Directory.Exists(root))
            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
                if (IdPattern.IsMatch(Path.GetFileName(directory)) && installed.Count < MaximumListed)
                    installed.Add(Describe(directory, includePaths));
        var pending = Directory.Exists(inbox)
            ? new DirectoryInfo(inbox).EnumerateFiles("*.zip").OrderBy(x => x.Name, StringComparer.Ordinal).Take(MaximumListed)
                .Select(x => new PendingExtensionPackage(x.Name, new((ulong)x.Length), new DateTimeOffset(x.LastWriteTimeUtc, TimeSpan.Zero))).ToArray()
            : [];
        return new(installed, pending, includePaths ? root : null, includePaths ? inbox : null, DevelopmentMode);
    }

    /// <summary>用户是否开启了开发模式加载；记录在 extensions/development.json。</summary>
    public bool DevelopmentMode => TryRead<DevelopmentSetting>(Path.Combine(root, DevelopmentFile)) is { SchemaVersion: 1, Enabled: true };

    /// <summary>一个已安装扩展的当前描述（含授权与运行状态）。</summary>
    public InstalledExtension Get(string id, bool includePaths) => Describe(Existing(id), includePaths);

    /// <summary>清单可读的全部已安装扩展（含停用的），供快捷键设置列出扩展命令及其可用性。</summary>
    public IReadOnlyList<(string Id, ExtensionManifest Manifest, bool Enabled, IReadOnlySet<string> Grants)> Declared()
    {
        var result = new List<(string, ExtensionManifest, bool, IReadOnlySet<string>)>();
        if (!Directory.Exists(root)) return result;
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            if (IdPattern.IsMatch(Path.GetFileName(directory)) && ReadRecord(directory) is { } record && Load(directory, record) is { } loaded)
                result.Add((loaded.Manifest.Id, loaded.Manifest, record.Enabled, Granted(loaded.Manifest, record)));
        return result;
    }

    /// <summary>启用、完好且声明了运行入口的扩展；开发模式关闭时不含开发中的扩展。</summary>
    public IReadOnlyList<RunnableExtension> Runnable()
    {
        var result = new List<RunnableExtension>();
        if (!Directory.Exists(root)) return result;
        var development = DevelopmentMode;
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if (!IdPattern.IsMatch(Path.GetFileName(directory)) || ReadRecord(directory) is not { Enabled: true } record) continue;
            if (record.DevelopmentDirectory is not null && !development) continue;
            if (Load(directory, record) is not { Manifest.Runtime: not null } loaded) continue;
            result.Add(new(loaded.Manifest.Id, record.Version, loaded.Directory, loaded.Manifest, Granted(loaded.Manifest, record),
                record.DevelopmentDirectory is not null));
        }
        return result;
    }

    /// <summary>
    /// 授予或撤销权限；只能涉及清单声明的权限。结果写入安装记录后通知运行时，撤销立即生效。
    /// </summary>
    public async Task<InstalledExtension> SetGrantsAsync(string id, IReadOnlyDictionary<string, bool> grants, bool includePaths, CancellationToken ct)
    {
        var directory = Existing(id);
        if (grants.Count is 0 or > 32) throw new OperationException("request.invalid", "授权变更须包含 1–32 项");
        if (!await gate.WaitAsync(0, ct)) throw new OperationException("resource.busy", "另一个扩展操作正在进行");
        try
        {
            var record = ReadRecord(directory) ?? throw new OperationException("extension.installDamaged", "安装记录缺失或损坏");
            var loaded = Load(directory, record) ?? throw new OperationException("extension.installDamaged", "清单无法读取");
            var current = Granted(loaded.Manifest, record).ToHashSet(StringComparer.Ordinal);
            foreach (var (permission, granted) in grants)
            {
                if (!loaded.Manifest.Permissions.Contains(permission, StringComparer.Ordinal) || ExtensionPermissions.Find(permission) is null)
                    throw new OperationException("request.invalid", "清单没有声明这项权限：" + permission);
                if (granted) current.Add(permission); else current.Remove(permission);
            }
            WriteAtomic(Path.Combine(directory, StateFile), record with
            {
                Grants = loaded.Manifest.Permissions.Where(x => ExtensionPermissions.Find(x) is not null)
                    .ToDictionary(x => x, current.Contains, StringComparer.Ordinal)
            });
        }
        finally { gate.Release(); }
        Changed?.Invoke(id);
        return Describe(directory, includePaths);
    }

    /// <summary>开启或关闭开发模式；关闭时运行中的开发扩展随之停止。</summary>
    public ExtensionCatalog SetDevelopmentMode(bool enabled, bool includePaths)
    {
        Directory.CreateDirectory(root);
        WriteAtomic(Path.Combine(root, DevelopmentFile), new DevelopmentSetting(1, enabled));
        foreach (var directory in Directory.EnumerateDirectories(root))
            if (IdPattern.IsMatch(Path.GetFileName(directory)) && ReadRecord(directory)?.DevelopmentDirectory is not null)
                Changed?.Invoke(Path.GetFileName(directory));
        return Catalog(includePaths);
    }

    /// <summary>
    /// 以开发模式加载一个本机目录：不复制文件，每次启动都从该目录读取清单与程序，便于改完即重启查看。
    /// 只在开发模式开启时接受；同一标识已正式安装时须先卸载。加载后为停用状态，普通权限已授予。
    /// </summary>
    public async Task<InstalledExtension> LoadDevelopmentAsync(string source, bool includePaths, CancellationToken ct)
    {
        if (!DevelopmentMode) throw new OperationException("extension.developmentModeOff", "开发模式未开启，请先在模块页开启");
        if (source.Length > 260 || !Path.IsPathFullyQualified(source) || !Directory.Exists(source))
            throw new OperationException("request.invalid", "开发目录必须是已存在的绝对路径");
        source = Path.GetFullPath(source);
        if (PathWithin(source, root) || PathWithin(root, source)) throw new OperationException("request.invalid", "开发目录不能与扩展目录互相包含");
        var manifestPath = Path.Combine(source, ExtensionPackageInspector.ManifestName);
        if (!File.Exists(manifestPath)) throw new OperationException("extension.manifestMissing", "开发目录根部缺少 extension.json");
        if (new FileInfo(manifestPath).Length > ExtensionPackageInspector.MaximumManifestBytes)
            throw new OperationException("extension.limitExceeded", "扩展清单超过 64 KiB");
        ExtensionManifest manifest;
        try { manifest = ExtensionPackageInspector.ParseManifest(await File.ReadAllBytesAsync(manifestPath, ct)); }
        catch (ExtensionPackageException error) { throw new OperationException(error.Code, error.Message); }
        if (hostApi < manifest.HostApi.Minimum || hostApi >= manifest.HostApi.MaximumExclusive)
            throw new OperationException("extension.hostApiIncompatible", "当前宿主 API 不在扩展声明的兼容范围内");
        if (manifest.Runtime is null) throw new OperationException("extension.manifestInvalid", "开发模式只加载声明了运行入口 runtime 的扩展");
        RequirePermissionsAvailable(manifest);
        if (!await gate.WaitAsync(0, ct)) throw new OperationException("resource.busy", "另一个扩展操作正在进行");
        var directory = Path.Combine(root, manifest.Id);
        try
        {
            if (ReadRecord(directory) is { DevelopmentDirectory: null })
                throw new OperationException("request.invalid", "已正式安装同一标识的扩展，请先卸载");
            Directory.CreateDirectory(directory);
            WriteAtomic(Path.Combine(directory, StateFile), new InstallRecord(1, Text(manifest.Version), false, DevelopmentTree, time.GetUtcNow(),
                InitialGrants(manifest, null), source));
        }
        finally { gate.Release(); }
        Changed?.Invoke(manifest.Id);
        return Describe(directory, includePaths);
    }

    /// <summary>为投放位置里的一个包生成安装预览。</summary>
    public async Task<ExtensionInstallPreview> PreviewInboxAsync(string name, CancellationToken ct)
    {
        if (name.Length is < 5 or > 200 || name != Path.GetFileName(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new OperationException("request.invalid", "投放位置里的文件名无效");
        FileNames.Validate(name);
        var path = Path.Combine(inbox, name);
        if (!File.Exists(path)) throw new OperationException("resource.notFound", "投放位置里没有这个文件");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return await PreviewAsync(input, "inbox", name, ct);
    }

    /// <summary>
    /// 把包复制到 staging/ 后检查，判定是否为纯数据扩展并算出安装动作；通过时返回十五分钟有效的预览。
    /// 安装使用这份暂存副本，预览之后原文件的变化不影响安装内容。
    /// </summary>
    public async Task<ExtensionInstallPreview> PreviewAsync(Stream package, string source, string? inboxName, CancellationToken ct)
    {
        Directory.CreateDirectory(staging);
        var id = Guid.NewGuid().ToString("N");
        var copy = Path.Combine(staging, id + ".zip");
        try
        {
            ExtensionPackageInspection inspection;
            await using (var file = new FileStream(copy, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await package.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > ExtensionPackageInspector.MaximumPackageBytes)
                        throw new ExtensionPackageException("extension.limitExceeded", "扩展包超过 32 MiB");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                file.Position = 0;
                inspection = await ExtensionPackageInspector.InspectAsync(file, hostApi, ct);
            }
            RequireInstallable(inspection);
            var (action, installedVersion) = Decide(inspection);
            var previous = installedVersion is null ? null : Load(Path.Combine(root, inspection.Manifest.Id), ReadRecord(Path.Combine(root, inspection.Manifest.Id))!);
            var snapshot = new ExtensionInstallPreview(id, source, inboxName, Summary(inspection, previous?.Manifest), action, installedVersion,
                time.GetUtcNow() + PreviewLifetime);
            lock (previewGate)
            {
                Expire();
                if (previews.Count >= MaximumPreviews) throw new OperationException("resource.capacityExceeded", "安装预览数量达到上限");
                previews.Add(id, new(snapshot, copy, inspection, time.GetTimestamp()));
            }
            return snapshot;
        }
        catch (ExtensionPackageException error)
        {
            TryDelete(copy);
            throw new OperationException(error.Code, error.Message);
        }
        catch
        {
            TryDelete(copy);
            throw;
        }
    }

    /// <summary>按预览安装；失败或取消时删除本次的暂存与发布目录。</summary>
    public async Task<InstalledExtension> InstallAsync(string previewId, bool includePaths, CancellationToken ct)
    {
        Preview preview;
        lock (previewGate)
        {
            Expire();
            if (!previews.Remove(previewId, out preview!))
                throw new OperationException("extension.previewExpired", "安装预览不存在或已过期，请重新检查扩展包");
        }
        bool entered;
        try { entered = await gate.WaitAsync(0, ct); }
        catch
        {
            TryDelete(preview.PackagePath);
            throw;
        }
        if (!entered)
        {
            lock (previewGate) previews.TryAdd(previewId, preview);
            throw new OperationException("resource.busy", "另一个扩展操作正在进行");
        }
        var work = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".work");
        var notify = false;
        try
        {
            var manifest = preview.Inspection.Manifest;
            var version = Text(manifest.Version);
            var directory = Path.Combine(root, manifest.Id);
            var (action, _) = Decide(preview.Inspection);
            if (action == "reinstall")
            {
                TryDelete(preview.PackagePath);
                RemoveFromInbox(preview.Snapshot);
                return Describe(directory, includePaths);
            }

            await ExtractAsync(preview.PackagePath, work, preview.Inspection.TreeSha256, ct);
            var previous = ReadRecord(directory);
            var previousManifest = previous is null ? null : Load(directory, previous)?.Manifest;
            if (previous is not null && BeforeReplace is { } stop) await stop(manifest.Id, ct);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, version);
            if (Directory.Exists(target)) DeleteDirectory(target);
            var markerPath = Path.Combine(directory, MarkerFile);
            WriteAtomic(markerPath, new InstallingMarker(1, version));
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(work, target);
                var grants = previous is null || previousManifest is null ? InitialGrants(manifest, null)
                    : InitialGrants(manifest, Granted(previousManifest, previous));
                WriteAtomic(Path.Combine(directory, StateFile), new InstallRecord(1, version, previous?.Enabled ?? false,
                    preview.Inspection.TreeSha256, time.GetUtcNow(), grants));
            }
            catch
            {
                if (previous?.Version != version) DeleteDirectory(target);
                File.Delete(markerPath);
                throw;
            }
            File.Delete(markerPath);
            if (previous is not null && previous.Version != version) DeleteDirectory(Path.Combine(directory, previous.Version));
            TryDelete(preview.PackagePath);
            RemoveFromInbox(preview.Snapshot);
            notify = true;
            return Describe(directory, includePaths);
        }
        catch
        {
            if (Directory.Exists(work)) DeleteDirectory(work);
            TryDelete(preview.PackagePath);
            notify = true;
            throw;
        }
        finally
        {
            gate.Release();
            if (notify) Changed?.Invoke(preview.Inspection.Manifest.Id);
        }
    }

    /// <summary>启用或停用；状态写入安装记录，重启后保持。损坏的扩展只能停用。</summary>
    public async Task<InstalledExtension> SetEnabledAsync(string id, bool enabled, bool includePaths, CancellationToken ct)
    {
        var directory = Existing(id);
        if (!await gate.WaitAsync(0, ct)) throw new OperationException("resource.busy", "另一个扩展操作正在进行");
        try
        {
            var current = Describe(directory, includePaths: false);
            var record = ReadRecord(directory);
            if (enabled && (current.State != "ready" || record is null))
                throw new OperationException("extension.installDamaged", "扩展已损坏，只能停用或卸载：" + current.Problem);
            if (record is not null && record.Enabled != enabled)
                WriteAtomic(Path.Combine(directory, StateFile), record with { Enabled = enabled });
        }
        finally { gate.Release(); }
        Changed?.Invoke(id);
        return Describe(directory, includePaths);
    }

    /// <summary>卸载：先把扩展目录整体移入 staging/ 再删除，结果只有“已卸载”或“未变”。</summary>
    public async Task<ExtensionUninstallReceipt> UninstallAsync(string id, CancellationToken ct)
    {
        var directory = Existing(id);
        if (!await gate.WaitAsync(0, ct)) throw new OperationException("resource.busy", "另一个扩展操作正在进行");
        try
        {
            var version = ReadRecord(directory)?.Version;
            if (BeforeReplace is { } stop) await stop(id, ct);
            Directory.CreateDirectory(staging);
            var removed = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".removed");
            try { Directory.Move(directory, removed); }
            catch (IOException) { throw new OperationException("resource.busy", "扩展文件正在被使用，请稍后再卸载"); }
            catch (UnauthorizedAccessException) { throw new OperationException("resource.busy", "扩展文件正在被使用，请稍后再卸载"); }
            // 已移出安装位置即视为卸载完成；删不掉的残留在下次启动清空 staging/ 时回收。
            try { DeleteDirectory(removed); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new(id, version);
        }
        finally
        {
            gate.Release();
            Changed?.Invoke(id);
        }
    }

    public void Dispose()
    {
        lock (previewGate)
        {
            foreach (var preview in previews.Values) TryDelete(preview.PackagePath);
            previews.Clear();
        }
        gate.Dispose();
    }

    /// <summary>
    /// 纯数据扩展只能含数据文件、不声明任何需要程序的字段；带代码的扩展必须声明运行入口，文件类型不限，
    /// 但不能申请不开放的权限（读取游戏进程内存）。
    /// </summary>
    private static void RequireInstallable(ExtensionPackageInspection inspection)
    {
        var manifest = inspection.Manifest;
        if (manifest.Runtime is null)
        {
            if (inspection.ContainsExecutableCode || manifest.DeclaresCode)
                throw new OperationException("extension.codeNotSupported", CodeNotSupportedMessage);
            if (inspection.Files.FirstOrDefault(file => !DataSuffixes.Contains(Path.GetExtension(file.Path))) is { } other)
                throw new OperationException("extension.fileTypeRejected", "纯数据扩展只能包含清单、图片与数据文件，不支持：" + other.Path);
        }
        RequirePermissionsAvailable(manifest);
    }

    private static void RequirePermissionsAvailable(ExtensionManifest manifest)
    {
        if (manifest.Permissions.Contains(ExtensionPermissions.ProcessRead, StringComparer.Ordinal))
            throw new OperationException("extension.permissionUnavailable", "读取游戏进程内存不向扩展开放");
    }

    /// <summary>首次安装授予全部普通权限；升级时普通权限随确认授予，高风险权限只保留旧版本已经授予且仍声明的。</summary>
    private static Dictionary<string, bool> InitialGrants(ExtensionManifest manifest, IReadOnlySet<string>? previous) =>
        manifest.Permissions.Where(x => ExtensionPermissions.Find(x) is not null).ToDictionary(x => x,
            x => !ExtensionPermissions.Find(x)!.High || previous?.Contains(x) == true, StringComparer.Ordinal);

    private static IReadOnlySet<string> Granted(ExtensionManifest manifest, InstallRecord record) =>
        manifest.Permissions.Where(x => ExtensionPermissions.Find(x) is { } permission &&
            (record.Grants is { } grants ? grants.GetValueOrDefault(x) : !permission.High)).ToHashSet(StringComparer.Ordinal);

    private sealed record LoadedExtension(string Directory, ExtensionManifest Manifest);

    /// <summary>读取记录对应的安装目录与清单；开发中的扩展从开发目录读取。读取失败时为空。</summary>
    private static LoadedExtension? Load(string directory, InstallRecord record)
    {
        var installed = record.DevelopmentDirectory ?? Path.Combine(directory, record.Version);
        try
        {
            var path = Path.Combine(installed, ExtensionPackageInspector.ManifestName);
            if (!File.Exists(path) || new FileInfo(path).Length > ExtensionPackageInspector.MaximumManifestBytes) return null;
            var manifest = ExtensionPackageInspector.ParseManifest(File.ReadAllBytes(path));
            return manifest.Id == Path.GetFileName(directory) && (record.DevelopmentDirectory is not null || Text(manifest.Version) == record.Version)
                ? new(installed, manifest) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExtensionPackageException) { return null; }
    }

    private (string Action, string? InstalledVersion) Decide(ExtensionPackageInspection inspection)
    {
        var manifest = inspection.Manifest;
        var record = ReadRecord(Path.Combine(root, manifest.Id));
        if (record is null) return ("install", null);
        if (record.DevelopmentDirectory is not null)
            throw new OperationException("request.invalid", "同一标识的扩展正以开发模式加载，请先卸载");
        var installed = Version.Parse(record.Version);
        if (manifest.Version > installed) return ("upgrade", record.Version);
        if (manifest.Version < installed)
            throw new OperationException("extension.downgradeRejected", $"已安装更高的版本 {record.Version}，请先卸载再安装 {Text(manifest.Version)}");
        if (record.TreeSha256 != inspection.TreeSha256)
            throw new OperationException("extension.versionConflict", $"已安装的 {record.Version} 与这个包内容不同，同一版本不能替换，请提高版本号");
        return ("reinstall", record.Version);
    }

    private static ExtensionPackageSummary Summary(ExtensionPackageInspection inspection, ExtensionManifest? previous)
    {
        var manifest = inspection.Manifest;
        return Summary(manifest, inspection.Files.Count, (ulong)inspection.Files.Sum(x => x.Size), inspection.TreeSha256, previous);
    }

    private static ExtensionPackageSummary Summary(ExtensionManifest manifest, int files, ulong bytes, string tree, ExtensionManifest? previous)
    {
        var contributions = new List<string>();
        if (manifest.AssetPack is not null) contributions.Add("assetPack");
        if (manifest.Runtime is not null) contributions.Add("code");
        if (manifest.SettingItems.Count > 0) contributions.Add("settings");
        if (manifest.CommandItems.Count > 0) contributions.Add("commands");
        return new(manifest.Id, Text(manifest.Version), manifest.Publisher, manifest.DisplayNames, manifest.Descriptions,
            files, new(bytes), tree, manifest.Signature is null ? "unsigned" : "unverified", contributions,
            manifest.Runtime is { } runtime ? new(runtime.Kind, runtime.Entry, runtime.Interpreter, runtime.Protocol) : null,
            manifest.Permissions.Where(x => ExtensionPermissions.Find(x) is not null)
                .Select(x => ExtensionPermissions.Info(x, previous is not null && !previous.Permissions.Contains(x, StringComparer.Ordinal))).ToArray(),
            manifest.Hosts);
    }

    /// <summary>读取一个已安装扩展；记录、版本目录、清单或素材包有问题时标为损坏并写明原因。</summary>
    private InstalledExtension Describe(string directory, bool includePaths)
    {
        var id = Path.GetFileName(directory);
        var record = ReadRecord(directory);
        InstalledExtension Damaged(string? version, bool enabled, string problem, ExtensionPackageSummary? package = null) =>
            new(id, version, enabled, "damaged", package, "extension.installDamaged", problem, record?.InstalledAt, null, null);
        if (record is null) return Damaged(null, false, "安装记录缺失或损坏");
        var development = record.DevelopmentDirectory is not null;
        var installed = record.DevelopmentDirectory ?? Path.Combine(directory, record.Version);
        if (!Directory.Exists(installed)) return Damaged(record.Version, record.Enabled, development ? "开发目录不存在" : "安装目录缺失");
        ExtensionManifest manifest;
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(installed, ExtensionPackageInspector.ManifestName));
            if (bytes.Length > ExtensionPackageInspector.MaximumManifestBytes) return Damaged(record.Version, record.Enabled, "清单超过 64 KiB");
            manifest = ExtensionPackageInspector.ParseManifest(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Damaged(record.Version, record.Enabled, "清单无法读取");
        }
        catch (ExtensionPackageException error)
        {
            return Damaged(record.Version, record.Enabled, "清单无效：" + error.Message);
        }
        if (manifest.Id != id || !development && Text(manifest.Version) != record.Version)
            return Damaged(record.Version, record.Enabled, "清单与安装记录不符");
        var files = development ? Array.Empty<FileInfo>() : Directory.EnumerateFiles(installed, "*", SearchOption.AllDirectories).Select(x => new FileInfo(x)).ToArray();
        var package = Summary(manifest, files.Length, (ulong)files.Sum(x => x.Length), record.TreeSha256, null);
        var granted = Granted(manifest, record);
        var permissions = manifest.Permissions.Where(x => ExtensionPermissions.Find(x) is not null)
            .Select(x => ExtensionPermissions.State(x, granted.Contains(x))).ToArray();
        var runtime = manifest.Runtime is null ? null : RuntimeState?.Invoke(id) ?? new ExtensionRuntimeState("stopped",
            record.Enabled ? development && !DevelopmentMode ? "extension.developmentModeOff" : null : "extension.disabled",
            record.Enabled ? development && !DevelopmentMode ? "开发模式已关闭" : null : "扩展已停用", null, null, 0, null);
        string? packDirectory = null;
        if (manifest.AssetPack is { } assetPack)
        {
            var packFile = Path.GetFullPath(Path.Combine([installed, .. assetPack.Split('/')]));
            if (!PathWithin(packFile, installed) || !File.Exists(packFile))
                return Damaged(record.Version, record.Enabled, "素材包文件缺失：" + assetPack, package);
            packDirectory = Path.GetDirectoryName(packFile);
        }
        var usable = includePaths && record.Enabled;
        return new(id, record.Version, record.Enabled, "ready", package, null, null, record.InstalledAt,
            usable ? installed : null, usable ? packDirectory : null, manifest.Runtime is null ? null : permissions, runtime, development);
    }

    /// <summary>扩展自己的数据目录（键值与文件存储），卸载时随扩展目录一起删除。</summary>
    public string DataDirectory(string id) => Path.Combine(root, id, DataName);
    public string LogDirectory(string id) => Path.Combine(root, id, LogsName);
    public string TempDirectory(string id) => Path.Combine(root, id, TempName);

    private string Existing(string id)
    {
        var directory = IdPattern.IsMatch(id) ? Path.Combine(root, id) : null;
        return directory is not null && Directory.Exists(directory) ? directory
            : throw new OperationException("resource.notFound", "没有安装这个扩展");
    }

    /// <summary>在 <paramref name="work"/> 解出全部文件，按磁盘内容重算内容树哈希并与预览比对。</summary>
    private static async Task ExtractAsync(string package, string work, string expectedTree, CancellationToken ct)
    {
        Directory.CreateDirectory(work);
        IReadOnlyList<ImportSourceFile> entries;
        await using (var input = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            entries = await FilePackageReader.ReadAsync(input, ExtensionPackageInspector.MaximumExpandedBytes, ct);
        var hashes = new List<(string Path, string Sha256)>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var relative = FileNames.ArchivePath(entry.Name);
            var path = Path.GetFullPath(Path.Combine([work, .. relative.Split('/')]));
            if (!PathWithin(path, work)) throw new OperationException("extension.pathRejected", "扩展包条目路径越出安装目录");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = Convert.FromBase64String(entry.ContentBase64);
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await file.WriteAsync(content, ct);
                await file.FlushAsync(ct);
                file.Flush(true);
            }
            await using var written = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            hashes.Add((relative, Convert.ToHexStringLower(await SHA256.HashDataAsync(written, ct))));
        }
        var tree = new StringBuilder();
        foreach (var (path, sha) in hashes.OrderBy(x => x.Path, StringComparer.Ordinal)) tree.Append(path).Append('\0').Append(sha).Append('\n');
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tree.ToString()))) != expectedTree)
            throw new OperationException("extension.packageInvalid", "解出的内容与检查结果不一致");
    }

    private void RemoveFromInbox(ExtensionInstallPreview preview)
    {
        if (preview.Source == "inbox" && preview.InboxName is { } name) TryDelete(Path.Combine(inbox, name));
    }

    private void Expire()
    {
        foreach (var (id, preview) in previews.ToArray())
            if (time.GetElapsedTime(preview.CreatedAt) >= PreviewLifetime)
            {
                previews.Remove(id);
                TryDelete(preview.PackagePath);
            }
    }

    private static InstallRecord? ReadRecord(string directory)
    {
        var record = TryRead<InstallRecord>(Path.Combine(directory, StateFile));
        return record is { SchemaVersion: 1 } && VersionPattern.IsMatch(record.Version) && record.TreeSha256 is { Length: 64 } ? record : null;
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>同目录临时文件写入并刷盘后替换，读到的记录要么是旧的、要么是完整的新记录。</summary>
    private static void WriteAtomic<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(value, Json));
                file.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static bool PathWithin(string path, string parent) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Text(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        // 只删除普通文件与目录；遇到链接时删除链接本身，不跟随到外部。
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null || entry is FileInfo)
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
            else DeleteDirectory(entry.FullName);
        }
        Directory.Delete(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
