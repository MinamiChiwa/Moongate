using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Updates;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

/// <summary>旧位置迁入结果；SourceBootstrap 与 SourceDataRoot 是保持原样、待用户确认后清理的旧目录。</summary>
public sealed record LegacyDataImportResult(string SourceBootstrap, string SourceDataRoot, int Files, long Bytes);

public sealed partial class DataRootLease
{
    private const string ImportStagingName = "data.import", ImportNoteName = "legacy-import.json";

    /// <summary>工具根布局之前的默认引导目录 %LOCALAPPDATA%\MabiTool\bootstrap；只在工具根尚未初始化时检查是否存在。</summary>
    public static string LegacyBootstrapDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MabiTool", "bootstrap");

    /// <summary>
    /// 工具根下还没有引导配置、旧引导目录存在配置时，把旧的活动数据根整体复制到工具根的 data，逐个文件校验 SHA-256 后
    /// 写入新的身份标记与引导配置。旧位置只读不改，迁入后不再访问；工具根已初始化或旧位置不存在时返回 null。
    /// </summary>
    public static LegacyDataImportResult? ImportLegacyLayout(StorageOptions options, string legacyBootstrap)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("数据根当前支持 Windows");
        var bootstrap = StoragePaths.ValidateLocal(options.BootstrapDirectory);
        if (File.Exists(Path.Combine(bootstrap, "data-root.json"))) return null;
        if (!Path.IsPathFullyQualified(legacyBootstrap) || !File.Exists(Path.Combine(legacyBootstrap, "data-root.json"))) return null;
        try { return ImportLegacyCore(bootstrap, StoragePaths.ValidateLocal(legacyBootstrap)); }
        catch (StorageException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or SqliteException or UpdatePackageException)
        {
            throw new StorageException("storage.legacyImportFailed", "旧位置数据迁入工具根失败，旧位置保持原样；请检查旧目录可读、工具根可写后重试", error);
        }
    }

    [SupportedOSPlatform("windows")]
    private static LegacyDataImportResult ImportLegacyCore(string bootstrap, string legacyBootstrap)
    {
        var target = StoragePaths.ValidateLocal(StorageOptions.DefaultDataRoot(bootstrap));
        EnsurePrivateDirectory(bootstrap, true);
        using var guard = Lock(Path.Combine(bootstrap, "bootstrap.lock"), "storage.bootstrapBusy");
        var configPath = Path.Combine(bootstrap, "data-root.json");
        if (File.Exists(configPath)) throw new StorageException("storage.configurationConflict", "工具根已在迁入期间初始化");
        using var legacyGuard = OpenExistingLock(Path.Combine(legacyBootstrap, "bootstrap.lock"), "storage.bootstrapBusy");
        if (DataMaintenanceJournal.HasPending(legacyBootstrap))
            throw new StorageException("storage.legacyImportBlocked", "旧位置有未完成的数据维护，不能自动迁入；请先用原引导目录完成维护");
        var legacy = Read<BootstrapConfiguration>(Path.Combine(legacyBootstrap, "data-root.json"));
        if (legacy.Version != 1 || legacy.Product != "MabiTool" || !Guid.TryParseExact(legacy.RootId, "N", out _))
            throw new StorageException("storage.configurationInvalid", "旧位置的数据根配置版本、产品或身份无效");
        var source = StoragePaths.ValidateLocal(legacy.DataRoot);
        if (StoragePaths.IsWithin(source, Path.GetDirectoryName(bootstrap)!) || StoragePaths.IsWithin(Path.GetDirectoryName(bootstrap)!, source))
            throw new StorageException("storage.legacyImportBlocked", "旧数据根与工具根互相包含，不能自动迁入");
        var sourceMarkerPath = Path.Combine(source, MarkerName);
        if (!File.Exists(sourceMarkerPath)) throw new StorageException("storage.identityMismatch", "旧数据根缺少身份标记");
        var marker = Read<DataRootIdentity>(sourceMarkerPath);
        VerifyIdentity(marker, legacy.RootId);
        if (marker.State != "active" || marker.AuthorityBootstrap is not null && !SamePath(marker.AuthorityBootstrap, legacyBootstrap))
            throw new StorageException("storage.legacyImportBlocked", "旧数据根不是该引导目录的活动数据根，不能自动迁入");

        // 此前在这里的首次初始化在发布身份标记前中断，只留下初始化自身的内容：清掉后照常迁入。
        if (Directory.Exists(target) && !File.Exists(Path.Combine(target, MarkerName)) && Directory.EnumerateFileSystemEntries(target).Any() &&
            ForeignEntry(target) is null)
        {
            using (Lock(Path.Combine(target, "writer.lock"), "storage.writerBusy"))
                foreach (var temporary in Directory.EnumerateFiles(target, "*.tmp")) File.Delete(temporary);
            foreach (var entry in Directory.EnumerateFileSystemEntries(target))
                if (Directory.Exists(entry)) Directory.Delete(entry, false); else File.Delete(entry);
        }
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            // 上次迁入在发布数据目录之后、写引导配置之前中断：核对身份后补写引导配置（其间程序文件夹被搬走时改记当前位置）。
            var markerPath = Path.Combine(target, MarkerName);
            var resumed = File.Exists(markerPath) ? Read<DataRootIdentity>(markerPath) : null;
            var moved = resumed is not null && UnfinishedDefaultLayout(resumed, target, bootstrap, requireNoDatabase: false);
            if (resumed is null || resumed.RootId != marker.RootId || resumed.State != "active" || !moved && !SamePath(resumed.AuthorityBootstrap, bootstrap))
                throw new StorageException("storage.rootNotEmpty",
                    $"工具根的 data 目录已有其他内容（{(resumed is null ? ForeignEntry(target) ?? MarkerName : MarkerName)}），不能迁入旧数据；请把它移出后重新打开 Moongate");
            if (moved)
                using (Lock(Path.Combine(target, "writer.lock"), "storage.writerBusy"))
                    Publish(markerPath, resumed! with { Version = 2, AuthorityBootstrap = bootstrap }, overwrite: true);
            var existing = UpdatePaths.EnumerateFiles(target, CancellationToken.None);
            return Complete(bootstrap, legacyBootstrap, source, target, marker.RootId, existing.Count, existing.Sum(file => new FileInfo(file.Full).Length));
        }

        // 持有旧数据根的写锁与数据库锁：旧宿主仍在运行时拒绝，复制期间没有写入。
        using var writer = OpenExistingLock(Path.Combine(source, "writer.lock"), "storage.writerBusy");
        using var database = OpenExistingLock(Path.Combine(source, "store", "database.lock"), "storage.writerBusy");
        var files = UpdatePaths.EnumerateFiles(source, CancellationToken.None)
            .Where(file => file.Relative is not ("writer.lock" or "store/database.lock")).ToArray();
        var bytes = files.Sum(file => new FileInfo(file.Full).Length);
        if (new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace < bytes + 64L * 1024 * 1024)
            throw new StorageException("storage.diskSpaceInsufficient", "工具根所在磁盘空间不足，无法迁入旧数据");

        var staging = Path.Combine(Path.GetDirectoryName(target)!, ImportStagingName);
        UpdatePaths.DeleteOwnedTree(staging);
        LocalDiscoverySecurity.EnsureDirectory(staging);
        try
        {
            foreach (var (relative, full) in files)
            {
                var destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                EnsureDirectories(staging, Path.GetDirectoryName(destination)!);
                var expected = CopyHashed(full, destination);
                using var copy = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(copy), expected))
                    throw new StorageException("storage.legacyImportFailed", "迁入文件校验不一致，旧位置保持原样");
            }
            Publish(Path.Combine(staging, MarkerName), marker with { Version = 2, AuthorityBootstrap = bootstrap }, overwrite: true);
            RelocateTemporaryRegistrations(Path.Combine(staging, "store", "main.sqlite"), source, target);
            if (Directory.Exists(target)) Directory.Delete(target, false);
            Directory.Move(staging, target);
        }
        catch { UpdatePaths.DeleteOwnedTree(staging); throw; }
        return Complete(bootstrap, legacyBootstrap, source, target, marker.RootId, files.Length, bytes);
    }

    private static LegacyDataImportResult Complete(string bootstrap, string legacyBootstrap, string source, string target, string rootId, int files, long bytes)
    {
        var result = new LegacyDataImportResult(legacyBootstrap, source, files, bytes);
        Publish(Path.Combine(bootstrap, ImportNoteName), new { schemaVersion = 1, importedAt = DateTimeOffset.UtcNow, result.SourceBootstrap,
            result.SourceDataRoot, files, bytes, note = "旧位置未被删除；确认新数据正常后可手动清理" }, overwrite: true);
        Publish(Path.Combine(bootstrap, "data-root.json"), new BootstrapConfiguration(1, "MabiTool", target, rootId, false), overwrite: false);
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static FileStream? OpenExistingLock(string path, string code)
    {
        if (!File.Exists(path)) return null;
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException error) { throw new StorageException(code, "旧位置的数据仍被其他宿主占用；请先退出旧版宿主", error); }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureDirectories(string root, string directory)
    {
        var pending = new Stack<string>();
        for (var current = directory; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
            pending.Push(current);
        while (pending.TryPop(out var next)) LocalDiscoverySecurity.EnsureDirectory(next);
    }

    private static byte[] CopyHashed(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        for (int read; (read = input.Read(buffer)) > 0;)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        output.Flush(true);
        return hash.GetHashAndReset();
    }

    // 临时文件登记保存绝对路径；把原数据根 temp 下的登记改到新数据根，其余内容不变。
    private static void RelocateTemporaryRegistrations(string database, string source, string target)
    {
        if (!File.Exists(database)) return;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='platform_temporary_files'";
            if (Convert.ToInt64(probe.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0) return;
        }
        var paths = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT path FROM platform_temporary_files";
            using var rows = select.ExecuteReader();
            while (rows.Read()) paths.Add(rows.GetString(0));
        }
        var sourceTemporary = Path.Combine(source, "temp");
        using var transaction = connection.BeginTransaction();
        foreach (var path in paths.Where(path => StoragePaths.IsWithin(path, sourceTemporary) && path != sourceTemporary))
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE platform_temporary_files SET path=$target WHERE path=$path";
            update.Parameters.AddWithValue("$target", Path.Combine(target, "temp", Path.GetRelativePath(sourceTemporary, path)));
            update.Parameters.AddWithValue("$path", path);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
