namespace MabiTool.ModuleSdk;

/// <summary>
/// 工具根：程序所在的发行根目录。宿主、Worker 与桌面前端运行时产生和使用的全部文件都放在工具根下的固定子目录，
/// 不写入 %LOCALAPPDATA%、%TEMP% 等其他位置。宿主与前端按同一规则判定，前端据此找到宿主的实例发现文件。
/// </summary>
public static class ToolRoot
{
    /// <summary>显式指定工具根的启动参数；开发与复验从构建输出运行时使用。</summary>
    public const string Argument = "--portable-root";
    public const string BootstrapName = "bootstrap", DataName = "data", RuntimeName = "runtime", DesktopName = "desktop";

    /// <summary>目录包里运行文件所在的子文件夹：包根只保留 <c>Moongate.exe</c>、<c>components/</c> 与 <c>licenses/</c>。</summary>
    public const string PackageStateName = "userdata";

    /// <summary>
    /// 由程序目录推出工具根：目录包布局 <c>&lt;根&gt;/components/&lt;组件&gt;</c> 取包根下的 <see cref="PackageStateName"/>，
    /// 包根已有旧布局的 bootstrap、data、runtime 或 desktop 目录时继续取包根；其余（构建输出等）取程序目录本身。
    /// </summary>
    public static string FromProgramDirectory(string programDirectory)
    {
        var program = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(programDirectory)));
        if (PackageRoot(program.FullName) is { } package)
            return new[] { BootstrapName, DataName, RuntimeName, DesktopName }.Any(name => Directory.Exists(Path.Combine(package, name)))
                ? package : Path.Combine(package, PackageStateName);
        return program.FullName;
    }

    /// <summary>
    /// 目录包根：程序目录为 <c>&lt;根&gt;/components/&lt;组件&gt;</c> 且根下有 <c>components/core/MabiTool.Host.dll</c> 时返回该根，否则为空。
    /// </summary>
    public static string? PackageRoot(string programDirectory)
    {
        var program = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(programDirectory)));
        return program.Parent is { } components && components.Name.Equals("components", StringComparison.OrdinalIgnoreCase) &&
            components.Parent is { } owner && File.Exists(Path.Combine(owner.FullName, "components", "core", "MabiTool.Host.dll"))
            ? owner.FullName : null;
    }

    /// <summary>当前进程的工具根：<see cref="Argument"/> 指定的绝对路径优先，否则由程序目录推出。</summary>
    public static string FromArguments(IReadOnlyList<string> arguments, string? programDirectory = null)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] != Argument) continue;
            if (i + 1 >= arguments.Count || !Path.IsPathFullyQualified(arguments[i + 1]))
                throw new ArgumentException($"{Argument} 需要绝对路径");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(arguments[i + 1]));
        }
        return FromProgramDirectory(programDirectory ?? AppContext.BaseDirectory);
    }

    public static string Bootstrap(string root) => Path.Combine(root, BootstrapName);
    public static string Data(string root) => Path.Combine(root, DataName);
    public static string Runtime(string root) => Path.Combine(root, RuntimeName);
    public static string Desktop(string root) => Path.Combine(root, DesktopName);
    public static string DiscoveryFile(string root) => Path.Combine(Runtime(root), "instance.json");

    /// <summary>工具根不可写时的统一说明：数据只保存在程序文件夹内，不回退到其他位置。</summary>
    public static string NotWritableMessage(string directory) =>
        $"无法在“{directory}”中创建文件：该文件夹不可写。Moongate 的数据、配置和运行文件全部保存在程序所在文件夹内，" +
        "请把整个程序文件夹移到当前用户可写的位置（不要放在 Program Files 等受保护目录）后再启动。";
}
