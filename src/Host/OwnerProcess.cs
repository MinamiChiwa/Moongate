using System.Diagnostics;

namespace MabiTool.Host;

/// <summary>
/// 拉起宿主的前端进程（启动参数 <c>--owner-process &lt;进程号&gt;</c>）。目录包里前端与宿主一起启动、一起退出：
/// 宿主持有前端的进程句柄并等待它结束，前端正常关闭、崩溃或被结束时都按正常停止流程退出（停止抓包与 Worker、释放输入、落盘），
/// 与 <c>POST /api/v1/system/shutdown</c> 走同一条路径。不带这个参数启动的宿主（开发与复验方式）不随任何前端退出。
/// </summary>
public static class OwnerProcess
{
    public const string Argument = "--owner-process";

    /// <summary>拆出 <c>--owner-process</c> 与进程号，其余参数原样返回。</summary>
    /// <returns><c>Arguments</c> 为其余参数；<c>Requested</c> 表示给了这个参数；<c>Owner</c> 是打开的前端进程，前端已结束时为空。</returns>
    public static (string[] Arguments, bool Requested, Process? Owner) Extract(string[] args)
    {
        var index = Array.IndexOf(args, Argument);
        if (index < 0) return (args, false, null);
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid) || pid <= 0 ||
            Array.IndexOf(args, Argument, index + 2) >= 0)
            throw new ArgumentException($"{Argument} 需要一个前端进程号，且只能出现一次");
        return (args.Where((_, i) => i != index && i != index + 1).ToArray(), true, Open(pid));
    }

    /// <summary>
    /// 打开前端进程。进程号已不存在，或该进程晚于本宿主启动（原前端已结束、进程号被重用）时视为前端已结束。
    /// </summary>
    public static Process? Open(int pid)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            if (!process.HasExited && process.StartTime <= self.StartTime) return process;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process?.Dispose();
        return null;
    }

    /// <summary>前端结束（或打开时已结束）后请求宿主停止；宿主先因其他原因停止时不再等待。</summary>
    public static async Task WatchAsync(Process? owner, IHostApplicationLifetime lifetime)
    {
        if (owner is not null)
        {
            try { await owner.WaitForExitAsync(lifetime.ApplicationStopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        lifetime.StopApplication();
    }
}
