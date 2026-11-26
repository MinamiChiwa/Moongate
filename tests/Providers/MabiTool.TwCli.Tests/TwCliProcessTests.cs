using System.Text;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.TwCli.Tests;

/// <summary>用批处理替身经真实进程调用：参数数组与 base64 body、UTF-8 中文输出、退出码、取消时杀掉进程。</summary>
public sealed class TwCliProcessTests
{
    [Fact]
    public async Task ChineseOutputAndBodyRoundTripThroughARealProcessAndExitCodesAreKept()
    {
        using var workspace = new TwCliWorkspace();
        var script = FakeCliScript.Write(workspace.Root);
        await File.WriteAllTextAsync(workspace.PathFor("get_items.out"), "[{\"DisplayName\":\"馬賽魚湯\",\"Count\":13}]", new UTF8Encoding(false));
        await File.WriteAllTextAsync(workspace.PathFor("status.out"), "{\"error\":\"not_connected\",\"reason\":\"option_off\"}");
        await File.WriteAllTextAsync(workspace.PathFor("status.code"), "5");
        var service = new TwCliService(() => new(script, workspace.Root, "s"), new TwCliProcessRunner());

        var items = await service.ExecuteAsync("get_items", TwCliBody.Json(new { category = "Food", name = "馬賽魚湯" }));
        Assert.True(items.IsOk);
        Assert.Equal("馬賽魚湯", items.Body!.Value[0].GetProperty("DisplayName").GetString());
        var logged = (await File.ReadAllTextAsync(workspace.PathFor("calls.log"))).Trim();
        Assert.StartsWith("get_items base64:", logged);
        var body = Encoding.UTF8.GetString(Convert.FromBase64String(logged["get_items base64:".Length..]));
        Assert.Equal("{\"category\":\"Food\",\"name\":\"馬賽魚湯\"}", body);

        var status = await service.StatusAsync();
        Assert.Equal((TwCliResultKind.NotConnected, 5, "option_off"), (status.Kind, status.ExitCode, status.Reason));
    }

    [Fact]
    public async Task CancellationKillsTheRunningProcessAndReportsInterrupted()
    {
        using var workspace = new TwCliWorkspace();
        var script = FakeCliScript.Write(workspace.Root);
        await File.WriteAllTextAsync(workspace.PathFor("play_music_score.delay"), "");
        var service = new TwCliService(() => new(script, workspace.Root, "s"), new TwCliProcessRunner());
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var result = await service.ExecuteAsync("play_music_score", TwCliBody.Plain("x"), source.Token);
        Assert.Equal(TwCliResultKind.Interrupted, result.Kind);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(6), "进程应在取消后被杀掉，而不是等待脚本结束");
    }
}
