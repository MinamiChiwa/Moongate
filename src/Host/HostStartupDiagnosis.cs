using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using Reasons = MabiTool.ModuleSdk.HostStartupReport.Reasons;

namespace MabiTool.Host;

/// <summary>
/// 存储类启动失败（退出码 3）的原因细分：按异常与原因码判断，再实地检查程序文件夹（能否写入、所在磁盘与位置、数据目录的所有者），
/// 给出用户能处理的原因分类与中文做法。结果随 <see cref="HostStartupReport"/> 写到标准错误。
/// </summary>
public static class HostStartupDiagnosis
{
    private const int AccessDenied = 5, WriteProtect = 19;

    /// <param name="toolRoot">工具根（目录包为包根下的 userdata）；启动参数解析前失败时为空，按程序目录推出。</param>
    public static HostStartupReport Describe(Exception error, string code, string message, string? toolRoot)
    {
        if (!OperatingSystem.IsWindows()) return new(code, Reasons.StorageUnknown, HostStartupFailure.StorageExitCode, message);
        toolRoot ??= ToolRoot.FromProgramDirectory(AppContext.BaseDirectory);
        var (reason, detail) = Classify(error, code, message, toolRoot);
        return new(code, reason, HostStartupFailure.StorageExitCode, detail);
    }

    [SupportedOSPlatform("windows")]
    private static (string Reason, string Message) Classify(Exception error, string code, string message, string toolRoot)
    {
        var folder = ProgramFolder(toolRoot);
        if (Find<LinkedPathException>(error) is { } linked)
            return linked.CloudPlaceholder
                ? (Reasons.CloudSync, $"程序文件夹位于 OneDrive 等同步盘的目录中（{linked.LinkedPath}），同步程序会在后台上传或替换数据库文件。" +
                                      @"请把整个 Moongate 文件夹移到不同步的普通文件夹（例如 D:\Moongate）后再打开。")
                : (Reasons.LinkedPath, $"程序文件夹的路径经过联接或符号链接（{linked.LinkedPath}），为防止数据被写到别处，后端拒绝启动。" +
                                       "请把 Moongate 文件夹移到不经过链接的普通文件夹后再打开。");
        switch (code)
        {
            case "storage.bootstrapBusy" or "storage.writerBusy" or "resource.busy":
                return (Reasons.DataBusy, "程序文件夹里的数据正被另一个 Moongate 后端使用。请关闭其他正在运行的 Moongate" +
                                          "（或在任务管理器的“详细信息”里结束命令行含 MabiTool.Host 的 dotnet.exe）后重新打开。");
            // 数据目录里有初始化之外的内容：原因已写明是哪一项与做法。中断的首次初始化只留下初始化内容，会自动续做，不会走到这里。
            case "storage.rootNotEmpty":
                return (Reasons.DataMismatch, message);
            case "storage.identityMismatch" or "storage.rootMissing" or "storage.configurationInvalid" or "storage.configurationConflict" or
                "storage.databaseIdentityMismatch" or "storage.notInitialized":
                return (Reasons.DataMismatch, $"{message}。请放回原来的 userdata 文件夹；不需要原有数据时，可把 userdata 改名留作备份，再重新打开 Moongate 新建数据。");
            case "storage.maintenanceRequired":
                return (Reasons.DataMaintenance, $"{message}。重新打开 Moongate 会按维护日志继续恢复；仍然失败时请在诊断页导出启动记录反馈。");
            case "storage.legacyImportFailed" or "storage.legacyImportBlocked":
                return (Reasons.LegacyImport, message);
            case "storage.otherOwner":
                return (Reasons.OtherOwner, message);
            case "storage.unsupportedVolume":
                return Volume(folder, message);
            case "storage.pathInvalid":
                return folder.StartsWith(@"\\", StringComparison.Ordinal)
                    ? Volume(folder, message)
                    : (Reasons.PathInvalid, $"{message}。程序文件夹路径中的文件夹名不能以空格或句点结尾，也不能是 CON、NUL 等系统保留名称；请改成普通名称后再打开。");
        }
        if (Volume(folder, message) is { Reason: not Reasons.StorageUnknown } volume) return volume;
        var probe = ProbeWrite(Existing(toolRoot));
        if (probe == WriteProtect)
            return (Reasons.WriteProtected, "程序所在的磁盘处于写保护状态（U 盘或存储卡的写保护开关、只读介质）。请解除写保护，或把 Moongate 文件夹复制到可写的磁盘后再打开。");
        if (probe == AccessDenied || probe != 0 && Find<UnauthorizedAccessException>(error) is not null)
        {
            if (IsUnder(folder, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
                    Environment.SpecialFolder.CommonProgramFiles))
                return (Reasons.ProtectedLocation, $"程序文件夹位于“{folder}”，这是系统保护的目录，普通权限不能写入。Moongate 的数据保存在程序文件夹内，" +
                                                   @"请把整个 Moongate 文件夹移到 D:\Moongate 之类的普通文件夹后再打开。");
            if (IsUnder(folder, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
                    Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.Favorites,
                    Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonDocuments))
                return (Reasons.ControlledFolder, $"无法在“{folder}”中写入。桌面、文档、图片等文件夹受 Windows“受控文件夹访问”保护，未被允许的程序写入会被 Windows 安全中心或安全软件拦截。" +
                                                  @"请把整个 Moongate 文件夹移到 D:\Moongate 之类的普通文件夹；或在“Windows 安全中心 → 病毒和威胁防护 → 勒索软件防护 → 允许应用通过受控文件夹访问”中" +
                                                  @"添加 components\dotnet-runtime\dotnet.exe 与 components\desktop\MabiTool.Desktop.exe，再重新打开。");
            return (Reasons.NotWritable, $"无法在“{folder}”中写入。请确认该文件夹没有被设为只读、没有被安全软件拦截，或把整个 Moongate 文件夹移到当前用户可写的普通文件夹后再打开。");
        }
        if (probe == 0 && OtherOwner(toolRoot) is { } owned)
            return (Reasons.OtherOwner, $"程序文件夹里的“{owned}”属于其他 Windows 用户或另一台电脑，当前用户不能使用。请用原来的 Windows 用户启动；" +
                                        $"或以管理员身份打开命令提示符，依次运行 takeown /f \"{toolRoot}\" /r /d y 与 icacls \"{toolRoot}\" /reset /t /q，再重新打开 Moongate。");
        return (Reasons.StorageUnknown, message);
    }

    // 网络位置、光盘与内存盘：数据库不能放在这些位置。
    [SupportedOSPlatform("windows")]
    private static (string Reason, string Message) Volume(string folder, string message)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal))
            return (Reasons.NetworkDrive, NetworkMessage);
        DriveType type;
        try { type = new DriveInfo(Path.GetPathRoot(folder)!).DriveType; }
        catch (ArgumentException) { return (Reasons.StorageUnknown, message); }
        if (type == DriveType.Network) return (Reasons.NetworkDrive, NetworkMessage);
        if (type is DriveType.CDRom or DriveType.Ram or DriveType.NoRootDirectory or DriveType.Unknown)
            return (Reasons.UnsupportedDrive, "程序所在的磁盘类型（光盘、内存盘等）不能保存数据。请把 Moongate 文件夹复制到本机硬盘或 U 盘后再打开。");
        return message.Contains("云同步", StringComparison.Ordinal)
            ? (Reasons.CloudSync, @"程序文件夹位于 OneDrive 同步目录中，同步程序会在后台上传或替换数据库文件。请把整个 Moongate 文件夹移到不同步的普通文件夹（例如 D:\Moongate）后再打开。")
            : (Reasons.StorageUnknown, message);
    }

    private const string NetworkMessage = "程序文件夹位于网络位置（映射的网络驱动器或共享文件夹），数据库不能放在网络位置。请把整个 Moongate 文件夹复制到本机硬盘或 U 盘后再打开。";

    /// <summary>界面与提示里说的“程序文件夹”：目录包为包根，其余为工具根本身。</summary>
    private static string ProgramFolder(string toolRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(toolRoot));
        return Path.GetFileName(full).Equals(ToolRoot.PackageStateName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(full) ?? full : full;
    }

    private static string Existing(string path)
    {
        for (var current = Path.GetFullPath(path); ; )
        {
            if (Directory.Exists(current)) return current;
            var parent = Path.GetDirectoryName(current);
            if (parent is null) return current;
            current = parent;
        }
    }

    /// <summary>在目录里新建并删除一个探测文件；返回 Win32 错误码，0 表示可写。</summary>
    public static int ProbeWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".write-check-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return 0;
        }
        catch (UnauthorizedAccessException) { return AccessDenied; }
        catch (IOException error) { return (error.HResult & 0xffff) is var win32 && win32 != 0 ? win32 : -1; }
    }

    [SupportedOSPlatform("windows")]
    private static string? OtherOwner(string toolRoot)
    {
        if (!LocalDiscoverySecurity.SupportsAccessControl(toolRoot)) return null;
        var user = WindowsIdentity.GetCurrent().User;
        if (user is null) return null;
        foreach (var name in new[] { ToolRoot.BootstrapName, ToolRoot.DataName, ToolRoot.RuntimeName })
        {
            var directory = new DirectoryInfo(Path.Combine(toolRoot, name));
            if (!directory.Exists) continue;
            try
            {
                var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
                if (!LocalDiscoverySecurity.IsAcceptedOwner(owner, user)) return Path.Combine(ToolRoot.PackageStateName, name);
                _ = directory.EnumerateFileSystemInfos().FirstOrDefault();
            }
            catch (UnauthorizedAccessException) { return Path.Combine(ToolRoot.PackageStateName, name); }
            catch (IOException) { }
        }
        return null;
    }

    private static bool IsUnder(string path, params Environment.SpecialFolder[] folders) =>
        folders.Select(Environment.GetFolderPath).Where(folder => folder.Length > 0)
            .Any(folder => path.Equals(Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase) ||
                           path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static T? Find<T>(Exception? error) where T : Exception
    {
        for (; error is not null; error = error.InnerException)
            if (error is T found) return found;
        return null;
    }
}
