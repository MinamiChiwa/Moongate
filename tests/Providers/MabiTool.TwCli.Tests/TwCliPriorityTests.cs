using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.TwCli.Tests;

/// <summary>通道出队优先级：执行类指令先于控制读取先于数据读取；已开始的进程不被抢占；等待中的取消不持有通道。</summary>
public sealed class TwCliPriorityTests
{
    private static readonly TwCliTarget Target = new("cli.exe", "game-dir", "100:1");

    [Fact]
    public async Task ExecuteBeatsControlBeatsBackgroundWhileTheRunningCallIsNotPreempted()
    {
        var runner = new ScriptedRunner { Delay = TimeSpan.FromMilliseconds(150) };
        var service = new TwCliService(() => Target, runner);
        var running = service.ExecuteAsync("get_my_info", null, priority: TwCliPriority.Background);
        await Task.Delay(40);
        // 数据读取先排队，随后控制读取与执行类指令到达。
        var background = service.ExecuteAsync("get_near_pcs", null, priority: TwCliPriority.Background);
        await Task.Delay(10);
        var control = service.ExecuteAsync("get_activity", null);
        await Task.Delay(10);
        var execute = service.ExecuteAsync("play_music_score", TwCliBody.Plain("x"));
        await Task.WhenAll(running, background, control, execute).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["get_my_info", "play_music_score", "get_activity", "get_near_pcs"], runner.Commands);
        Assert.Equal(1, runner.MaximumConcurrent);
    }

    [Theory]
    [InlineData("play_music_score", TwCliPriority.Execute)]
    [InlineData("change_instrument", TwCliPriority.Execute)]
    [InlineData("stop_action", TwCliPriority.Execute)]
    [InlineData("execute_gathering", TwCliPriority.Execute)]
    [InlineData("complete_altering_work", TwCliPriority.Execute)]
    [InlineData("get_activity", TwCliPriority.Control)]
    [InlineData("get_music_scores", TwCliPriority.Control)]
    [InlineData("capabilities", TwCliPriority.Control)]
    public void CommandsGetTheirDefaultPriority(string command, TwCliPriority expected) =>
        Assert.Equal(expected, TwCliService.PriorityOf(command));

    [Fact]
    public async Task InterruptCancelsQueuedBackgroundReadsAndLaterCallsStillRun()
    {
        var runner = new ScriptedRunner { Delay = TimeSpan.FromSeconds(30) };
        var service = new TwCliService(() => Target, runner);
        var running = service.ExecuteAsync("play_music_score", TwCliBody.Plain("x"));
        await Task.Delay(80);
        var read = service.ExecuteAsync("get_my_info", null, priority: TwCliPriority.Background);
        await Task.Delay(30);
        Assert.Equal(1, service.Queued);
        service.Interrupt();
        var results = await Task.WhenAll(running, read).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal(TwCliResultKind.Interrupted, result.Kind));
        Assert.Equal(["play_music_score"], runner.Commands);
        runner.Delay = TimeSpan.Zero;
        Assert.True((await service.ExecuteAsync("get_activity", null)).IsOk);
        Assert.Equal(0, service.Queued);
    }

    [Fact]
    public async Task CallerCancellationWhileQueuedLeavesTheChannelFree()
    {
        var runner = new ScriptedRunner { Delay = TimeSpan.FromMilliseconds(200) };
        var service = new TwCliService(() => Target, runner);
        var running = service.ExecuteAsync("get_activity", null);
        await Task.Delay(40);
        using var cancel = new CancellationTokenSource();
        var waiting = service.ExecuteAsync("get_my_info", null, cancel.Token, TwCliPriority.Background);
        await Task.Delay(30);
        cancel.Cancel();
        Assert.Equal(TwCliResultKind.Interrupted, (await waiting).Kind);
        await running;
        Assert.True((await service.ExecuteAsync("get_near_pcs", null, priority: TwCliPriority.Background)).IsOk);
        Assert.Equal(["get_activity", "get_near_pcs"], runner.Commands);
    }
}
