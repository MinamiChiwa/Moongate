using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Infrastructure.Workers;
using MabiTool.LifeVision.Worker;
using Xunit;

namespace MabiTool.LifeVision.Tests;

/// <summary>
/// 真实 Worker 进程经宿主的 <see cref="WorkerSession"/> 握手、受理请求并回报错误；窗口句柄是不存在的值，
/// 因此 Worker 不会截取任何真实窗口，只验证进程协议、错误码与进程回收。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LifeVisionProcessTests
{
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "MabiTool.LifeVision.Worker.exe");

    private static LifeVisionWatchArguments Arguments(int width = 1280, int height = 960) => new(new(0x7FFFFFF0), Environment.ProcessId,
        Process.GetCurrentProcess().StartTime.ToUniversalTime(), new(0, 0, width, height), 96, 100, LifeVisionProfiles.TwFishing1280x960);

    private static async Task<(WorkerFrame Terminal, int ProcessId, bool Exited)> RunAsync(LifeVisionWatchArguments arguments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MabiTool.slnx"))) dir = dir.Parent;
        var assets = Path.Combine(dir!.FullName, "artifacts", "tmp", "life-vision-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(assets);
        try
        {
            var options = new WorkerProcessOptions(Executable, AppContext.BaseDirectory, assets, LifeVisionProfiles.WorkerId) { StartupTimeout = TimeSpan.FromSeconds(30) };
            var session = await WorkerSession.StartAsync(options, _ => { });
            var processId = session.ProcessId;
            WorkerFrame terminal;
            try
            {
                var call = await session.BeginAsync(new(LifeVisionProfiles.WatchMethod, JsonSerializer.SerializeToElement(arguments, ContractJson.Options),
                    Guid.NewGuid().ToString("N"), "1", "1"));
                terminal = await call.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { await session.DisposeAsync(); }
            using var gone = Process.GetProcesses().FirstOrDefault(p => p.Id == processId);
            return (terminal, processId, gone is null || gone.HasExited);
        }
        finally { Directory.Delete(assets, true); }
    }

    [Fact]
    public async Task MissingWindowIsReportedAsBindingChangeAndTheProcessIsReclaimed()
    {
        var (terminal, _, exited) = await RunAsync(Arguments());
        Assert.Equal("error", terminal.Kind);
        Assert.Equal("input.bindingChanged", terminal.Payload.GetProperty("code").GetString());
        Assert.True(exited);
    }

    [Fact]
    public async Task UnsupportedClientSizeIsRejectedBeforeAnyCapture()
    {
        var (terminal, _, exited) = await RunAsync(Arguments(1920, 1080));
        Assert.Equal("error", terminal.Kind);
        Assert.Equal("life.fishing.layoutUnsupported", terminal.Payload.GetProperty("code").GetString());
        Assert.True(exited);
    }
}
