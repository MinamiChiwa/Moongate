using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MabiTool.PortableLauncher;

/// <summary>
/// 包根启动计划：核对桌面前端、宿主与随包运行时都在 <c>components/</c> 下，给出启动前端的命令行与环境；
/// 缺少任何一项时给出用户能照着处理的中文说明。本文件同时编入测试项目。
/// </summary>
internal sealed class LaunchPlan
{
    internal const string Title = "Moongate";

    private LaunchPlan(string? problem, string desktop, string runtime, string packageRoot)
    {
        Problem = problem; Desktop = desktop; Runtime = runtime; PackageRoot = packageRoot;
    }

    /// <summary>不能启动的原因；为空表示可以启动。</summary>
    internal string? Problem { get; }
    internal string Desktop { get; }
    /// <summary>随包运行时目录（<c>components/dotnet-runtime</c>），作为前端的 DOTNET_ROOT。</summary>
    internal string Runtime { get; }
    internal string PackageRoot { get; }

    internal IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>
    {
        ["DOTNET_ROOT"] = Runtime, ["DOTNET_ROOT_X64"] = Runtime, ["DOTNET_MULTILEVEL_LOOKUP"] = "0"
    };

    /// <summary>
    /// 最低系统构建号：Windows 10 1703（15063）。宿主启动时声明每显示器 DPI 感知 v2（<c>SetProcessDpiAwarenessContext</c>，1703 起提供），
    /// 游戏窗口读取用到的 <c>GetDpiForWindow</c>、<c>GetWindowDpiAwarenessContext</c> 为 1607 起提供；
    /// 后台触控（1809）与浮窗不被截图（2004）在较旧系统上停用对应功能，不作为启动门槛。
    /// </summary>
    internal const int MinimumWindowsBuild = 15063;

    internal static LaunchPlan Create(string packageRoot, bool is64BitOperatingSystem, int windowsBuild = MinimumWindowsBuild)
    {
        var root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var components = Path.Combine(root, "components");
        var desktop = Path.Combine(components, "desktop", "MabiTool.Desktop.exe");
        var runtime = Path.Combine(components, "dotnet-runtime");
        string? problem = null;
        if (!is64BitOperatingSystem)
            problem = "Moongate 需要 64 位的 Windows 10 或 Windows 11。";
        else if (windowsBuild < MinimumWindowsBuild)
            problem = $"Moongate 需要 Windows 10 1703（版本 15063）或更新的系统，当前系统版本为 {windowsBuild}。\n\n" +
                      "请通过 Windows 更新升级系统后再运行。";
        else if (!Directory.Exists(components))
            problem = "Moongate.exe 旁边没有 components 文件夹。\n\n请把下载的压缩包完整解压到一个可写的文件夹" +
                      "（例如 D:\\Moongate），再运行解压出的 Moongate 文件夹里的 Moongate.exe；不要在压缩包查看器里直接运行。";
        else
        {
            var missing = Missing(components).ToArray();
            if (missing.Length > 0)
                problem = "程序文件不完整，缺少：\n" + string.Join("\n", missing.Select(item => "  components\\" + item)) +
                          "\n\n请重新完整解压下载的压缩包；解压后仍缺少时，可能被安全软件隔离，请把程序文件夹加入信任后重新解压。";
        }
        return new(problem, desktop, runtime, root);
    }

    /// <summary>前端、宿主与随包运行时（主程序、hostfxr、共享框架）中缺少的项，路径相对 <c>components</c>。</summary>
    private static IEnumerable<string> Missing(string components)
    {
        if (!File.Exists(Path.Combine(components, "desktop", "MabiTool.Desktop.exe"))) yield return @"desktop\MabiTool.Desktop.exe";
        if (!File.Exists(Path.Combine(components, "core", "MabiTool.Host.dll"))) yield return @"core\MabiTool.Host.dll";
        var runtime = Path.Combine(components, "dotnet-runtime");
        if (!File.Exists(Path.Combine(runtime, "dotnet.exe"))) yield return @"dotnet-runtime\dotnet.exe";
        if (!AnyVersion(Path.Combine(runtime, "host", "fxr"), "hostfxr.dll")) yield return @"dotnet-runtime\host\fxr\<版本>\hostfxr.dll";
        foreach (var framework in new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App" })
            if (!AnyVersion(Path.Combine(runtime, "shared", framework), "System.Private.CoreLib.dll", "Microsoft.AspNetCore.dll"))
                yield return $@"dotnet-runtime\shared\{framework}";
    }

    private static bool AnyVersion(string directory, params string[] marker) =>
        Directory.Exists(directory) && Directory.EnumerateDirectories(directory)
            .Any(version => marker.Any(name => File.Exists(Path.Combine(version, name))));

    /// <summary>按 Windows 命令行解析规则（CommandLineToArgvW）为每个参数加引号并拼接，前端收到与本程序相同的参数。</summary>
    internal static string CommandLine(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return argument;
        var text = new StringBuilder("\"");
        for (var i = 0; ; i++)
        {
            var slashes = 0;
            while (i < argument.Length && argument[i] == '\\') { slashes++; i++; }
            if (i == argument.Length) { text.Append('\\', slashes * 2); break; }
            if (argument[i] == '"') text.Append('\\', slashes * 2 + 1).Append('"');
            else text.Append('\\', slashes).Append(argument[i]);
        }
        return text.Append('"').ToString();
    }

    /// <summary>前端在观察期内以非零码退出时的说明。</summary>
    internal static string ExitMessage(int exitCode) =>
        $"桌面前端启动后立即退出（退出码 0x{exitCode:X8}）。\n\n请重新完整解压下载的压缩包后再试；" +
        "仍然失败时，在程序文件夹运行 components\\core\\launch-backend.cmd -Wait 可查看后端能否启动。";
}
