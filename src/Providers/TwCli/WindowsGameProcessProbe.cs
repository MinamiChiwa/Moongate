using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using MabiTool.Platform.Windows;

namespace MabiTool.Providers.TwCli;

/// <summary>
/// 按进程映像路径判断所选台服目录的游戏是否在运行：只比较路径，不读进程内存或启动参数；
/// 读不到某个同名进程的映像路径时返回 <see cref="GameProcessState.Unverifiable"/>（写偏好时拒绝）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGameProcessProbe : IGameProcessProbe
{
    public GameProcessInfo Probe(string gameDirectory)
    {
        var expected = Normalize(Path.Combine(gameDirectory, TwCliFile.GameExecutable));
        var unverifiable = false;
        foreach (var process in Process.GetProcessesByName("MabinogiMobile"))
        {
            using (process)
            {
                var image = WindowsProcessAccess.ImagePath(process.Id);
                if (image is null)
                {
                    if (!Exited(process)) unverifiable = true;
                    continue;
                }
                if (!string.Equals(Normalize(image), expected, StringComparison.OrdinalIgnoreCase)) continue;
                long start = 0;
                try { start = process.StartTime.ToUniversalTime().Ticks; }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
                return new(GameProcessState.Running, process.Id, start);
            }
        }
        return new(unverifiable ? GameProcessState.Unverifiable : GameProcessState.NotRunning);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private static bool Exited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { return true; }
    }
}
