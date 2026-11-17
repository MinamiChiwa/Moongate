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

    [Fact]
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

    [Fact]
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
}
