using System.Text.Json;
using MabiTool.Contracts;
using MabiTool.Host.GameCli;
using MabiTool.Infrastructure;
using MabiTool.Providers.TwCli;
using Xunit;

namespace MabiTool.Host.Tests;

public sealed class GameDataServiceTests
{
    private static readonly string[] AllReads =
        ["get_my_info", "get_currencies", "get_daily_missions", "get_weekly_missions", "get_growth_guide", "get_current_environment", "get_near_pcs"];

    private const string Profile = """{"Title":"稱號","RealmName":"愛麗沙","Level":60,"EnabledCombatJobDisplayName":"魔法師","STR":{"DisplayName":"力量","Value":10}}""";
    private const string Environment = """{"ChannelDisplayName":"頻道一","GameSpaceDisplayName":"庫漢","WorldPosition":{"X":1.5,"Y":2.5},"Weather":"Sunny","ErinnNow":"2968-6-08 13:13"}""";

    private static GameDataService Service(ConnectedRig rig, GameCaptureFacts? facts = null, GameDataTimings? timings = null, StateEventHub? hub = null) =>
        new(rig.Service, () => facts ?? GameCaptureFacts.None, hub, "epoch", TimeProvider.System, null, timings);

    private static GameDataQuery Topics(bool refresh = false, params string[] topics) => new(topics, refresh);

    /// <summary>读取间隔缩到毫秒级，让一个用例能在几十毫秒里经过多次到期。</summary>
    private static GameDataTimings Fast(int confirmMs = 300) => new(TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.FromMilliseconds(confirmMs), 0.001);

    [Fact]
    public async Task NoQueryNeverCallsTheCommandLineEvenWhenConnected()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        using var service = Service(rig);
        await rig.Service.RefreshAsync(false);
        Assert.Equal(["status", "capabilities"], rig.Runner.Commands);
        await Assert.ThrowsAsync<OperationException>(() => service.QueryAsync(new([]), default));
        await Assert.ThrowsAsync<OperationException>(() => service.QueryAsync(new(["window"]), default));
        Assert.Equal(["status", "capabilities"], rig.Runner.Commands);
    }

    [Fact]
    public async Task QueriesWithinTheIntervalShareOneReadAndConcurrentQueriesAreCoalesced()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        rig.Runner.Handlers["get_current_environment"] = _ => DataRunner.Ok(Environment);
        rig.Runner.Delays["get_current_environment"] = TimeSpan.FromMilliseconds(80);
        using var service = Service(rig);
        var both = await Task.WhenAll(service.QueryAsync(Topics(false, "environment"), default), service.QueryAsync(Topics(false, "environment"), default));
        Assert.Equal(1, rig.Runner.CountOf("get_current_environment"));
        Assert.Equal(both[0].Environment!.Status.ReadAt, both[1].Environment!.Status.ReadAt);
        var again = await service.QueryAsync(Topics(false, "environment"), default);
        Assert.Equal(1, rig.Runner.CountOf("get_current_environment"));
        var environment = again.Environment!;
        Assert.Equal(GameDataSource.Connector, environment.Status.Source.KnownValue);
        Assert.Equal(("頻道一", "庫漢", "Sunny", "2968-6-08 13:13"), (environment.Value!.Channel, environment.Value.Area, environment.Value.Weather, environment.Value.ErinnTime));
        // 刷新绕过 5 秒间隔，同一主题的最小间隔由 RefreshGap 约束。
        using var spaced = Service(rig, timings: new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)));
        await spaced.QueryAsync(Topics(true, "environment"), default);
        await spaced.QueryAsync(Topics(true, "environment"), default);
        Assert.Equal(2, rig.Runner.CountOf("get_current_environment"));
    }

    [Fact]
    public async Task OnlyRequestedTopicsAreReadAndProfileKeepsTitleAndServerSeparate()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        rig.Runner.Handlers["get_my_info"] = _ => DataRunner.Ok(Profile);
        rig.Runner.Handlers["get_currencies"] = _ => DataRunner.Ok("""[{"DisplayName":"金幣","Amount":5}]""");
        using var service = Service(rig);
        var snapshot = await service.QueryAsync(Topics(false, "profile", "currencies"), default);
        Assert.Null(snapshot.Missions); Assert.Null(snapshot.Nearby); Assert.Null(snapshot.Environment); Assert.Null(snapshot.GrowthGuide);
        Assert.Equal(["get_currencies", "get_my_info"], rig.Runner.Commands.Skip(2).Order(StringComparer.Ordinal));
        Assert.Equal(("稱號", "愛麗沙", (string?)null), (snapshot.Profile!.Profile!.Title, snapshot.Profile.Profile.ServerName, snapshot.Profile.Profile.Nickname));
        Assert.Equal(5d, Assert.Single(snapshot.Currencies!.Items!).Amount);
        Assert.All(rig.Runner.Calls.Where(x => x[0].StartsWith("get_", StringComparison.Ordinal)), call => Assert.Single(call));
    }

    [Fact]
    public async Task UnavailableConnectorFallsBackToCaptureWithSourceAndReasonWithoutCallingTheCommandLine()
    {
        using var rig = new ConnectedRig();
        await rig.Service.RefreshAsync(true);
        var facts = new GameCaptureFacts("抓包昵稱", "庫漢", DateTimeOffset.UtcNow, false, [new("金幣", 99)]);
        using var service = Service(rig, facts);
        var snapshot = await service.QueryAsync(new(GameDataTopics.All), default);
        Assert.Empty(rig.Runner.Commands);
        Assert.Equal((GameDataSource.Capture, "gameCli.disabled", "抓包昵稱"),
            (snapshot.Profile!.Status.Source.KnownValue, snapshot.Profile.Status.Reason, snapshot.Profile.Profile!.Nickname));
        Assert.Null(snapshot.Profile.Profile.Level);
        Assert.Equal((GameDataSource.Capture, "庫漢", (string?)null), (snapshot.Environment!.Status.Source.KnownValue, snapshot.Environment.Value!.Area, snapshot.Environment.Value.ErinnTime));
        Assert.Equal(("金幣", 99d), (snapshot.Currencies!.Items![0].DisplayName, snapshot.Currencies.Items[0].Amount));
        // 没有抓包来源的主题：来源为无、内容为空（未读取），不是“没有玩家／没有任务”。
        Assert.Equal((GameDataSource.None, "gameCli.disabled"), (snapshot.Nearby!.Status.Source.KnownValue, snapshot.Nearby.Status.Reason));
        Assert.Null(snapshot.Nearby.Players);
        Assert.Null(snapshot.Missions!.Daily.Items); Assert.Null(snapshot.Missions.Weekly.Items);
        Assert.Equal(GameDataSource.None, snapshot.GrowthGuide!.Status.Source.KnownValue);
    }

    [Fact]
    public async Task ConnectorValueWinsButAreaConflictsWithCaptureAreReportedAndTheNicknameIsNeverComparedWithTheServer()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        rig.Runner.Handlers["get_my_info"] = _ => DataRunner.Ok(Profile);
        rig.Runner.Handlers["get_current_environment"] = _ => DataRunner.Ok(Environment);
        using var service = Service(rig, new("乙", "提爾·納·諾伊", DateTimeOffset.UtcNow, false, []));
        var snapshot = await service.QueryAsync(Topics(false, "profile", "environment"), default);
        // 昵称取抓包本机身份，服务器名取命令行的 RealmName：语义不同，不比较、不产生冲突。
        Assert.Empty(snapshot.Profile!.Status.Conflicts);
        Assert.Equal(("乙", "愛麗沙"), (snapshot.Profile.Profile!.Nickname, snapshot.Profile.Profile.ServerName));
        var area = Assert.Single(snapshot.Environment!.Status.Conflicts);
        Assert.Equal(("area", "庫漢", "提爾·納·諾伊"), (area.Field, area.ConnectorValue, area.CaptureValue));
        // 一致时没有冲突。
        using var agreeing = Service(rig, new("甲", "庫漢", DateTimeOffset.UtcNow, false, []));
        var same = await agreeing.QueryAsync(Topics(false, "profile", "environment"), default);
        Assert.Empty(same.Profile!.Status.Conflicts); Assert.Empty(same.Environment!.Status.Conflicts);
    }

    [Fact]
    public async Task EmptyMissionListKeepsTheLastResultAsStaleUntilTheEmptyResultIsConfirmed()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        var daily = """[{"Title":"討伐","CurrentCount":1,"GoalCount":3,"IsCompleted":false,"IsRewardReceived":false,"HasShortcut":false}]""";
        rig.Runner.Handlers["get_daily_missions"] = _ => DataRunner.Ok(daily);
        rig.Runner.Handlers["get_weekly_missions"] = _ => DataRunner.Ok("""[{"Title":"週常","CurrentCount":0,"GoalCount":1,"IsCompleted":false,"IsRewardReceived":false,"HasShortcut":false}]""");
        using var service = Service(rig, timings: Fast(confirmMs: 250));
        var first = (await service.QueryAsync(Topics(false, "missions"), default)).Missions!;
        Assert.Equal(("討伐", false), (first.Daily.Items![0].Title, first.Daily.Status.Stale));

        // 切换频道：日常任务暂时返回空列表，周常照常。
        daily = "[]";
        await Task.Delay(40);
        var held = (await service.QueryAsync(Topics(false, "missions"), default)).Missions!;
        Assert.Equal("討伐", Assert.Single(held.Daily.Items!).Title);
        Assert.Equal((true, "gameCli.emptyHeld"), (held.Daily.Status.Stale, held.Daily.Status.Reason));
        Assert.False(held.Weekly.Status.Stale);

        // 空列表持续超过确认时间（且已有两次读取）才确认已清空。
        await Task.Delay(40);
        Assert.Equal(["討伐"], (await service.QueryAsync(Topics(false, "missions"), default)).Missions!.Daily.Items!.Select(x => x.Title));
        await Task.Delay(300);
        var cleared = (await service.QueryAsync(Topics(false, "missions"), default)).Missions!;
        Assert.Empty(cleared.Daily.Items!);
        Assert.False(cleared.Daily.Status.Stale);
        Assert.Null(cleared.Daily.Status.Reason);
    }

    [Fact]
    public async Task RejectedMissionReadKeepsThePreviousListAsStaleAndWithoutOneIsUnread()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        var weekly = """{"status":"rejected","body":{"error":"not_ready","message":"m"}}""";
        rig.Runner.Handlers["get_daily_missions"] = _ => DataRunner.Ok("[]");
        rig.Runner.Handlers["get_weekly_missions"] = _ => DataRunner.Ok(weekly);
        using var service = Service(rig, timings: Fast());
        var first = (await service.QueryAsync(Topics(false, "missions"), default)).Missions!;
        Assert.Null(first.Weekly.Items);
        Assert.Equal((GameDataSource.None, "gameCli.notReady"), (first.Weekly.Status.Source.KnownValue, first.Weekly.Status.Reason));
        Assert.Empty(first.Daily.Items!);
        weekly = """{"status":"accepted","body":[{"Title":"週常","CurrentCount":0,"GoalCount":1,"IsCompleted":false,"IsRewardReceived":false,"HasShortcut":false}]}""";
        rig.Runner.Handlers["get_weekly_missions"] = _ => DataRunner.Ok(weekly);
        await Task.Delay(40);
        Assert.Equal("週常", Assert.Single((await service.QueryAsync(Topics(false, "missions"), default)).Missions!.Weekly.Items!).Title);
        weekly = """{"status":"rejected","body":{"error":"not_ready","message":"m"}}""";
        await Task.Delay(40);
        var later = (await service.QueryAsync(Topics(false, "missions"), default)).Missions!;
        Assert.Equal(("週常", true, "gameCli.notReady"), (Assert.Single(later.Weekly.Items!).Title, later.Weekly.Status.Stale, later.Weekly.Status.Reason));
    }

    [Fact]
    public async Task LockedGrowthGuideReportsTheReasonAndTheGameUnlockHint()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        rig.Runner.Handlers["get_growth_guide"] = _ => DataRunner.Ok("""{"status":"rejected","body":{"error":"guide_locked","message":"x","unlockHint":"完成主線任務後解鎖"}}""");
        using var service = Service(rig);
        var guide = (await service.QueryAsync(Topics(false, "growth-guide"), default)).GrowthGuide!;
        Assert.True(guide.Locked);
        Assert.Equal(("gameCli.guideLocked", "完成主線任務後解鎖"), (guide.Status.Reason, guide.UnlockHint));
        Assert.Null(guide.Items);
    }

    [Fact]
    public async Task ReadsBlockedBehindAnExecuteCommandReturnStaleCacheAsBusyAndFinishInTheBackground()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        var value = Environment;
        rig.Runner.Handlers["get_current_environment"] = _ => DataRunner.Ok(value);
        using var service = Service(rig, timings: new(TimeSpan.FromMilliseconds(80), TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        var first = (await service.QueryAsync(Topics(false, "environment"), default)).Environment!;
        Assert.Equal("頻道一", first.Value!.Channel);

        value = Environment.Replace("頻道一", "頻道二");
        rig.Runner.Delays["get_current_environment"] = TimeSpan.FromMilliseconds(400);
        var busy = (await service.QueryAsync(Topics(true, "environment"), default)).Environment!;
        Assert.Equal(("頻道一", true, "gameCli.busy"), (busy.Value!.Channel, busy.Status.Stale, busy.Status.Reason));
        await Task.Delay(500);
        // 后台读取已完成：下一次查询直接取新值，不再次调用命令行。
        var calls = rig.Runner.CountOf("get_current_environment");
        var fresh = (await service.QueryAsync(Topics(false, "environment"), default)).Environment!;
        Assert.Equal(("頻道二", false), (fresh.Value!.Channel, fresh.Status.Stale));
        Assert.Equal(calls, rig.Runner.CountOf("get_current_environment"));
    }

    [Fact]
    public async Task BusyWithoutAnyCacheReportsNothingReadInsteadOfAnEmptyResult()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        rig.Runner.Handlers["get_near_pcs"] = _ => DataRunner.Ok("[]");
        rig.Runner.Delays["get_near_pcs"] = TimeSpan.FromMilliseconds(300);
        using var service = Service(rig, timings: new(TimeSpan.FromMilliseconds(50), TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        var busy = (await service.QueryAsync(Topics(false, "nearby"), default)).Nearby!;
        Assert.Equal((GameDataSource.None, "gameCli.busy"), (busy.Status.Source.KnownValue, busy.Status.Reason));
        Assert.Null(busy.Players);
        await Task.Delay(400);
        // 空数组是正常的读取结果（附近没有玩家），与未读取不同。
        var done = (await service.QueryAsync(Topics(false, "nearby"), default)).Nearby!;
        Assert.Equal(GameDataSource.Connector, done.Status.Source.KnownValue);
        Assert.Empty(done.Players!);
    }

    [Fact]
    public async Task ReadFailureAfterASuccessKeepsTheLastValueAsStaleInsteadOfEmpty()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        rig.Runner.Handlers["get_near_pcs"] = _ => DataRunner.Ok("""[{"RealmName":"甲","Performance":{"IsPlaying":true,"MusicTitle":"曲","ChannelCount":2}}]""");
        using var service = Service(rig, timings: Fast());
        Assert.Equal("甲", Assert.Single((await service.QueryAsync(Topics(false, "nearby"), default)).Nearby!.Players!).ServerName);
        rig.Runner.Handlers["get_near_pcs"] = _ => new(1, "", "boom", false, TimeSpan.Zero);
        await Task.Delay(40);
        var failed = (await service.QueryAsync(Topics(false, "nearby"), default)).Nearby!;
        Assert.Equal("甲", Assert.Single(failed.Players!).ServerName);
        Assert.Equal((true, "gameCli.readFailed"), (failed.Status.Stale, failed.Status.Reason));
    }

    [Fact]
    public async Task ConnectorChangesInvalidateCachedFallbackSoReadsResumeWithoutWaitingForTheInterval()
    {
        using var rig = new ConnectedRig();
        await rig.Service.RefreshAsync(true);
        rig.Runner.Handlers["get_my_info"] = _ => DataRunner.Ok(Profile);
        using var service = Service(rig, new("抓", null, DateTimeOffset.UtcNow, false, []));
        Assert.Equal(GameDataSource.Capture, (await service.QueryAsync(Topics(false, "profile"), default)).Profile!.Status.Source.KnownValue);
        await rig.ConnectAsync(AllReads);
        var connected = (await service.QueryAsync(Topics(false, "profile"), default)).Profile!;
        Assert.Equal((GameDataSource.Connector, "抓", "愛麗沙"), (connected.Status.Source.KnownValue, connected.Profile!.Nickname, connected.Profile.ServerName));
    }

    [Fact]
    public async Task TopicStatusAndCapabilitiesFollowTheConnectorAndCatalog()
    {
        using var rig = new ConnectedRig();
        await rig.Service.RefreshAsync(true);
        var facts = GameCaptureFacts.None;
        using var service = Service(rig, null);
        Assert.Equal((CapabilityStatus.Unavailable, (string?)"gameCli.disabled"), service.TopicStatus("environment")!.Value);
        Assert.Null(service.TopicStatus("room"));
        // 抓包有地图时环境降级，附近没有抓包来源仍不可用。
        using var withMap = Service(rig, new(null, "庫漢", DateTimeOffset.UtcNow, false, []));
        Assert.Equal((CapabilityStatus.Degraded, (string?)"gameCli.disabled"), withMap.TopicStatus("environment")!.Value);
        Assert.Equal((CapabilityStatus.Unavailable, (string?)"gameCli.disabled"), withMap.TopicStatus("nearby")!.Value);
        var degraded = GameDataCapabilities.Snapshots(rig.Service, () => new(null, "庫漢", null, false, []), DateTimeOffset.UtcNow).Single(x => x.Id == "environment.observe");
        Assert.Equal((CapabilityStatus.Degraded, "gameCli.disabled", "source.capture"), (degraded.Status.KnownValue, degraded.ReasonCode, Assert.Single(degraded.Limitations)));

        await rig.ConnectAsync("get_current_environment", "get_my_info", "get_currencies", "get_daily_missions", "get_weekly_missions", "get_market_prices", "get_market_price_detail");
        Assert.Equal((CapabilityStatus.Available, (string?)null), service.TopicStatus("environment")!.Value);
        var missing = service.TopicStatus("nearby")!.Value;
        Assert.Equal((CapabilityStatus.Unavailable, "gameCli.commandMissing"), (missing.Status, missing.Reason));
        var capabilities = GameDataCapabilities.Snapshots(rig.Service, () => facts, DateTimeOffset.UtcNow).ToDictionary(x => x.Id);
        Assert.Equal(CapabilityStatus.Available, capabilities["environment.observe"].Status.KnownValue);
        Assert.Equal(CapabilityStatus.Available, capabilities["character.info"].Status.KnownValue);
        Assert.Equal(CapabilityStatus.Available, capabilities["market.prices"].Status.KnownValue);
        Assert.Equal(("gameCli.commandMissing", "get_near_pcs"), (capabilities["nearby.observe"].ReasonCode, Assert.Single(capabilities["nearby.observe"].Limitations)));
    }

    [Fact]
    public async Task SuccessfulEnvironmentAndNearbyReadsArePublishedAsObservationsAndFallbacksAreNot()
    {
        using var rig = new ConnectedRig();
        await rig.ConnectAsync(AllReads);
        rig.Runner.Handlers["get_current_environment"] = _ => DataRunner.Ok(Environment);
        rig.Runner.Handlers["get_near_pcs"] = _ => DataRunner.Ok("""[{"RealmName":"甲"}]""");
        var hub = new StateEventHub(null, "epoch");
        using var service = Service(rig, hub: hub);
        await service.QueryAsync(Topics(false, "environment", "nearby"), default);
        var session = new AuthSession("client", "test", [ApiPermissions.SystemRead], null, null, false);
        var snapshot = await hub.SnapshotAsync(session, EventFilter.Create(["game.environment.changed", "game.nearby.changed"]));
        Assert.Equal(["game.environment.changed", "game.nearby.changed"], snapshot.Resources.Select(x => x.Type).Order(StringComparer.Ordinal));
        var environment = snapshot.Resources.Single(x => x.Type == "game.environment.changed").Data;
        Assert.Equal("庫漢", environment.GetProperty("value").GetProperty("area").GetString());
        Assert.Equal("2968-6-08 13:13", environment.GetProperty("value").GetProperty("rawTimeText").GetString());
        Assert.Equal("tw-cli", environment.GetProperty("source").GetProperty("provider").GetString());
        var nearby = snapshot.Resources.Single(x => x.Type == "game.nearby.changed").Data;
        Assert.Equal("甲", nearby.GetProperty("value").GetProperty("players")[0].GetProperty("serverName").GetString());

        using var offline = new ConnectedRig();
        await offline.Service.RefreshAsync(true);
        var quiet = new StateEventHub(null, "epoch");
        using var fallback = new GameDataService(offline.Service, () => new(null, "庫漢", DateTimeOffset.UtcNow, false, []), quiet, "epoch");
        await fallback.QueryAsync(Topics(false, "environment"), default);
        Assert.Empty((await quiet.SnapshotAsync(session, EventFilter.Create(["game.environment.changed"]))).Resources);
    }

    [Fact]
    public void WalletFallbackUsesOnlyLiveObservedResourcesWithClientNames()
    {
        InventoryResourceView Item(string key, string state, string? name, ulong? value, bool current) =>
            new(key, "Money", state, null, name, null, value is null ? null : new(value.Value), null, null, null, null, null, null, null, false, current);
        var view = new InventoryResourcesView("a", "c", "名", true, new(1), [
            Item("goldFree", "observed", "金幣", 1234, true), Item("silverCoin", "observed", "銀幣", 5, false),
            Item("passDeep", "unread", "魔族貢品", null, true), Item("noName", "observed", null, 7, true)]);
        var wallet = GameCaptureFactsSource.Wallet(view);
        Assert.Equal(("金幣", 1234d), (Assert.Single(wallet).DisplayName, wallet[0].Amount));
        Assert.Empty(GameCaptureFactsSource.Wallet(null));
    }
}
