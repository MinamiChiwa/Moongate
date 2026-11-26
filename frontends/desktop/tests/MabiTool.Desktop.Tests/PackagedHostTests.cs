using System.Diagnostics;
using Avalonia.Headless.XUnit;
using MabiTool.Desktop.Infrastructure;
using MabiTool.ModuleSdk;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>目录包里的前端按需启动同一工具根的宿主：首次运行带初始化参数，宿主已运行或显式指定连接目标时不启动。</summary>
public sealed class PackagedHostTests
{
    [Fact]
    public void PackagedFrontendStartsHostOnceAndInitializesOnFirstRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "moongate-packaged-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var components = Path.Combine(root, "components");
            var desktop = Path.Combine(components, "desktop");
            Directory.CreateDirectory(desktop);
            Directory.CreateDirectory(Path.Combine(components, "core"));
            Directory.CreateDirectory(Path.Combine(components, "dotnet-runtime"));
            File.WriteAllBytes(Path.Combine(components, "core", "MabiTool.Host.dll"), []);
            File.WriteAllBytes(Path.Combine(components, "dotnet-runtime", "dotnet.exe"), []);
            // 工具根是包根下的 userdata；宿主的工作目录是包根。
            var toolRoot = Path.Combine(root, ToolRoot.PackageStateName);
            Assert.Equal(toolRoot, ToolRoot.FromProgramDirectory(desktop));

            var first = PackagedHost.Plan([], toolRoot, desktop);
            Assert.NotNull(first);
            Assert.True(first.InitializeData);
            var start = PackagedHost.StartInfo(first);
            Assert.Equal(Path.Combine(components, "dotnet-runtime", "dotnet.exe"), start.FileName);
            // 宿主跟随本前端进程：前端结束后宿主随之退出。
            var owner = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(new[] { Path.Combine(components, "core", "MabiTool.Host.dll"), "--initialize-data", "--owner-process", owner },
                start.ArgumentList);
            Assert.Equal(root, start.WorkingDirectory);
            Assert.Equal(Path.Combine(components, "dotnet-runtime"), start.Environment["DOTNET_ROOT"]);
            Assert.Equal(new[] { "components" }, Directory.EnumerateFileSystemEntries(root).Select(path => Path.GetFileName(path)));

            // 命令行工具只在包内有该组件文件时固定为包内路径。
            Assert.DoesNotContain("MABITOOL_GAME_CLI_EXECUTABLE", first.Environment.Keys);
            var gameCli = Path.Combine(components, "game-cli", "MabinogiMobile_CLI.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(gameCli)!);
            File.WriteAllBytes(gameCli, []);
            Assert.Equal(gameCli, PackagedHost.Plan([], toolRoot, desktop)!.Environment["MABITOOL_GAME_CLI_EXECUTABLE"]);

            Directory.CreateDirectory(Path.Combine(toolRoot, "bootstrap"));
            File.WriteAllText(Path.Combine(toolRoot, "bootstrap", "data-root.json"), "{}");
            Directory.CreateDirectory(Path.Combine(toolRoot, "runtime"));
            var hostLock = Path.Combine(toolRoot, "runtime", "host.lock");
            File.WriteAllBytes(hostLock, []);
            var later = PackagedHost.Plan([], toolRoot, desktop);
            Assert.False(later!.InitializeData);
            Assert.Equal(new[] { Path.Combine(components, "core", "MabiTool.Host.dll"), "--owner-process", owner },
                PackagedHost.StartInfo(later).ArgumentList);

            // 宿主运行期间独占 host.lock：只打开前端。显式指定工具根或发现文件（开发与复验方式）时不启动宿主，也不照看它。
            using (new FileStream(hostLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Null(PackagedHost.Plan([], toolRoot, desktop));
            Assert.Null(PackagedHost.Plan([ToolRoot.Argument, root], toolRoot, desktop));
            Assert.Null(PackagedHost.Plan(["--instance", hostLock], toolRoot, desktop));
            Assert.Null(PackagedHost.Plan([], toolRoot, Path.Combine(root, "elsewhere")));
            Assert.Null(PackagedHost.PackageOf([ToolRoot.Argument, root], toolRoot, desktop));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void PortConflictExitCodeIsExplainedWithoutTheDataDirectoryReason()
    {
        // 宿主绑定端口失败以退出码 5 结束（HostStartupFailure.PortInUseExitCode）；连接状态说明端口与另一份 Moongate，不提数据目录。
        var message = PackagedHost.FailureMessage(5);
        Assert.Contains("19998", message);
        Assert.Contains("Moongate", message);
        Assert.Contains("MabiTool.Host", message);
        Assert.DoesNotContain("数据目录", message);
        Assert.DoesNotContain("資料目錄", message);
    }

    [AvaloniaFact]
    public void HostStoppingWhileTheFrontendIsOpenIsRestartedOnceAndStartupFailuresAreNotRetried()
    {
        // 启动阶段失败：写明原因，不自动重试，避免反复崩溃。
        var startup = PackagedHost.AfterExit(3, TimeSpan.FromSeconds(2), relaunched: false);
        Assert.False(startup.Relaunch);
        Assert.Equal(PackagedHost.FailureMessage(3), startup.Problem);
        // 运行中停止（崩溃或被结束）：自动重新启动一次，连接状态写明后端已停止。
        var first = PackagedHost.AfterExit(-1, TimeSpan.FromMinutes(5), relaunched: false);
        Assert.True(first.Relaunch);
        Assert.Contains("已停止", first.Problem);
        Assert.DoesNotContain("Moongate", first.Problem);
        // 重新启动后再次停止：不再重试，请用户重新打开。
        var second = PackagedHost.AfterExit(-1, TimeSpan.FromMinutes(5), relaunched: true);
        Assert.False(second.Relaunch);
        Assert.Contains("Moongate", second.Problem);
    }

    [AvaloniaFact]
    public async Task HostExitDuringUpdateDoesNotWaitForAnInheritedErrorPipe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command" }) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("""
            $childStart = [System.Diagnostics.ProcessStartInfo]::new($env:ComSpec)
            $childStart.Arguments = '/d /c "ping -n 30 127.0.0.1 >nul"'
            $childStart.UseShellExecute = $false
            $childStart.CreateNoWindow = $true
            $childStart.WindowStyle = 'Hidden'
            $child = [System.Diagnostics.Process]::Start($childStart)
            [Console]::Error.WriteLine('child:' + $child.Id)
            [Console]::In.ReadLine() | Out-Null
            """);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var parent = new Process { StartInfo = start };
        parent.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { } text && text.StartsWith("child:", StringComparison.Ordinal) && int.TryParse(text[6..], out var id)) ready.TrySetResult(id);
        };
        Process? holder = null;
        var started = false;
        try
        {
            started = parent.Start();
            Assert.True(started);
            parent.BeginErrorReadLine();
            holder = Process.GetProcessById(await ready.Task.WaitAsync(stop.Token));
            using (var cancelled = new CancellationTokenSource())
            {
                var waiting = PackagedHost.WaitForHostExitAsync(parent, () => true, cancelled.Token);
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            }
            var updating = PackagedHost.WaitForHostExitAsync(parent, () => true, stop.Token);
            var ordinary = PackagedHost.WaitForHostExitAsync(parent, () => false, stop.Token);
            await parent.StandardInput.WriteLineAsync("exit");
            await parent.StandardInput.FlushAsync();
            await updating.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(parent.HasExited);
            Assert.False(holder.HasExited);
            Assert.False(ordinary.IsCompleted);
            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync(stop.Token);
            await ordinary.WaitAsync(stop.Token);
        }
        finally
        {
            stop.Cancel();
            if (holder is { HasExited: false }) { holder.Kill(entireProcessTree: true); await holder.WaitForExitAsync(); }
            if (started && !parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(); }
            holder?.Dispose();
        }
    }

    [AvaloniaFact]
    public void HostStartupReportOnStandardErrorBecomesASpecificReasonWithoutCodes()
    {
        // 宿主写到标准错误的启动失败记录是纯 ASCII 的一行；前端解析后按原因分类写短句与做法，不出现英文原因码，也不要求运行命令行。
        var report = new HostStartupReport("storage.permissionDenied", HostStartupReport.Reasons.ProtectedLocation, 3, @"程序文件夹位于“C:\Program Files\Moongate”");
        var line = report.Format();
        Assert.All(line, character => Assert.True(character < 128));
        var record = new HostLaunchRecord(DateTimeOffset.Now);
        record.Error("storage.permissionDenied: 程序文件夹位于受保护目录");
        record.Error(line);
        Assert.Equal(report, record.Report);
        Assert.Single(record.ErrorTail);

        var (relaunch, problem) = PackagedHost.AfterExit(3, TimeSpan.FromSeconds(2), relaunched: false, record.Report);
        Assert.False(relaunch);
        Assert.Contains("系统保护目录", problem);
        Assert.DoesNotContain("toolRoot", problem);
        Assert.DoesNotContain("launch-backend", problem);
        var text = StartupFailureText.For(3, record.Report);
        Assert.True(text.Status.Length <= 16 && text.Hint.Length <= 20);
        // 没有记录时按退出码说明，并指向诊断页的启动记录。
        Assert.Contains("启动记录", PackagedHost.FailureMessage(3));

        // 导出的启动记录写明退出码与原因分类，并替换用户名。
        record.Finish(3, TimeSpan.FromSeconds(2), null);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var exported = StartupRecordExport.Sanitize(StartupRecordExport.Compose(DateTimeOffset.Now, [record], Path.Combine(profile, "Moongate", "userdata"),
            Path.Combine(profile, "Moongate", "components", "desktop"), null));
        Assert.Contains("退出码：3", exported);
        Assert.Contains(HostStartupReport.Reasons.ProtectedLocation, exported);
        Assert.Contains("%USERPROFILE%", exported);
        Assert.DoesNotContain(Environment.UserName, exported, StringComparison.OrdinalIgnoreCase);
    }
}
