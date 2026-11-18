using System.Diagnostics;
using MabiTool.Observation.Worker;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>
/// 游戏迁到别处后用联接挂回原路径：观测 Worker 准备库存布局时按解析后的最终路径取客户端目录，目录与客户端文件照常通过链接检查；
/// 未解析的原路径仍被拒绝。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LinkedClientDirectoryTests
{
    [Fact]
    public async Task InventoryLayoutDirectoryIsUsableThroughAJunctionOnceResolved()
    {
        var root = Path.Combine(Path.GetTempPath(), "moongate-linked-client-" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(root, "real"); var link = Path.Combine(root, "link");
        try
        {
            Directory.CreateDirectory(Path.Combine(real, "MabinogiMobile_Data", "il2cpp_data", "Metadata"));
            foreach (var file in new[] { "MabinogiMobile.exe", "GameAssembly.dll", Path.Combine("MabinogiMobile_Data", "il2cpp_data", "Metadata", "global-metadata.dat") })
                await File.WriteAllBytesAsync(Path.Combine(real, file), [1, 2, 3]);
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe")
                   { ArgumentList = { "/d", "/c", "mklink", "/J", link, real }, UseShellExecute = false, CreateNoWindow = true,
                       RedirectStandardOutput = true, RedirectStandardError = true })!)
            {
                await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, error);
            }
            var reported = Path.Combine(link, "MabinogiMobile.exe");
            Assert.ThrowsAny<IOException>(() => NpcapObservationSource.ClientDirectory(reported));
            var directory = NpcapObservationSource.ClientDirectory(SelectedGameProcess.FinalImagePath(reported));
            Assert.True(File.Exists(Path.Combine(directory, "GameAssembly.dll")));
            Assert.Equal(new DirectoryInfo(real).GetFiles().Select(f => f.Name).Order(), new DirectoryInfo(directory).GetFiles().Select(f => f.Name).Order());
            Assert.False((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
