using System.Text;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.TwCli.Tests;

public sealed class TwCliServiceTests
{
    private static readonly TwCliTarget Target = new("cli.exe", "game-dir", "100:1");

    private static (TwCliService Service, ScriptedRunner Runner) Create(Func<TwCliTarget?>? target = null)
    {
        var runner = new ScriptedRunner();
        return (new TwCliService(target ?? (() => Target), runner), runner);
    }

    [Fact]
    public async Task CallsRunOneAtATimeWithTheGameDirectoryAsWorkingDirectory()
    {
        var (service, runner) = Create();
        runner.Delay = TimeSpan.FromMilliseconds(40);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => service.ExecuteAsync("get_activity", null)));
        Assert.All(results, result => Assert.True(result.IsOk));
        Assert.Equal(1, runner.MaximumConcurrent);
        Assert.All(runner.Directories, directory => Assert.Equal("game-dir", directory));
    }

    [Fact]
    public async Task BodyIsPassedAsASingleBase64ArgumentAfterTheCommand()
    {
        var (service, runner) = Create();
        await service.ExecuteAsync("play_music_score", TwCliBody.Json(new { title = "少年冒險家" }));
        var call = Assert.Single(runner.Calls);
        Assert.Equal(2, call.Length);
        Assert.Equal("play_music_score", call[0]);
        Assert.Equal("{\"title\":\"少年冒險家\"}", Encoding.UTF8.GetString(Convert.FromBase64String(call[1]["base64:".Length..])));
    }

    [Fact]
    public async Task InterruptCancelsQueuedCallsAndTheRunningOneWithoutResending()
    {
        var (service, runner) = Create();
        runner.Delay = TimeSpan.FromSeconds(30);
        var running = service.ExecuteAsync("play_music_score", TwCliBody.Plain("x"));
        await Task.Delay(100);
        var queued = service.ExecuteAsync("get_activity", null);
        await Task.Delay(50);
        service.Interrupt();
        var results = await Task.WhenAll(running, queued).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal(TwCliResultKind.Interrupted, result.Kind));
        Assert.Equal(["play_music_score"], runner.Commands);
        // 中断之后通道恢复，之后的调用正常执行，被中断的命令没有被重发。
        runner.Delay = TimeSpan.Zero;
        Assert.True((await service.ExecuteAsync("get_activity", null)).IsOk);
        Assert.Equal(["play_music_score", "get_activity"], runner.Commands);
    }

    [Fact]
    public async Task NoTargetReturnsUnavailableWithoutStartingAProcess()
    {
        var (service, runner) = Create(() => null);
        var result = await service.ExecuteAsync("status", null);
        Assert.Equal(TwCliResultKind.Unavailable, result.Kind);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task CatalogIsCachedPerGameSessionAndRefetchedWhenTheSessionChanges()
    {
        var session = "100:1";
        var (service, runner) = Create(() => Target with { SessionKey = session });
        runner.Respond = (_, _) => ScriptedRunner.Ok(ScriptedRunner.Catalog(false, "get_activity", "stop_action"));
        Assert.Equal(2, (await service.CatalogAsync())!.Count);
        Assert.Equal(2, (await service.CatalogAsync())!.Count);
        Assert.Single(runner.Calls);
        session = "200:2";
        Assert.Null(service.CachedCatalog());
        await service.CatalogAsync();
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task LoadingCatalogIsRefetchedAfterTheRetryDelayAndUnknownCommandInvalidatesTheCache()
    {
        var loading = true;
        var runner = new ScriptedRunner();
        var time = new ManualTime();
        var service = new TwCliService(() => Target, runner, time);
        runner.Respond = (command, _) => command == "capabilities"
            ? ScriptedRunner.Ok(ScriptedRunner.Catalog(loading, "get_activity")) : new(4, "", "unknown command", false, TimeSpan.Zero);
        Assert.True((await service.CatalogAsync())!.Loading);
        Assert.True((await service.CatalogAsync())!.Loading);
        Assert.Single(runner.Calls);
        loading = false; time.Advance(TimeSpan.FromSeconds(6));
        Assert.False((await service.CatalogAsync())!.Loading);
        Assert.Equal(2, runner.Calls.Count);
        Assert.NotNull(service.CachedCatalog());
        var unknown = await service.ExecuteAsync("nope", null);
        Assert.Equal(TwCliResultKind.UnknownCommand, unknown.Kind);
        Assert.Null(service.CachedCatalog());
        await service.CatalogAsync();
        Assert.Equal(["capabilities", "capabilities", "nope", "capabilities"], runner.Commands);
    }

    [Fact]
    public async Task LatencyIsTheMedianOfRecentNonInterruptedCalls()
    {
        var runner = new ScriptedRunner();
        var durations = new Queue<int>([10, 300, 20, 30, 40]);
        runner.Respond = (_, _) => new(0, "{}", "", false, TimeSpan.FromMilliseconds(durations.Dequeue()));
        var service = new TwCliService(() => Target, runner);
        Assert.Equal(0, service.RecentLatencyMs);
        for (var i = 0; i < 5; i++) await service.ExecuteAsync("get_activity", null);
        Assert.Equal(30, service.RecentLatencyMs);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long ticks;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan span) => ticks += span.Ticks;
    }
}
