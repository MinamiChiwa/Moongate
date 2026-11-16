using MabiTool.PortableLauncher;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>解压版包根 Moongate.exe 的启动计划：组件齐全时以随包运行时打开前端，缺少时给出可照做的中文说明。</summary>
public sealed class PortableLauncherTests
{
    [Fact]
    public void LauncherFindsBundledComponentsOrNamesWhatIsMissing()
    {
        var root = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "portable-launcher-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 只有 Moongate.exe（例如在压缩包查看器里直接运行）：提示完整解压。
            Directory.CreateDirectory(root);
            var bare = LaunchPlan.Create(root, true);
            Assert.Contains("没有 components 文件夹", bare.Problem);
            Assert.Contains("完整解压", bare.Problem);

            var components = Path.Combine(root, "components");
            string Touch(params string[] parts)
            {
                var path = Path.Combine([components, .. parts]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, []);
                return path;
            }
            Touch("desktop", "MabiTool.Desktop.exe");
            Touch("core", "MabiTool.Host.dll");
            var incomplete = LaunchPlan.Create(root + Path.DirectorySeparatorChar, true);
            Assert.Contains(@"components\dotnet-runtime\dotnet.exe", incomplete.Problem);
            Assert.Contains(@"components\dotnet-runtime\host\fxr", incomplete.Problem);
            Assert.Contains(@"components\dotnet-runtime\shared\Microsoft.NETCore.App", incomplete.Problem);
            Assert.Contains(@"components\dotnet-runtime\shared\Microsoft.AspNetCore.App", incomplete.Problem);
            Assert.DoesNotContain("MabiTool.Desktop.exe", incomplete.Problem);

            Touch("dotnet-runtime", "dotnet.exe");
            Touch("dotnet-runtime", "host", "fxr", "10.0.13", "hostfxr.dll");
            Touch("dotnet-runtime", "shared", "Microsoft.NETCore.App", "10.0.13", "System.Private.CoreLib.dll");
            Touch("dotnet-runtime", "shared", "Microsoft.AspNetCore.App", "10.0.13", "Microsoft.AspNetCore.dll");
            var ready = LaunchPlan.Create(root, true);
            Assert.Null(ready.Problem);
            Assert.Equal(Path.Combine(components, "desktop", "MabiTool.Desktop.exe"), ready.Desktop);
            Assert.Equal(root, ready.PackageRoot);
            Assert.Equal(Path.Combine(components, "dotnet-runtime"), ready.Environment["DOTNET_ROOT"]);
            Assert.Equal(Path.Combine(components, "dotnet-runtime"), ready.Environment["DOTNET_ROOT_X64"]);
            Assert.Contains("64 位", LaunchPlan.Create(root, false).Problem);

            File.Delete(Path.Combine(components, "core", "MabiTool.Host.dll"));
            Assert.Contains(@"components\core\MabiTool.Host.dll", LaunchPlan.Create(root, true).Problem);
            Assert.Equal(new[] { "components" }, Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("--sample")]
    [InlineData("D:\\Moongate Data\\")]
    [InlineData("say \"hi\"")]
    [InlineData("")]
    [InlineData("a\\\\b\\\"c")]
    public void ForwardedArgumentsRoundTripThroughTheWindowsCommandLine(string argument)
    {
        // 前端收到的参数与传给 Moongate.exe 的一致：用系统的命令行解析读回拼接结果。
        Assert.Equal(new[] { "first", argument, "last" }, Split(LaunchPlan.CommandLine(["first", argument, "last"])));
    }

    private static string[] Split(string commandLine)
    {
        var pointer = CommandLineToArgvW("x " + commandLine, out var count);
        try
        {
            return Enumerable.Range(1, count - 1)
                .Select(index => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!)
                .ToArray();
        }
        finally { LocalFree(pointer); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
