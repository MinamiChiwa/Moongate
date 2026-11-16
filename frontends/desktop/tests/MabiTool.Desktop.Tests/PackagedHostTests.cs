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
            Assert.Equal(new[] { Path.Combine(components, "core", "MabiTool.Host.dll"), "--initialize-data" }, start.ArgumentList);
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
            Assert.Equal(new[] { Path.Combine(components, "core", "MabiTool.Host.dll") }, PackagedHost.StartInfo(later).ArgumentList);

            // 宿主运行期间独占 host.lock：只打开前端。显式指定工具根或发现文件时同样不启动宿主。
            using (new FileStream(hostLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Null(PackagedHost.Plan([], toolRoot, desktop));
            Assert.Null(PackagedHost.Plan([ToolRoot.Argument, root], toolRoot, desktop));
            Assert.Null(PackagedHost.Plan(["--instance", hostLock], toolRoot, desktop));
            Assert.Null(PackagedHost.Plan([], toolRoot, Path.Combine(root, "elsewhere")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
