using System.Text.Json;

namespace MabiTool.ModuleSdk;

/// <summary>
/// 宿主启动失败记录：宿主退出前把它作为一行写到标准错误（<see cref="Prefix"/> 加 JSON，非 ASCII 字符转义），
/// 拉起宿主的前端读取后按 <see cref="Reason"/> 显示针对性的说明，并放进“导出启动记录”。工具根不可写时也能送达。
/// </summary>
/// <param name="Code">宿主内部原因码（例如 <c>storage.permissionDenied</c>），只用于记录。</param>
/// <param name="Reason">面向前端的原因分类，取值见 <see cref="Reasons"/>；前端按它选择界面文字。</param>
/// <param name="ExitCode">宿主退出码（见宿主 <c>HostStartupFailure</c>）。</param>
/// <param name="Message">中文详细原因与做法，可能含本机路径；导出时由前端替换用户名等隐私。</param>
public sealed record HostStartupReport(string Code, string Reason, int ExitCode, string Message)
{
    public const string Prefix = "moongate.startup.failure ";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Format() => Prefix + JsonSerializer.Serialize(this, Json);

    /// <summary>从标准错误的一行解析；不是启动失败记录或格式无效时返回 null。</summary>
    public static HostStartupReport? TryParse(string? line)
    {
        if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal) || line.Length > 16384) return null;
        try
        {
            var report = JsonSerializer.Deserialize<HostStartupReport>(line.AsSpan(Prefix.Length), Json);
            return report is { Code.Length: > 0, Reason.Length: > 0, Message: not null } ? report : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>原因分类。存储类（退出码 3）细分到用户能处理的程度；其余退出码各一类。</summary>
    public static class Reasons
    {
        /// <summary>程序文件夹在 Program Files、Windows 等系统保护目录。</summary>
        public const string ProtectedLocation = "toolRoot.protectedLocation";
        /// <summary>程序文件夹在桌面、文档等“受控文件夹访问”保护的位置且写入被拒绝。</summary>
        public const string ControlledFolder = "toolRoot.controlledFolder";
        /// <summary>程序文件夹不可写（权限、安全软件或只读属性）。</summary>
        public const string NotWritable = "toolRoot.notWritable";
        /// <summary>磁盘写保护。</summary>
        public const string WriteProtected = "toolRoot.writeProtected";
        /// <summary>网络驱动器或共享文件夹。</summary>
        public const string NetworkDrive = "toolRoot.networkDrive";
        /// <summary>光盘、内存盘等不支持的磁盘类型。</summary>
        public const string UnsupportedDrive = "toolRoot.unsupportedDrive";
        /// <summary>OneDrive 等同步盘目录。</summary>
        public const string CloudSync = "toolRoot.cloudSync";
        /// <summary>路径经过联接或符号链接。</summary>
        public const string LinkedPath = "toolRoot.linkedPath";
        /// <summary>路径含不支持的名称。</summary>
        public const string PathInvalid = "toolRoot.pathInvalid";
        /// <summary>数据属于其他 Windows 用户或另一台电脑。</summary>
        public const string OtherOwner = "toolRoot.otherOwner";
        /// <summary>数据正被另一个宿主使用。</summary>
        public const string DataBusy = "data.busy";
        /// <summary>数据文件夹与引导配置不匹配或不完整。</summary>
        public const string DataMismatch = "data.mismatch";
        /// <summary>数据维护（迁移、恢复）未完成。</summary>
        public const string DataMaintenance = "data.maintenance";
        /// <summary>旧位置数据迁入失败。</summary>
        public const string LegacyImport = "data.legacyImport";
        /// <summary>其他存储失败。</summary>
        public const string StorageUnknown = "storage.unknown";
        /// <summary>启动参数无效（退出码 2）。</summary>
        public const string Arguments = "host.arguments";
        /// <summary>上次更新未完成恢复（退出码 4）。</summary>
        public const string UpdateRecovery = "update.recovery";
        /// <summary>端口被另一份 Moongate 或无法辨认的程序占用（退出码 5）。</summary>
        public const string PortInUse = "port.inUse";
        /// <summary>指定端口不可用（退出码 6）。</summary>
        public const string PortUnavailable = "port.unavailable";
        /// <summary>未知启动错误（退出码 7）。</summary>
        public const string Unknown = "host.unknown";
    }
}
