using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MabiTool.Desktop.Localization;
using MabiTool.ModuleSdk;
using Reasons = MabiTool.ModuleSdk.HostStartupReport.Reasons;

namespace MabiTool.Desktop.Infrastructure;

/// <summary>一次宿主启动：启动时刻、退出码、运行时长、启动失败记录与标准错误尾部（至多 40 行、每行 1000 字）。线程安全。</summary>
internal sealed class HostLaunchRecord(DateTimeOffset startedAt)
{
    private const int MaximumLines = 40, MaximumLineLength = 1000;
    private readonly object gate = new();
    private readonly Queue<string> errors = new();
    private HostStartupReport? report;
    private int? exitCode;
    private TimeSpan ranFor;
    private string? launchError;

    public DateTimeOffset StartedAt { get; } = startedAt;
    public HostStartupReport? Report { get { lock (gate) return report; } }
    public int? ExitCode { get { lock (gate) return exitCode; } }
    public TimeSpan RanFor { get { lock (gate) return ranFor; } }
    public string? LaunchError { get { lock (gate) return launchError; } }
    public IReadOnlyList<string> ErrorTail { get { lock (gate) return errors.ToArray(); } }

    /// <summary>收到宿主标准错误的一行：启动失败记录单独解析，其余保留尾部。</summary>
    public void Error(string? line)
    {
        if (line is null) return;
        lock (gate)
        {
            if (HostStartupReport.TryParse(line) is { } parsed) { report = parsed; return; }
            errors.Enqueue(line.Length > MaximumLineLength ? line[..MaximumLineLength] : line);
            while (errors.Count > MaximumLines) errors.Dequeue();
        }
    }

    public void Finish(int? code, TimeSpan duration, string? error)
    {
        lock (gate) { exitCode = code; ranFor = duration; launchError = error; }
    }
}

/// <summary>宿主启动失败在“未连接后端”处的两句：状态行（约 16 字）与操作提示（约 20 字），按启动失败记录的原因分类给出，简繁两版。</summary>
internal sealed record StartupFailureText(string Status, string Hint)
{
    public string Line => UiText.Pick($"后端未能启动：{Status}。{Hint}。", $"後端未能啟動：{Status}。{Hint}。");

    public static StartupFailureText For(int exitCode, HostStartupReport? report)
    {
        var reason = report?.Reason ?? exitCode switch
        {
            2 => Reasons.Arguments, 3 => Reasons.StorageUnknown, 4 => Reasons.UpdateRecovery, 5 => Reasons.PortInUse, 6 => Reasons.PortUnavailable,
            _ => Reasons.Unknown
        };
        var (status, hint) = reason switch
        {
            Reasons.ProtectedLocation => (P("程序放在系统保护目录", "程式放在系統保護目錄"), P("把程序文件夹移到 D 盘等普通位置", "把程式資料夾移到 D 槽等一般位置")),
            Reasons.ControlledFolder => (P("写入被系统安全防护拦截", "寫入被系統安全防護攔截"), P("移出桌面，或在安全中心允许 Moongate", "移出桌面，或在安全中心允許 Moongate")),
            Reasons.NotWritable => (P("程序文件夹无法写入", "程式資料夾無法寫入"), P("移到可写文件夹或检查安全软件", "移到可寫資料夾或檢查安全軟體")),
            Reasons.WriteProtected => (P("磁盘处于写保护状态", "磁碟處於防寫狀態"), P("解除写保护或复制到其他磁盘", "解除防寫或複製到其他磁碟")),
            Reasons.NetworkDrive => (P("不支持放在网络位置", "不支援放在網路位置"), P("把程序文件夹复制到本机磁盘", "把程式資料夾複製到本機磁碟")),
            Reasons.UnsupportedDrive => (P("该磁盘不能保存数据", "該磁碟不能儲存資料"), P("复制到本机硬盘或 U 盘", "複製到本機硬碟或隨身碟")),
            Reasons.CloudSync => (P("程序位于同步盘目录", "程式位於同步碟目錄"), P("移到不同步的普通文件夹", "移到不同步的一般資料夾")),
            Reasons.LinkedPath => (P("路径经过联接或链接", "路徑經過連結"), P("移到普通文件夹后重新打开", "移到一般資料夾後重新開啟")),
            Reasons.PathInvalid => (P("路径含不支持的名称", "路徑含不支援的名稱"), P("把文件夹改成普通名称", "把資料夾改成一般名稱")),
            Reasons.OtherOwner => (P("数据属于其他 Windows 用户", "資料屬於其他 Windows 使用者"), P("用原用户打开，或按启动记录修复", "用原使用者開啟，或按啟動記錄修復")),
            Reasons.DataBusy => (P("数据正被另一个后端使用", "資料正被另一個後端使用"), P("关闭其他 Moongate 后重新打开", "關閉其他 Moongate 後重新開啟")),
            Reasons.DataMismatch => (P("数据文件夹不匹配", "資料資料夾不相符"), P("放回原来的 userdata 文件夹", "放回原本的 userdata 資料夾")),
            Reasons.DataMaintenance => (P("数据恢复尚未完成", "資料復原尚未完成"), P("重新打开 Moongate 继续恢复", "重新開啟 Moongate 繼續復原")),
            Reasons.LegacyImport => (P("旧数据迁入失败", "舊資料遷入失敗"), P("确认旧目录可读后重新打开", "確認舊目錄可讀後重新開啟")),
            Reasons.Arguments => (P("启动参数无效", "啟動參數無效"), P("直接双击 Moongate.exe 打开", "直接雙擊 Moongate.exe 開啟")),
            Reasons.UpdateRecovery => (P("上次更新未完成恢复", "上次更新未完成復原"), P("按使用说明手动升级", "按使用說明手動升級")),
            Reasons.PortInUse => (P("端口被另一份 Moongate 占用", "連接埠被另一份 Moongate 佔用"), P("关闭另一份 Moongate 后重新打开", "關閉另一份 Moongate 後重新開啟")),
            Reasons.PortUnavailable => (P("指定的端口不可用", "指定的連接埠不可用"), P("清除 MABITOOL_API_PORT 设置", "清除 MABITOOL_API_PORT 設定")),
            Reasons.StorageUnknown => (P("数据目录不可用", "資料目錄不可用"), P("在诊断页导出启动记录反馈", "在診斷頁匯出啟動記錄回報")),
            _ => (P("后端启动出错", "後端啟動出錯"), P("在诊断页导出启动记录反馈", "在診斷頁匯出啟動記錄回報"))
        };
        return new(status, hint);
    }

    private static string P(string simplified, string traditional) => UiText.Pick(simplified, traditional);
}

/// <summary>
/// 不依赖后端的“导出启动记录”：宿主每次启动的退出码、失败记录与标准错误尾部，以及工具根是否可写、所在磁盘与文件系统、
/// 是否以管理员身份运行、Windows 与程序版本。用户名、计算机名、用户配置文件路径与当前用户 SID 替换为占位文字。
/// </summary>
internal static class StartupRecordExport
{
    public static string FileName(DateTimeOffset now) => $"moongate-startup-{now:yyyyMMdd-HHmmss}.txt";

    public static byte[] Build(DateTimeOffset now) => Encoding.UTF8.GetBytes(Sanitize(Compose(now, PackagedHost.Launches, DesktopFiles.Root,
        AppContext.BaseDirectory, DesktopFiles.Problem)));

    internal static string Compose(DateTimeOffset now, IReadOnlyList<HostLaunchRecord> launches, string toolRoot, string programDirectory, string? frontendProblem)
    {
        var text = new StringBuilder();
        void Line(string label, string? value) => text.Append(label).Append("：").AppendLine(value ?? "未知");
        text.AppendLine("Moongate 启动记录");
        Line("导出时间", now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        Line("程序版本", Version(programDirectory));
        Line("Windows", $"{RuntimeInformation.OSDescription}（{Environment.OSVersion.Version}，{RuntimeInformation.OSArchitecture}）");
        Line("以管理员身份运行", Elevated() switch { true => "是", false => "否", null => "未知" });
        var package = ToolRoot.PackageRoot(programDirectory);
        Line("程序文件夹", package ?? programDirectory);
        Line("工具根", toolRoot);
        Line("工具根存在", Directory.Exists(toolRoot) ? "是" : "否");
        var probe = Writable(Existing(toolRoot));
        Line("工具根可写（前端进程）", probe is null ? "是" : "否（" + probe + "）");
        var root = Path.GetPathRoot(Path.GetFullPath(toolRoot));
        if (!string.IsNullOrEmpty(root))
        {
            string? type = null;
            try { type = new DriveInfo(root).DriveType.ToString(); } catch (ArgumentException) { }
            Line("磁盘", root + (type is null ? string.Empty : $"（{type}）"));
            if (OperatingSystem.IsWindows())
            {
                Line("文件系统", LocalDiscoverySecurity.FileSystemName(root));
                Line("保存访问控制", LocalDiscoverySecurity.SupportsAccessControl(root) ? "是" : "否");
            }
        }
        Line("位置", Location(package ?? toolRoot));
        if (OperatingSystem.IsWindows())
            foreach (var name in new[] { ToolRoot.BootstrapName, ToolRoot.DataName, ToolRoot.RuntimeName, ToolRoot.DesktopName })
                Line($"{name} 所有者", Owner(Path.Combine(toolRoot, name)));
        Line("前端发现的问题", frontendProblem ?? "无");
        text.AppendLine();
        text.AppendLine(launches.Count == 0 ? "本次运行没有由前端启动宿主（宿主已在运行，或不是解压版目录布局）。" : $"宿主启动 {launches.Count} 次：");
        foreach (var launch in launches)
        {
            text.AppendLine();
            Line("启动时间", launch.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Line("退出码", launch.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? (launch.LaunchError is null ? "仍在运行" : "未能启动"));
            Line("运行时长", launch.RanFor.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒");
            if (launch.LaunchError is { } error) Line("启动错误", error);
            if (launch.Report is { } report)
            {
                Line("原因码", report.Code);
                Line("原因分类", report.Reason);
                Line("原因说明", report.Message);
            }
            if (launch.ErrorTail.Count > 0)
            {
                text.AppendLine("标准错误：");
                foreach (var line in launch.ErrorTail) text.Append("  ").AppendLine(line);
            }
        }
        return text.ToString();
    }

    /// <summary>替换用户配置文件路径、用户名、计算机名与当前用户 SID。</summary>
    internal static string Sanitize(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows() && WindowsIdentity.GetCurrent().User?.Value is { } sid) text = text.Replace(sid, "<当前用户SID>", StringComparison.OrdinalIgnoreCase);
        foreach (var (value, placeholder) in new[] { (Environment.UserName, "<用户名>"), (Environment.MachineName, "<计算机名>"), (Environment.UserDomainName, "<域>") })
            if (value is { Length: >= 2 }) text = text.Replace(value, placeholder, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string? Version(string programDirectory)
    {
        if (ToolRoot.PackageRoot(programDirectory) is { } package)
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package, "components", "release.json")));
                if (document.RootElement.TryGetProperty("displayVersion", out var version) && version.ValueKind == JsonValueKind.String) return version.GetString();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return typeof(StartupRecordExport).Assembly.GetName().Version?.ToString();
    }

    private static bool? Elevated()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string Existing(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current) && Path.GetDirectoryName(current) is { } parent) current = parent;
        return current;
    }

    /// <summary>新建并删除一个探测文件；可写时为空，否则为错误说明。</summary>
    private static string? Writable(string directory)
    {
        try
        {
            using (new FileStream(Path.Combine(directory, ".write-check-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{error.GetType().Name} 0x{error.HResult:X8}：{error.Message}";
        }
    }

    private static string Location(string folder)
    {
        static bool Under(string path, Environment.SpecialFolder folder) =>
            Environment.GetFolderPath(folder) is { Length: > 0 } known &&
            (path + Path.DirectorySeparatorChar).StartsWith(Path.TrimEndingDirectorySeparator(known) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Under(full, Environment.SpecialFolder.ProgramFiles) || Under(full, Environment.SpecialFolder.ProgramFilesX86) || Under(full, Environment.SpecialFolder.Windows))
            return "系统保护目录（Program Files、Windows）";
        if (Under(full, Environment.SpecialFolder.DesktopDirectory)) return "桌面（受控文件夹访问保护的位置）";
        if (Under(full, Environment.SpecialFolder.MyDocuments) || Under(full, Environment.SpecialFolder.MyPictures) ||
            Under(full, Environment.SpecialFolder.MyVideos) || Under(full, Environment.SpecialFolder.MyMusic))
            return "文档、图片等（受控文件夹访问保护的位置）";
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? "网络位置" : "普通位置";
    }

    private static string Owner(string directory)
    {
        if (!OperatingSystem.IsWindows()) return "未知";
        if (!Directory.Exists(directory)) return "不存在";
        try
        {
            var owner = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            var user = WindowsIdentity.GetCurrent().User;
            if (owner is null) return "无法读取";
            if (user is not null && user.Equals(owner)) return "当前用户";
            if (owner.Value == "S-1-5-32-544") return "Administrators 组";
            return "其他账户 " + owner.Value;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidOperationException) { return "无法读取（" + error.GetType().Name + "）"; }
    }
}
