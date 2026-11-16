using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MabiTool.Contracts;
using MabiTool.Infrastructure;

namespace MabiTool.Host.Extensions;

/// <summary>
/// 管理数据根下的 extensions/：只安装纯数据扩展（清单、图片、数据文件），不执行包内任何内容。
/// 安装先生成预览，确认后在 staging/ 解出、复核内容树哈希，再以同卷改名发布到 &lt;标识&gt;/&lt;版本&gt;/；
/// 进程中断留下的暂存与未完成发布由 <see cref="Initialize"/> 在下次启动时收尾。
/// </summary>
public sealed class ExtensionInstaller : IDisposable
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);
    public const int MaximumPreviews = 16, MaximumListed = 64;
    public const string CodeNotSupportedMessage = "带代码的扩展需要能力代理，尚未支持";
    public const string InboxName = "inbox", StagingName = "staging";
    private const string StateFile = "state.json", MarkerFile = "installing.json";

    private static readonly HashSet<string> DataSuffixes = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".txt", ".md", ".csv", ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9-]{0,31}\\.[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options) { WriteIndented = true };

    private sealed record Preview(ExtensionInstallPreview Snapshot, string PackagePath, ExtensionPackageInspection Inspection, long CreatedAt);
    private sealed record InstallRecord(int SchemaVersion, string Version, bool Enabled, string TreeSha256, DateTimeOffset InstalledAt);
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
        return new(installed, pending, includePaths ? root : null, includePaths ? inbox : null);
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
            RequireDataOnly(inspection);
            var (action, installedVersion) = Decide(inspection);
            var snapshot = new ExtensionInstallPreview(id, source, inboxName, Summary(inspection), action, installedVersion,
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
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, version);
            if (Directory.Exists(target)) DeleteDirectory(target);
            var markerPath = Path.Combine(directory, MarkerFile);
            WriteAtomic(markerPath, new InstallingMarker(1, version));
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(work, target);
                WriteAtomic(Path.Combine(directory, StateFile), new InstallRecord(1, version, previous?.Enabled ?? false,
                    preview.Inspection.TreeSha256, time.GetUtcNow()));
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
            return Describe(directory, includePaths);
        }
        catch
        {
            if (Directory.Exists(work)) DeleteDirectory(work);
            TryDelete(preview.PackagePath);
            throw;
        }
        finally { gate.Release(); }
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
            return Describe(directory, includePaths);
        }
        finally { gate.Release(); }
    }

    /// <summary>卸载：先把扩展目录整体移入 staging/ 再删除，结果只有“已卸载”或“未变”。</summary>
    public async Task<ExtensionUninstallReceipt> UninstallAsync(string id, CancellationToken ct)
    {
        var directory = Existing(id);
        if (!await gate.WaitAsync(0, ct)) throw new OperationException("resource.busy", "另一个扩展操作正在进行");
        try
        {
            var version = ReadRecord(directory)?.Version;
            Directory.CreateDirectory(staging);
            var removed = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".removed");
            try { Directory.Move(directory, removed); }
            catch (IOException) { throw new OperationException("resource.busy", "扩展文件正在被使用，请稍后再卸载"); }
            catch (UnauthorizedAccessException) { throw new OperationException("resource.busy", "扩展文件正在被使用，请稍后再卸载"); }
            // 已移出安装位置即视为卸载完成；删不掉的残留在下次启动清空 staging/ 时回收。
            try { DeleteDirectory(removed); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new(id, version);
        }
        finally { gate.Release(); }
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

    private static void RequireDataOnly(ExtensionPackageInspection inspection)
    {
        var manifest = inspection.Manifest;
        if (inspection.ContainsExecutableCode || manifest.Provides.Count > 0 || manifest.Permissions.Count > 0 || manifest.HighRiskBehaviors.Count > 0)
            throw new OperationException("extension.codeNotSupported", CodeNotSupportedMessage);
        if (inspection.Files.FirstOrDefault(file => !DataSuffixes.Contains(Path.GetExtension(file.Path))) is { } other)
            throw new OperationException("extension.fileTypeRejected", "扩展只能包含清单、图片与数据文件，不支持：" + other.Path);
    }

    private (string Action, string? InstalledVersion) Decide(ExtensionPackageInspection inspection)
    {
        var manifest = inspection.Manifest;
        var record = ReadRecord(Path.Combine(root, manifest.Id));
        if (record is null) return ("install", null);
        var installed = Version.Parse(record.Version);
        if (manifest.Version > installed) return ("upgrade", record.Version);
        if (manifest.Version < installed)
            throw new OperationException("extension.downgradeRejected", $"已安装更高的版本 {record.Version}，请先卸载再安装 {Text(manifest.Version)}");
        if (record.TreeSha256 != inspection.TreeSha256)
            throw new OperationException("extension.versionConflict", $"已安装的 {record.Version} 与这个包内容不同，同一版本不能替换，请提高版本号");
        return ("reinstall", record.Version);
    }

    private static ExtensionPackageSummary Summary(ExtensionPackageInspection inspection)
    {
        var manifest = inspection.Manifest;
        return new(manifest.Id, Text(manifest.Version), manifest.Publisher, manifest.DisplayNames, manifest.Descriptions,
            inspection.Files.Count, new((ulong)inspection.Files.Sum(x => x.Size)), inspection.TreeSha256,
            manifest.Signature is null ? "unsigned" : "unverified", manifest.AssetPack is null ? [] : ["assetPack"]);
    }

    /// <summary>读取一个已安装扩展；记录、版本目录、清单或素材包有问题时标为损坏并写明原因。</summary>
    private InstalledExtension Describe(string directory, bool includePaths)
    {
        var id = Path.GetFileName(directory);
        var record = ReadRecord(directory);
        InstalledExtension Damaged(string? version, bool enabled, string problem, ExtensionPackageSummary? package = null) =>
            new(id, version, enabled, "damaged", package, "extension.installDamaged", problem, record?.InstalledAt, null, null);
        if (record is null) return Damaged(null, false, "安装记录缺失或损坏");
        var installed = Path.Combine(directory, record.Version);
        if (!Directory.Exists(installed)) return Damaged(record.Version, record.Enabled, "安装目录缺失");
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
        if (manifest.Id != id || Text(manifest.Version) != record.Version)
            return Damaged(record.Version, record.Enabled, "清单与安装记录不符");
        var files = Directory.EnumerateFiles(installed, "*", SearchOption.AllDirectories).Select(x => new FileInfo(x)).ToArray();
        var package = new ExtensionPackageSummary(id, record.Version, manifest.Publisher, manifest.DisplayNames, manifest.Descriptions,
            files.Length, new((ulong)files.Sum(x => x.Length)), record.TreeSha256,
            manifest.Signature is null ? "unsigned" : "unverified", manifest.AssetPack is null ? [] : ["assetPack"]);
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
            usable ? installed : null, usable ? packDirectory : null);
    }

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
