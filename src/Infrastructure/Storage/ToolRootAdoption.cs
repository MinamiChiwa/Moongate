using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.ModuleSdk;
using Microsoft.Data.Sqlite;

namespace MabiTool.Infrastructure;

/// <summary>工具根接管结果：<paramref name="PreviousDataRoot"/> 为引导配置原先记录的数据根（未改写时为空），<paramref name="AccessRepaired"/> 表示修复了目录权限。</summary>
public sealed record ToolRootAdoption(string? PreviousDataRoot, bool AccessRepaired);

public sealed partial class DataRootLease
{
    /// <summary>
    /// 工具根布局（工具根下的 bootstrap 与同级 data：不带参数，或只用 --portable-root 指定工具根）整体搬移、改名、复制或换 Windows 用户后，
    /// 在打开数据根之前接管当前位置：
    /// <list type="number">
    /// <item>bootstrap、data、runtime 不是当前用户独占时（复制会继承新位置的权限，换用户会改变所有者），改为当前用户独占；</item>
    /// <item>引导配置记录的数据根不是同级 data，而同级 data 的身份标记与配置是同一 rootId 的活动数据根、原记录也是默认布局时，
    /// 把配置、身份标记的权威引导目录与数据库里的临时文件登记改到当前位置；</item>
    /// <item>数据根在工具根之外（经数据根迁移选定）且身份标记记录的权威引导目录已不存在时，把权威引导目录改为当前位置。</item>
    /// </list>
    /// 身份不一致时不改写任何内容。用 --data-root 或引导目录环境变量指定位置时不接管，返回 null；没有需要接管的内容时同样返回 null。
    /// </summary>
    public static ToolRootAdoption? AdoptToolRootLayout(StorageOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("数据根当前支持 Windows");
        if (!options.LocatedByToolRoot) return null;
        try { return AdoptCore(StoragePaths.ValidateLocal(options.BootstrapDirectory)); }
        catch (StorageException) { throw; }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or System.Security.Principal.IdentityNotMappedException)
        {
            var state = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(options.BootstrapDirectory)) ?? options.BootstrapDirectory;
            throw new StorageException("storage.otherOwner",
                "程序文件夹里的数据属于其他 Windows 用户或另一台电脑，当前用户无权读取或改为自己独占。请用原来的 Windows 用户启动；" +
                $"或以管理员身份打开命令提示符，依次运行 takeown /f \"{state}\" /r /d y 与 icacls \"{state}\" /reset /t /q，再重新打开 Moongate", error);
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or SqliteException)
        {
            throw new StorageException("storage.unavailable", $"程序文件夹里的数据无法在新位置接管：{error.Message}", error);
        }
    }

    [SupportedOSPlatform("windows")]
    private static ToolRootAdoption? AdoptCore(string bootstrap)
    {
        var toolRoot = Path.GetDirectoryName(bootstrap)!;
        var data = StoragePaths.ValidateLocal(StorageOptions.DefaultDataRoot(bootstrap));
        var repaired = false;
        foreach (var directory in new[] { bootstrap, data, Path.Combine(toolRoot, ToolRoot.RuntimeName) })
        {
            if (!Directory.Exists(directory)) continue;
            if (!LocalDiscoverySecurity.IsPrivate(directory))
            {
                LocalDiscoverySecurity.MakePrivate(directory);
                repaired = true;
            }
            // 从光盘、只读压缩包或其他电脑复制来的文件可能带只读属性，宿主打开锁文件与数据库时会被拒绝写入。
            if (new DirectoryInfo(directory).EnumerateFiles().Any(file => file.IsReadOnly))
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    if (file.IsReadOnly) file.IsReadOnly = false;
                repaired = true;
            }
        }
        var configPath = Path.Combine(bootstrap, "data-root.json");
        if (!File.Exists(configPath) || DataMaintenanceJournal.HasPending(bootstrap)) return repaired ? new(null, true) : null;
        string? previous = null;
        using (Lock(Path.Combine(bootstrap, "bootstrap.lock"), "storage.bootstrapBusy"))
        {
            var config = Read<BootstrapConfiguration>(configPath);
            if (config.Version != 1 || config.Product != "MabiTool" || !Guid.TryParseExact(config.RootId, "N", out _))
                return repaired ? new(null, true) : null;
            if (!SamePath(config.DataRoot, data)) previous = AdoptDefaultDataRoot(bootstrap, data, configPath, config);
            else AdoptAuthority(bootstrap, data, config);
            if (previous is null && !SamePath(config.DataRoot, data) && Directory.Exists(config.DataRoot))
                AdoptAuthority(bootstrap, StoragePaths.ValidateLocal(config.DataRoot), config);
        }
        return previous is null && !repaired ? null : new(previous, repaired);
    }

    /// <summary>同级 data 是配置所记录数据根搬来的那一份时改写登记，返回原数据根；不是时返回 null，交由正常启动报告。</summary>
    [SupportedOSPlatform("windows")]
    private static string? AdoptDefaultDataRoot(string bootstrap, string data, string configPath, BootstrapConfiguration config)
    {
        var markerPath = Path.Combine(data, MarkerName);
        if (!File.Exists(markerPath)) return null;
        var marker = Read<DataRootIdentity>(markerPath);
        var original = config.DataRoot;
        var originalBootstrap = Path.Combine(Path.GetDirectoryName(original) ?? original, ToolRoot.BootstrapName);
        if (marker.RootId != config.RootId)
        {
            // 原位置已不存在、同级 data 却是另一份数据：不接管，也不让启动落到空库。
            if (!Directory.Exists(original))
                throw new StorageException("storage.identityMismatch", "程序文件夹中的 data 与引导配置不属于同一份数据，未自动接管；请放回原来的 data 文件夹");
            return null;
        }
        // 只接管默认布局：原记录是同级 data，身份标记是活动数据根，权威引导目录是原同级 bootstrap 或当前位置。
        if (!Path.GetFileName(original).Equals(ToolRoot.DataName, StringComparison.OrdinalIgnoreCase) || marker.State != "active" ||
            marker.AuthorityBootstrap is not null && !SamePath(marker.AuthorityBootstrap, originalBootstrap) && !SamePath(marker.AuthorityBootstrap, bootstrap))
            return null;
        using (Lock(Path.Combine(data, "writer.lock"), "storage.writerBusy"))
        {
            var locked = Read<DataRootIdentity>(markerPath);
            if (locked != marker) throw new StorageException("storage.identityMismatch", "接管期间数据根身份已改变");
            RelocateTemporaryRegistrations(Path.Combine(data, "store", "main.sqlite"), original, data);
            if (!SamePath(marker.AuthorityBootstrap, bootstrap))
                Publish(markerPath, marker with { Version = 2, AuthorityBootstrap = bootstrap }, overwrite: true);
            Publish(configPath, config with { DataRoot = data, LaunchRoot = null }, overwrite: true);
        }
        return original;
    }

    /// <summary>数据根的身份标记记录的权威引导目录已不存在（工具根被搬走）且配置与标记身份一致时，把权威引导目录改为当前位置。</summary>
    [SupportedOSPlatform("windows")]
    private static void AdoptAuthority(string bootstrap, string root, BootstrapConfiguration config)
    {
        var markerPath = Path.Combine(root, MarkerName);
        if (!File.Exists(markerPath)) return;
        var marker = Read<DataRootIdentity>(markerPath);
        if (marker.RootId != config.RootId || marker.State != "active" || marker.AuthorityBootstrap is null ||
            SamePath(marker.AuthorityBootstrap, bootstrap) || Directory.Exists(marker.AuthorityBootstrap) ||
            !Path.GetFileName(marker.AuthorityBootstrap).Equals(ToolRoot.BootstrapName, StringComparison.OrdinalIgnoreCase))
            return;
        using (Lock(Path.Combine(root, "writer.lock"), "storage.writerBusy"))
        {
            if (Read<DataRootIdentity>(markerPath) != marker) throw new StorageException("storage.identityMismatch", "接管期间数据根身份已改变");
            Publish(markerPath, marker with { Version = 2, AuthorityBootstrap = bootstrap }, overwrite: true);
        }
    }
}
