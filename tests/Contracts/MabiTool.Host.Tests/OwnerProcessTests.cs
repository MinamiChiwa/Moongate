using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>目录包里宿主跟随拉起它的前端进程：前端结束（正常退出或被强制结束）后宿主按正常停止流程退出。</summary>
public sealed class OwnerProcessTests
{
    [Fact]
    public async Task HostExitsAfterItsOwnerFrontendIsKilled()
    {
        var root = Path.Combine(Repository(), "artifacts/tmp/owner-process-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var owner = WaitingProcess();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { typeof(BackendApplication).Assembly.Location, "--mock", "--portable-root", root, "--initialize-data",
                OwnerProcess.Argument, owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
        start.Environment["MABITOOL_API_PORT"] = "0";
        start.Environment["MABITOOL_ALLOWED_ORIGINS"] = "";
        foreach (var name in new[] { "MABITOOL_API_TOKEN", "MABITOOL_BOOTSTRAP_DIRECTORY", "MABITOOL_RUNTIME_DIRECTORY" }) start.Environment.Remove(name);
        using var host = Process.Start(start)!;
        var output = host.StandardOutput.ReadToEndAsync();
        var errors = host.StandardError.ReadToEndAsync();
        try
        {
            var discovery = Path.Combine(root, "runtime", "instance.json");
            for (var waited = Stopwatch.StartNew(); !File.Exists(discovery); await Task.Delay(100))
            {
                if (host.HasExited) Assert.Fail("宿主未能启动：" + await errors);
                if (waited.Elapsed > TimeSpan.FromSeconds(40)) Assert.Fail("宿主 40 秒内未就绪");
            }
            // 前端被任务管理器结束：宿主在短时间内按正常停止流程退出，移除发现文件，退出码为 0。
            owner.Kill();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.WaitForExitAsync(limit.Token);
            Assert.Equal(0, host.ExitCode);
            Assert.False(File.Exists(discovery));
        }
        finally
        {
            if (!owner.HasExited) owner.Kill();
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            for (var attempt = 0; Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (IOException) when (attempt < 50) { await Task.Delay(200); }
            }
        }
    }

    [Fact]
    public async Task OwnerExitingNormallyStopsTheHostAndALaterProcessIsNotTakenAsTheOwner()
    {
        using var owner = WaitingProcess();
        // 晚于本进程启动的进程不可能是拉起本进程的前端（进程号被重用），视为前端已结束。
        Assert.Null(OwnerProcess.Open(owner.Id));
        Assert.Null(OwnerProcess.Open(int.MaxValue));
        var lifetime = new Lifetime();
        var watch = OwnerProcess.WatchAsync(owner, lifetime);
        await Task.Delay(200);
        Assert.False(lifetime.Stopped);
        // 前端正常关闭（这里以关闭标准输入让它自行结束）。
        owner.StandardInput.Close();
        await watch.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(lifetime.Stopped);
        Assert.Equal(new[] { "rest" }, OwnerProcess.Extract(["rest", OwnerProcess.Argument, int.MaxValue.ToString()]).Arguments);
        Assert.Throws<ArgumentException>(() => OwnerProcess.Extract([OwnerProcess.Argument, "x"]));
    }

    // 一直等待标准输入的进程，代替前端。
    private static Process WaitingProcess() => Process.Start(new ProcessStartInfo("cmd.exe")
    {
        ArgumentList = { "/c", "set /p wait=" }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
    })!;

    private static string Repository()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "MabiTool.slnx"))) root = root.Parent!;
        return root.FullName;
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();
        public bool Stopped => stopping.IsCancellationRequested;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
    }
}
