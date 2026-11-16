namespace MabiTool.Infrastructure.Workers;

/// <summary>
/// Npcap 是用户自行安装的外部依赖，不随工具发行。观测 Worker 只加载系统目录下的 <c>Npcap\wpcap.dll</c>；
/// 这里只判断该文件是否存在，不加载 DLL，宿主据此在不启动 Worker 的情况下报告 Npcap 是否已安装。
/// </summary>
public static class NpcapInstallation
{
    public const string Installed = "installed";
    public const string Missing = "missing";

    /// <summary>Worker 加载的 Npcap 库：<c>%SystemRoot%\System32\Npcap\wpcap.dll</c>。</summary>
    public static string LibraryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll");

    /// <summary><see cref="Installed"/> 或 <see cref="Missing"/>；非 Windows 无法判定，返回空。</summary>
    public static string? Probe() => OperatingSystem.IsWindows() ? File.Exists(LibraryPath) ? Installed : Missing : null;
}
