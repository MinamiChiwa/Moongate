using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiTool.Client;
using MabiTool.Contracts;
using MabiTool.Host.Game;
using MabiTool.Host.GameCli;
using MabiTool.Host.Life;
using MabiTool.Infrastructure;
using MabiTool.ModuleSdk;
using MabiTool.Providers.TwCli;
using MabiTool.Providers.TwObservation.ItemCatalog;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MabiTool.Host.Tests;

/// <summary>“生活”模块：替身命令行、临时目录与内存绑定，不连接真实游戏或命令行。</summary>
public sealed class LifeServiceTests
{
    private static readonly byte[] CliBytes = Encoding.UTF8.GetBytes("test command line tool");
    private const string Cost = "5 精靈之翼";

    private static readonly string[] Reads = ["get_gatherable_items", "get_alterable_items", "get_altering_works", "get_craftable_items"];
    private static readonly string[] Executes = ["execute_gathering", "execute_altering", "complete_altering_work", "execute_crafting"];

    private sealed class FakeProbe : IGameProcessProbe
    {
        public GameProcessInfo Info { get; set; } = new(GameProcessState.NotRunning);
        public GameProcessInfo Probe(string gameDirectory) => Info;
    }

    /// <summary>按指令名返回预设响应的替身命令行：记录每次调用（含解码后的 body），可让某条指令挂起到被取消。</summary>
    private sealed class FakeCli : ITwCliRunner
    {
        public List<(string Command, string Body)> Calls { get; } = [];
        public Dictionary<string, Queue<string>> Sequences { get; } = [];
        public Dictionary<string, string> Defaults { get; } = [];
        public HashSet<string> Hang { get; } = [];
        public ManualResetEventSlim Entered { get; } = new();
        /// <summary>这些指令发出后等到 <see cref="Release"/> 才返回（不受取消影响），用来在一轮进行中发出暂停等命令。</summary>
        public HashSet<string> Held { get; } = [];
        public SemaphoreSlim Release { get; } = new(0);
        public string[] Commands { get { lock (Calls) return [.. Calls.Select(x => x.Command)]; } }
        public (string Command, string Body)[] Executed { get { lock (Calls) return [.. Calls.Where(x => Executes.Contains(x.Command))]; } }

        public async Task<TwCliRun> RunAsync(string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var command = arguments[0];
            var body = arguments.Count > 1 && arguments[1].StartsWith("base64:", StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(arguments[1][7..])) : string.Empty;
            lock (Calls) Calls.Add((command, body));
            if (Hang.Contains(command))
            {
                Entered.Set();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { return new(-1, "", "", true, TimeSpan.Zero); }
            }
            if (Held.Contains(command))
            {
                Entered.Set();
                await Release.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            string? text;
            lock (Sequences) text = Sequences.TryGetValue(command, out var queue) && queue.Count > 0 ? queue.Dequeue() : Defaults.GetValueOrDefault(command);
            return new(0, text ?? "{}", "", false, TimeSpan.FromMilliseconds(3));
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly string root;
        public FakeProbe Probe { get; } = new();
        public FakeCli Cli { get; } = new();
        public GameControlContext Controls { get; }
        public GameConnectorService Connector { get; }
        public LifeClientSource Client { get; }
        public LifeService Life { get; private set; }
        public LifeClientTables? Tables { get; set; }
        private readonly SystemInfo info = new("Moongate", "0.1.0", "1.0", "epoch", DateTimeOffset.UtcNow, "test", "test");

        public Rig(string[]? catalog = null, bool connect = true, LifeQueueStore? store = null)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) directory = directory.Parent;
            root = Path.Combine(directory!.FullName, "artifacts", "tmp", "life-tests", Guid.NewGuid().ToString("N"));
            var game = Path.Combine(root, "game"); var prefs = Path.Combine(root, "prefs"); var package = Path.Combine(root, "package", TwCliFile.FileName);
            Directory.CreateDirectory(game); Directory.CreateDirectory(prefs); Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            File.WriteAllText(Path.Combine(game, TwCliFile.GameExecutable), "game");
            File.WriteAllText(Path.Combine(prefs, "BaseMMPlayerPrefs.txt"), "BOOL\nother_flag : True\n");
            File.WriteAllBytes(package, CliBytes);
            var catalogCommands = catalog ?? [.. Reads, .. Executes, "stop_action"];
            Cli.Defaults["status"] = "{\"pipe\":\"connected\"}";
            Cli.Defaults["capabilities"] = "{\"commands\":[" + string.Join(",", catalogCommands.Select(command => Executes.Contains(command) && command != "complete_altering_work"
                ? "{\"Command\":\"" + command + "\",\"Note\":\"Works. Running this command consumes 5 精靈之翼. It is rejected with not_enough_currency.\"}"
                : "{\"Command\":\"" + command + "\"}")) + "]}";
            var environment = new GameConnectorEnvironment(Path.Combine(root, "state"), prefs, [game], Probe, _ => null, Cli, () => package, () => [], null,
                Convert.ToHexStringLower(SHA256.HashData(CliBytes)));
            var time = TimeProvider.System;
            Controls = GameControlContext.Create(new ResourceLeaseCoordinator(time), time, true);
            Controls.Bindings.Update(new("TW", 4242, DateTimeOffset.UtcNow.AddMinutes(-5), new(900), "build", new(0, 0, 1280, 720), 96, 96, false));
            Connector = new(Controls, info, new(null), environment, time);
            Client = new(Connector, time) { Reader = (_, _) => Tables ?? throw new InvalidDataException("没有客户端表") };
            Life = new(Connector, Controls, info, Client, time, store: store);
            if (connect) ConnectAsync().GetAwaiter().GetResult();
        }

        private async Task ConnectAsync()
        {
            await Connector.CommandAsync("p", new("enable", RiskAcknowledged: true), "key-life-001");
            Probe.Info = new(GameProcessState.Running, 4242, 1);
            Assert.Equal(GameConnectorStatus.Connected, (await Connector.RefreshAsync(true)).Status.KnownValue);
        }

        public async Task<LifeSnapshot> StartAsync(string kind, string? name, int count = 1, string? facility = null, int? variant = null, string? cost = Cost, string key = "")
        {
            key = key.Length == 0 ? "key-" + Guid.NewGuid().ToString("N")[..12] : key;
            return await Life.StartAsync("p", new(kind, name, facility, count, variant, cost), key);
        }

        /// <summary>等动作结束（租约已释放、结束时刻已写入），再稍等后端清掉进行中标记。</summary>
        public async Task<LifeSnapshot> FinishedAsync()
        {
            for (var i = 0; i < 1000; i++)
            {
                if (Life.Current() is { Action.EndedAt: not null }) { await Task.Delay(50); return Life.Current(); }
                await Task.Delay(10);
            }
            throw new TimeoutException("动作没有结束");
        }

        public async Task<LifeSnapshot> ReadyAsync()
        {
            for (var i = 0; i < 1000; i++)
            {
                if (Client.Current().State.KnownValue != LifeClientState.Loading) return Life.Current();
                await Task.Delay(10);
            }
            throw new TimeoutException("客户端表没有读完");
        }

        /// <summary>模拟宿主重启：释放当前服务（与宿主退出相同，进行中的命令被取消），用同一个存储建一个新服务。</summary>
        public void Restart(LifeQueueStore store)
        {
            Life.Dispose();
            Life = new(Connector, Controls, info, Client, TimeProvider.System, store: store);
        }

        public Task<LifeSnapshot> AddAsync(string kind, string? name, int count = 1, string? facility = null, string? position = null, string? cost = null) =>
            Life.AddTaskAsync("p", new(kind, name, facility, count, null, position, cost), "key-" + Guid.NewGuid().ToString("N")[..12]);

        /// <summary>按当前目录的单价确认整队花费上限后开始。</summary>
        public Task<LifeSnapshot> RunQueueAsync(int ceiling) => Life.CommandAsync(new("start", ceiling));

        /// <summary>等队列不再执行（没有当前任务）。</summary>
        public async Task<LifeSnapshot> IdleAsync()
        {
            for (var i = 0; i < 1000; i++)
            {
                if (Life.Current() is { Tasks.CurrentTaskId: null } current && current.Occupied is null) { await Task.Delay(50); return Life.Current(); }
                await Task.Delay(10);
            }
            throw new TimeoutException("队列没有停下");
        }

        public void Dispose() { Life.Dispose(); Connector.Dispose(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GeneratedClientReachesTheLifeEndpointsAndStructuredErrorsComeBack()
    {
        const string token = "life_test_0123456789012345678901234567890123456789";
        await using var app = MabiTool.Host.BackendApplication.Build(new(0, token, new HashSet<string>()));
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var sdk = new MabiToolClient(address, _ => ValueTask.FromResult(token));
            var initial = await sdk.GetLifeAsync();
            Assert.Equal((LifeReadState.NotRead, 0), (initial.Gather.State.KnownValue, initial.GatherItems.Count));
            Assert.Contains(initial.Commands, command => command.Command == "execute_gathering" && !command.Available && command.MovesCharacter);
            // 没有游戏连接器：读取把各块标为未读取并写明原因，执行以结构化错误拒绝。
            var read = await sdk.RefreshLifeAsync(new("all"));
            Assert.All([read.Gather, read.Alter, read.Works, read.Craft], section => Assert.Equal(LifeReadState.Failed, section.State.KnownValue));
            var refused = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.StartLifeActionAsync(new("gather", "原木", null, 1, null, null), "life-key-0000000001"));
            Assert.Equal(("life.connectorUnavailable", 409), (refused.Problem!.Code, refused.Status));
            var invalid = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.RefreshLifeAsync(new("everything")));
            Assert.Equal("request.invalid", invalid.Problem!.Code);
            Assert.Null((await sdk.StopLifeActionAsync()).Action);

            // 总队列：加入、改数量、开始（没有连接器时拒绝）、删除，都经生成的客户端。
            var added = await sdk.AddLifeTaskAsync(new("gather", "原木", null, 2), "life-key-0000000002");
            var task = Assert.Single(added.Tasks!.Tasks);
            Assert.Equal(("gather", "原木", 2, LifeTaskState.Pending), (task.Kind, task.Target, task.Count, task.State.KnownValue));
            var updated = await sdk.UpdateLifeTaskAsync(task.Id, new(added.Tasks.Revision, 3));
            Assert.Equal(3, updated.Tasks!.Tasks.Single().Count);
            var stale = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.UpdateLifeTaskAsync(task.Id, new(added.Tasks.Revision, 4)));
            Assert.Equal("revision.stale", stale.Problem!.Code);
            var offline = await Assert.ThrowsAsync<MabiToolApiException>(() => sdk.CommandLifeQueueAsync(new("start", 15)));
            Assert.Equal(("life.connectorUnavailable", 409), (offline.Problem!.Code, offline.Status));
            Assert.Empty((await sdk.RemoveLifeTaskAsync(task.Id)).Tasks!.Tasks);
        }
        finally { await app.StopAsync(); }
    }

    private static IReadOnlySet<T> Set<T>(params T[] values) => new HashSet<T>(values);

    private static LifeClientTables FixtureTables() => new("fixture",
        new Dictionary<string, IReadOnlySet<LifeSkillRef>>
        {
            ["原木"] = Set(new LifeSkillRef("伐木", false)), ["鮭魚"] = Set(new LifeSkillRef("釣魚", true)),
            ["樹枝"] = Set(new LifeSkillRef("日常採集", false), new LifeSkillRef("伐木", false)),
            ["測試礦"] = Set(new LifeSkillRef("釣魚", true), new LifeSkillRef("採礦", false))
        },
        new Dictionary<string, IReadOnlySet<string>> { ["壯壯蘑菇粉末"] = Set("藥品加工設備"), ["皮革+"] = Set("皮革加工設備") },
        new Dictionary<string, IReadOnlySet<string>> { ["繽紛營火套組"] = Set("立即製作") }, new Dictionary<string, string>());

    private static string Gatherables(params string[] names) => "{\"items\":[" + string.Join(",", names.Select(name => "{\"DisplayName\":\"" + name + "\",\"ToolOk\":true}")) + "]}";
    private const string Completed = "{\"status\":\"accepted\",\"body\":{\"result\":\"completed\",\"gained\":16,\"target\":10,\"cost\":\"精靈之翼 5 spent, 11530 left.\"}}";
    private const string Blocked = "{\"error\":\"blocked\",\"kind\":\"unknown_modal\",\"message\":\"A blocking UI is covering the screen\"}";
    private const string Works = "{\"completedCount\":2,\"works\":[{\"DisplayName\":\"合金鋼錠\",\"FacilityName\":\"金屬加工設備\",\"State\":\"Completed\",\"IsCompleted\":true,\"RemainingSeconds\":0}," +
        "{\"DisplayName\":\"皮革+\",\"FacilityName\":\"皮革加工設備\",\"State\":\"Completed\",\"IsCompleted\":true,\"RemainingSeconds\":0}," +
        "{\"DisplayName\":\"壯壯蘑菇粉末\",\"FacilityName\":\"藥品加工設備\",\"State\":\"InProgress\",\"IsCompleted\":false,\"RemainingSeconds\":125.5}]}";

    private static void Enqueue(Rig rig, string command, params string[] responses)
    {
        lock (rig.Cli.Sequences) rig.Cli.Sequences[command] = new(responses);
    }

    [Fact]
    public async Task ItemsAreGroupedByTheClientTablesAndUnreadableOnesGoToOtherWithAReason()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        rig.Cli.Defaults["get_gatherable_items"] = Gatherables("原木", "鮭魚", "樹枝", "未知原木", "測試礦");
        rig.Cli.Defaults["get_altering_works"] = Works;
        rig.Cli.Defaults["get_alterable_items"] = "{\"items\":[{\"DisplayName\":\"壯壯蘑菇粉末\",\"Alterable\":false,\"ProducedPerWork\":5,\"Reason\":\"not_enough_ingredient\"," +
            "\"MissingIngredients\":[{\"DisplayName\":\"壯壯蘑菇汁液\",\"Required\":8,\"Owned\":0}]},{\"DisplayName\":\"未知配方\",\"Alterable\":true}]}";
        rig.Cli.Defaults["get_craftable_items"] = "{\"craftingUnlocked\":true,\"items\":[{\"DisplayName\":\"繽紛營火套組\",\"Craftable\":true,\"ProducedPerCraft\":1,\"SuccessRate\":95.5," +
            "\"Ingredients\":[{\"DisplayName\":\"原木\",\"Required\":4,\"Owned\":9}]},{\"DisplayName\":\"長劍\",\"Variant\":2}]}";
        await rig.ReadyAsync();

        var snapshot = await rig.Life.RefreshAsync(new("all"));
        Assert.Equal(LifeClientState.Ready, snapshot.Client.KnownValue);
        Assert.All([snapshot.Gather, snapshot.Alter, snapshot.Works, snapshot.Craft], section => Assert.Equal(LifeReadState.Loaded, section.State.KnownValue));
        LifeGatherItem Item(string name) => snapshot.GatherItems.Single(x => x.Name == name);
        // 分类来源：MabiAgent 采集归类优先（原木），没有的取客户端表（鮭魚），两边都没有或对应多个非钓鱼技能归“其他”。
        Assert.Equal(("伐木", (string?)null, false, "mabiagent"), (Item("原木").Group, Item("原木").GroupReason, Item("原木").Fishing, Item("原木").GroupSource));
        Assert.Equal(("釣魚", true, "client"), (Item("鮭魚").Group, Item("鮭魚").Fishing, Item("鮭魚").GroupSource));
        Assert.Equal(((string?)null, "life.gatherMultiple", (string?)null), (Item("樹枝").Group, Item("樹枝").GroupReason, Item("樹枝").GroupSource));
        Assert.Equal(((string?)null, "life.gatherNotInClient"), (Item("未知原木").Group, Item("未知原木").GroupReason));
        // 同时掉落于钓鱼点与一个非钓鱼技能采集点的物品归该技能，不算钓鱼类。
        Assert.Equal(("採礦", false, "client"), (Item("測試礦").Group, Item("測試礦").Fishing, Item("測試礦").GroupSource));
        // 加工设施：配方归属与加工队列里的设施，按先后去重；配方报告 queue_full 时槽位已满。
        Assert.Equal(["藥品加工設備", "金屬加工設備", "皮革加工設備"], snapshot.Facilities!.Select(x => x.Name));
        Assert.All(snapshot.Facilities!, facility => Assert.False(facility.QueueFull));
        var alter = snapshot.AlterRecipes;
        Assert.Equal(("藥品加工設備", false, "not_enough_ingredient"), (alter[0].Group, alter[0].Alterable, alter[0].Reason));
        Assert.Equal(("壯壯蘑菇汁液", 8, 0), (alter[0].Missing[0].Name, alter[0].Missing[0].Required, alter[0].Missing[0].Owned));
        Assert.Equal(((string?)null, "life.alterNotInClient", (int?)null), (alter[1].Group, alter[1].GroupReason, alter[1].ProducedPerWork));
        Assert.Equal(("立即製作", 95.5, 9), (snapshot.CraftRecipes[0].Group, snapshot.CraftRecipes[0].SuccessRate, snapshot.CraftRecipes[0].Ingredients[0].Owned));
        Assert.Equal(((string?)null, "life.craftNotInClient", 2), (snapshot.CraftRecipes[1].Group, snapshot.CraftRecipes[1].GroupReason, snapshot.CraftRecipes[1].Variant));
    }

    [Fact]
    public async Task UnreadableClientTablesPutEverythingInOtherAndNameTheReason()
    {
        using var rig = new Rig { Tables = null };
        rig.Cli.Defaults["get_gatherable_items"] = Gatherables("未知原木", "原木");
        await rig.ReadyAsync();
        var snapshot = await rig.Life.RefreshAsync(new("gather"));
        Assert.Equal((LifeClientState.Unavailable, "life.clientUnavailable"), (snapshot.Client.KnownValue, snapshot.ClientReason));
        Assert.Equal(((string?)null, "life.clientUnavailable"), (snapshot.GatherItems[0].Group, snapshot.GatherItems[0].GroupReason));
        // 客户端表不可读时，MabiAgent 采集归类里有的物品照常分组。
        Assert.Equal(("伐木", "mabiagent"), (snapshot.GatherItems[1].Group, snapshot.GatherItems[1].GroupSource));

        // 单张表核对失败：只有依赖它的分类不可读，其他分类照常。
        var partial = FixtureTables() with { Unsupported = new Dictionary<string, string> { ["craft"] = "life.craftLayoutUnsupported" } };
        Assert.Equal(((string?)null, "life.craftLayoutUnsupported"), (LifeClassifier.Craft(LifeClientState.Ready, null, partial, "繽紛營火套組").Group,
            LifeClassifier.Craft(LifeClientState.Ready, null, partial, "繽紛營火套組").Reason));
        Assert.Equal("伐木", LifeClassifier.Gather(LifeClientState.Ready, null, partial, "原木").Group);
        Assert.Equal("life.clientLoading", LifeClassifier.Gather(LifeClientState.Loading, null, null, "未知原木").Reason);
    }

    [Fact]
    public async Task AFailedSectionShowsItsReasonWithoutOldDataAndOthersStayLoaded()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        rig.Cli.Defaults["get_gatherable_items"] = Gatherables("原木");
        rig.Cli.Defaults["get_altering_works"] = "{\"completedCount\":0,\"works\":[]}";
        await rig.Life.RefreshAsync(new("all"));
        Assert.Single(rig.Life.Current().GatherItems);
        Enqueue(rig, "get_gatherable_items", "{\"status\":\"rejected\",\"body\":{\"error\":\"system_error\",\"message\":\"x\"}}");
        var failed = await rig.Life.RefreshAsync(new("gather"));
        Assert.Equal((LifeReadState.Failed, "gameCli.systemError"), (failed.Gather.State.KnownValue, failed.Gather.Reason));
        Assert.Empty(failed.GatherItems);
        Assert.Equal(LifeReadState.Loaded, failed.Works.State.KnownValue);
        Assert.Empty(failed.Queue!.Works);

        // 连接器没连上：每块都写原因，不显示旧数据。
        rig.Probe.Info = new(GameProcessState.NotRunning);
        await rig.Connector.RefreshAsync(true);
        var offline = await rig.Life.RefreshAsync(new("all"));
        Assert.All([offline.Gather, offline.Alter, offline.Works, offline.Craft], section => Assert.Equal((LifeReadState.Failed, "gameCli.gameNotRunning"), (section.State.KnownValue, section.Reason)));
        Assert.Null(offline.Queue);
        Assert.False(offline.Commands.First(x => x.Command == "execute_gathering").Available);
    }

    [Fact]
    public async Task BatchGatheringRunsRoundByRoundStopsAtBlockedAndNeverRetries()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        rig.Cli.Defaults["get_gatherable_items"] = Gatherables("原木");
        await rig.Life.RefreshAsync(new("gather"));
        Enqueue(rig, "execute_gathering", Completed, Completed, Blocked);
        var started = await rig.StartAsync("gather", "原木", 5);
        Assert.Equal((LifeActionPhase.Running, 5, Cost), (started.Action!.Phase.KnownValue, started.Action.Rounds, started.Action.CostText));
        var done = await rig.FinishedAsync();
        var action = done.Action!;
        // 旧接口的 5 轮是目标 50 个；两次各得 16 个共 32 个，按每轮 10 个换算为已完成 3 轮。
        Assert.Equal((LifeActionPhase.Failed, 3, "gameCli.blocked", "unknown_modal", false), (action.Phase.KnownValue, action.DoneRounds, action.StopCode, action.StopDetail, action.StateUnknown));
        Assert.Equal((50, 32), (done.Tasks!.Tasks.Single(x => x.Immediate).Count, done.Tasks.Tasks.Single(x => x.Immediate).Done));
        Assert.Equal([LifeRoundOutcome.Completed, LifeRoundOutcome.Completed, LifeRoundOutcome.Rejected], action.Results.Select(x => x.Outcome.KnownValue!.Value));
        Assert.Equal((16, 10, 5, 11530, "精靈之翼"), (action.Results[0].Gained, action.Results[0].Target, action.Results[0].Spent, action.Results[0].Left, action.Results[0].CostCurrency));
        Assert.Equal("精靈之翼 5 spent, 11530 left.", action.Results[0].Cost);
        Assert.Equal("unknown_modal", action.Results[2].BlockedKind);
        // 第 4、5 轮没有发出；每次执行的 body 是游戏返回的物品名。
        var executed = rig.Cli.Executed;
        Assert.Equal(3, executed.Length);
        Assert.All(executed, call => Assert.Equal("{\"displayName\":\"原木\"}", call.Body));
        // 租约已释放，活动锁没有占用者。
        Assert.Null(done.Occupied);
        Assert.Empty(rig.Controls.Leases.Snapshot());
    }

    [Theory]
    [InlineData("{\"status\":\"rejected\",\"body\":{\"error\":\"not_enough_currency\",\"message\":\"m\"}}", "gameCli.notEnoughCurrency")]
    [InlineData("{\"status\":\"accepted\",\"body\":{\"error\":\"overweight\",\"message\":\"m\",\"gained\":3,\"target\":10}}", "gameCli.overweight")]
    [InlineData("{\"status\":\"rejected\",\"body\":{\"error\":\"not_enough_ingredient\",\"message\":\"m\"}}", "gameCli.notEnoughIngredient")]
    public async Task RejectionsStopTheBatchWithTheirReasonAndKeepWhatWasGained(string response, string code)
    {
        using var rig = new Rig { Tables = FixtureTables() };
        Enqueue(rig, "execute_gathering", Completed, response);
        await rig.StartAsync("gather", "原木", 3);
        var action = (await rig.FinishedAsync()).Action!;
        Assert.Equal((LifeActionPhase.Failed, 1, code), (action.Phase.KnownValue, action.DoneRounds, action.StopCode));
        Assert.Equal(2, rig.Cli.Executed.Length);
        Assert.Equal(LifeRoundOutcome.Rejected, action.Results[1].Outcome.KnownValue);
    }

    [Fact]
    public async Task OldGatherRoundsAreTenItemsEachAndStopAtTheConfirmedCalls()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        // 旧接口的 2 轮换算为目标 20 个，确认的花费是 2 次；第一次得 3 个，第二次没有 gained 与 target 时按 10 个计。
        Enqueue(rig, "execute_gathering", "{\"result\":\"completed\",\"gained\":3,\"target\":10,\"cost\":\"精靈之翼 5 spent, 11530 left.\"}", "{\"result\":\"completed\"}", Completed);
        await rig.StartAsync("gather", "原木", 2);
        var done = await rig.FinishedAsync();
        var task = done.Tasks!.Tasks.Single(x => x.Immediate);
        Assert.Equal((20, 13), (task.Count, task.Done));
        // 13 个未到目标，第三次会超过确认的 10 精靈之翼：停在确认的次数，不再发出。
        Assert.Equal((LifeActionPhase.Stopped, "life.costLimitReached", 2, 1), (done.Action!.Phase.KnownValue, done.Action.StopCode, done.Action.Rounds, done.Action.DoneRounds));
        Assert.Equal(2, rig.Cli.Executed.Length);
    }

    [Fact]
    public async Task TheConfirmedCostMustMatchTheCurrentCatalogAndTheRequestIsIdempotent()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        var stale = await Assert.ThrowsAsync<OperationException>(() => rig.StartAsync("gather", "原木", cost: "4 精靈之翼"));
        Assert.Equal("life.costChanged", stale.Code);
        Assert.Equal("5 精靈之翼", stale.Details!["cost"].GetString());
        var missing = await Assert.ThrowsAsync<OperationException>(() => rig.StartAsync("gather", "原木", cost: null));
        Assert.Equal("life.costChanged", missing.Code);
        Assert.Empty(rig.Cli.Executed);

        Enqueue(rig, "execute_gathering", Completed);
        await rig.StartAsync("gather", "原木", key: "key-same-0001");
        await rig.FinishedAsync();
        await rig.StartAsync("gather", "原木", key: "key-same-0001");
        Assert.Single(rig.Cli.Executed);
        var conflict = await Assert.ThrowsAsync<OperationException>(() => rig.StartAsync("gather", "堅硬原木", key: "key-same-0001"));
        Assert.Equal("idempotency.conflict", conflict.Code);
        var noKey = await Assert.ThrowsAsync<OperationException>(() => rig.Life.StartAsync("p", new("gather", "原木", null, 1, null, Cost), null));
        Assert.Equal("idempotency.required", noKey.Code);
        var invalid = await Assert.ThrowsAsync<OperationException>(() => rig.StartAsync("gather", "原木", count: LifeService.MaximumRounds + 1));
        Assert.Equal("request.invalid", invalid.Code);
    }

    [Fact]
    public async Task OneActionAtATimeReadsAreRefusedWhileRunningAndTheActivityLockIsShared()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        // 其他模块持有活动锁：不发命令，返回占用信息；占用者也出现在快照里。
        var session = rig.Controls.Bindings.Current!.Session;
        var other = rig.Controls.Leases.Acquire(new("rooms", "rooms-1", session.Id, session.BindingRevision, ["activity-control:" + session.Id], TimeSpan.FromMinutes(1)));
        var busy = await Assert.ThrowsAsync<OperationException>(() => rig.StartAsync("gather", "原木"));
        Assert.Equal("resource.busy", busy.Code);
        Assert.Empty(rig.Cli.Executed);
        Assert.Equal(("rooms", false), (rig.Life.Current().Occupied!.OwnerId, rig.Life.Current().Occupied!.Life));
        rig.Controls.Leases.Release(other.Id);

        rig.Cli.Hang.Add("execute_gathering");
        await rig.StartAsync("gather", "原木");
        Assert.True(rig.Cli.Entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(("life", true), (rig.Life.Current().Occupied!.OwnerId, rig.Life.Current().Occupied!.Life));
        Assert.Equal("life.actionRunning", (await Assert.ThrowsAsync<OperationException>(() => rig.StartAsync("gather", "原木"))).Code);
        Assert.Equal("life.actionRunning", (await Assert.ThrowsAsync<OperationException>(() => rig.Life.RefreshAsync(new("all")))).Code);
        rig.Life.Stop();
        await rig.FinishedAsync();
    }

    [Fact]
    public async Task UserStopInterruptsTheCommandMarksTheStateUnknownAndRereadsTheQueue()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        rig.Cli.Defaults["get_altering_works"] = Works;
        await rig.Life.RefreshAsync(new("works"));
        rig.Cli.Hang.Add("execute_altering");
        await rig.StartAsync("alter", "壯壯蘑菇粉末", 3);
        Assert.True(rig.Cli.Entered.Wait(TimeSpan.FromSeconds(5)));
        var stopped = rig.Life.Stop();
        var done = await rig.FinishedAsync();
        var action = done.Action!;
        Assert.Equal((LifeActionPhase.Stopped, "life.stoppedByUser", true, 0), (action.Phase.KnownValue, action.StopCode, action.StateUnknown, action.DoneRounds));
        Assert.Equal(LifeRoundOutcome.Interrupted, action.Results.Single().Outcome.KnownValue);
        // 中断后后端重读加工队列与配方，之后的轮次不再发出，也不自动重发。
        for (var i = 0; i < 200 && rig.Cli.Commands.Count(x => x == "get_altering_works") < 2; i++) await Task.Delay(10);
        Assert.True(rig.Cli.Commands.Count(x => x == "get_altering_works") >= 2);
        Assert.Single(rig.Cli.Executed);
        Assert.Empty(rig.Controls.Leases.Snapshot());
        // 通道在中断后恢复可用。
        rig.Cli.Hang.Clear();
        Assert.True((await rig.Life.RefreshAsync(new("works"))).Works.State.KnownValue == LifeReadState.Loaded);
    }

    [Fact]
    public async Task EmergencyStopRevokesTheLeaseAndTheBatchStopsWithoutFurtherCommands()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        rig.Cli.Hang.Add("execute_gathering");
        await rig.StartAsync("gather", "原木", 4);
        Assert.True(rig.Cli.Entered.Wait(TimeSpan.FromSeconds(5)));
        // 与 GameControlService.StopLocked 相同：先中断命令行，再撤销全部活动租约。
        rig.Connector.EmergencyStop();
        foreach (var lease in rig.Controls.Leases.Snapshot()) rig.Controls.Leases.Release(lease.Id);
        var action = (await rig.FinishedAsync()).Action!;
        Assert.Equal(LifeActionPhase.Stopped, action.Phase.KnownValue);
        Assert.True(action.StateUnknown);
        Assert.Single(rig.Cli.Executed);
        await Task.Delay(700);
    }

    [Fact]
    public async Task CollectingUsesACompletedWorkOfThatFacilityAndRereadsTheQueueAndQueuingRereadsRecipes()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        var nothing = await Assert.ThrowsAsync<OperationException>(() => rig.StartAsync("collect", null, facility: "皮革加工設備", cost: null));
        Assert.Equal("life.nothingToCollect", nothing.Code);
        rig.Cli.Defaults["get_altering_works"] = Works;
        await rig.Life.RefreshAsync(new("works"));
        Assert.Equal(("皮革加工設備", 1), (rig.Life.Current().Queue!.Works[1].Facility, rig.Life.Current().Queue!.Works.Count(x => x.Completed == true && x.Facility == "皮革加工設備")));
        var noWork = await Assert.ThrowsAsync<OperationException>(() => rig.StartAsync("collect", null, facility: "藥品加工設備", cost: null));
        Assert.Equal("life.nothingToCollect", noWork.Code);

        Enqueue(rig, "complete_altering_work", "{\"collected\":1,\"rewards\":[{\"Name\":\"皮革+\",\"Amount\":3}],\"criticalRewards\":[],\"message\":\"Collected 1 altering works.\"}");
        Enqueue(rig, "get_altering_works", "{\"completedCount\":1,\"works\":[{\"DisplayName\":\"合金鋼錠\",\"FacilityName\":\"金屬加工設備\",\"State\":\"Completed\",\"IsCompleted\":true,\"RemainingSeconds\":0}]}");
        await rig.StartAsync("collect", null, facility: "皮革加工設備", cost: null);
        var done = await rig.FinishedAsync();
        Assert.Equal(("complete_altering_work", "{\"displayName\":\"皮革+\"}"), rig.Cli.Executed.Single());
        Assert.Equal((LifeActionPhase.Completed, 1), (done.Action!.Phase.KnownValue, done.Action.Results[0].Collected));
        Assert.Equal(("皮革+", 3), (done.Action.Results[0].Rewards[0].Name, done.Action.Results[0].Rewards[0].Amount));
        Assert.Null(done.Action.Results[0].Cost);
        for (var i = 0; i < 200 && rig.Life.Current().Queue!.Works.Count != 1; i++) await Task.Delay(10);
        Assert.Equal("金屬加工設備", rig.Life.Current().Queue!.Works.Single().Facility);

        // 加工排队：每轮一项，完成后重读队列与配方。
        Enqueue(rig, "execute_altering", "{\"result\":\"started\",\"message\":\"Altering work queued.\",\"cost\":\"精靈之翼 5 spent, 11525 left.\"}",
            "{\"result\":\"started\",\"message\":\"Altering work queued.\",\"cost\":\"精靈之翼 5 spent, 11520 left.\"}");
        var before = rig.Cli.Commands.Count(x => x is "get_alterable_items");
        await rig.StartAsync("alter", "壯壯蘑菇粉末", 2);
        var queued = (await rig.FinishedAsync()).Action!;
        Assert.Equal((LifeActionPhase.Completed, 2, 11520), (queued.Phase.KnownValue, queued.DoneRounds, queued.Results[1].Left));
        for (var i = 0; i < 200 && rig.Cli.Commands.Count(x => x is "get_alterable_items") == before; i++) await Task.Delay(10);
        Assert.True(rig.Cli.Commands.Count(x => x is "get_alterable_items") > before);
    }

    [Fact]
    public async Task FishingItemsAreRefusedAndAnUnrecognisedOneIsStoppedAtOnceAndRemembered()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        rig.Cli.Defaults["get_gatherable_items"] = Gatherables("鮭魚", "黃金鮭魚");
        await rig.Life.RefreshAsync(new("gather"));
        var refused = await Assert.ThrowsAsync<OperationException>(() => rig.StartAsync("gather", "鮭魚"));
        Assert.Equal("life.fishingNotSupported", refused.Code);
        Assert.Empty(rig.Cli.Executed);

        Enqueue(rig, "execute_gathering", "{\"result\":\"started\",\"message\":\"Auto fishing started at the fishery.\",\"cost\":\"精靈之翼 5 spent, 11520 left.\"}");
        await rig.StartAsync("gather", "黃金鮭魚", 3);
        var action = (await rig.FinishedAsync()).Action!;
        Assert.Equal((LifeActionPhase.Failed, "life.fishingStarted", 0), (action.Phase.KnownValue, action.StopCode, action.DoneRounds));
        Assert.Equal(["execute_gathering", "stop_action"], rig.Cli.Commands.Where(x => x is "execute_gathering" or "stop_action"));
        Assert.True(rig.Life.Current().GatherItems.Single(x => x.Name == "黃金鮭魚").Fishing);
        Assert.Equal("life.fishingNotSupported", (await Assert.ThrowsAsync<OperationException>(() => rig.StartAsync("gather", "黃金鮭魚"))).Code);
    }

    [Fact]
    public async Task CraftingSendsVariantAndCountAndReportsTheCatalogLimit()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        Enqueue(rig, "execute_crafting", "{\"status\":\"rejected\",\"body\":{\"error\":\"invalid_count\",\"message\":\"m\",\"maxCount\":2}}",
            "{\"result\":\"completed\",\"craftCount\":2,\"rewards\":[{\"Name\":\"繽紛營火套組\",\"Amount\":2}],\"criticalRewards\":[],\"cost\":\"精靈之翼 5 spent, 11515 left.\"}");
        await rig.StartAsync("craft", "繽紛營火套組", 5, variant: 2);
        var rejected = (await rig.FinishedAsync()).Action!;
        Assert.Equal(("gameCli.invalidCount", 2), (rejected.StopCode, rejected.Results[0].MaxCount));
        Assert.Equal(("execute_crafting", "{\"displayName\":\"繽紛營火套組\",\"variant\":2,\"craftCount\":5}"), rig.Cli.Executed.Single());
        await rig.StartAsync("craft", "繽紛營火套組", 2);
        var done = (await rig.FinishedAsync()).Action!;
        Assert.Equal((LifeActionPhase.Completed, 2), (done.Phase.KnownValue, done.Results[0].CraftCount));
        Assert.Equal("{\"displayName\":\"繽紛營火套組\",\"craftCount\":2}", rig.Cli.Executed[^1].Body);
    }

    [Fact]
    public async Task ExecutionNeedsAConnectedConnectorAndTheCatalogCommandAndCapabilitiesFollowTheCatalog()
    {
        using var down = new Rig(connect: false);
        await down.Connector.RefreshAsync(true);
        var refused = await Assert.ThrowsAsync<OperationException>(() => down.StartAsync("gather", "原木"));
        Assert.Equal("life.connectorUnavailable", refused.Code);
        Assert.All(LifeCapabilities.Snapshots(down.Connector, DateTimeOffset.UtcNow), item => Assert.Equal(CapabilityStatus.Unavailable, item.Status.KnownValue));

        using var partial = new Rig(["get_gatherable_items", "execute_gathering", "get_alterable_items", "get_altering_works", "execute_altering"]);
        var snapshots = LifeCapabilities.Snapshots(partial.Connector, DateTimeOffset.UtcNow);
        Assert.Equal(CapabilityStatus.Available, snapshots.Single(x => x.Id == "life.gather").Status.KnownValue);
        var altering = snapshots.Single(x => x.Id == "life.altering");
        Assert.Equal((CapabilityStatus.Unavailable, "gameCli.commandMissing", "complete_altering_work"), (altering.Status.KnownValue, altering.ReasonCode, Assert.Single(altering.Limitations)));
        Assert.Equal(CapabilityStatus.Unavailable, snapshots.Single(x => x.Id == "life.crafting").Status.KnownValue);
        var missing = await Assert.ThrowsAsync<OperationException>(() => partial.StartAsync("craft", "繽紛營火套組"));
        Assert.Equal("life.commandUnavailable", missing.Code);
        var info = (await partial.Life.RefreshAsync(new("all"))).Commands;
        Assert.Equal((true, 5, "精靈之翼", "5 精靈之翼", true), (info.Single(x => x.Command == "execute_gathering").Available, info.Single(x => x.Command == "execute_gathering").CostAmount,
            info.Single(x => x.Command == "execute_gathering").CostCurrency, info.Single(x => x.Command == "execute_gathering").CostText, info.Single(x => x.Command == "execute_gathering").MovesCharacter));
        Assert.Equal((false, "gameCli.commandMissing"), (info.Single(x => x.Command == "execute_crafting").Available, info.Single(x => x.Command == "execute_crafting").Reason));
    }

    // ───────────────────────── 总队列 ─────────────────────────

    private const string Queued = "{\"result\":\"started\",\"message\":\"Altering work queued.\",\"cost\":\"精靈之翼 5 spent, 11525 left.\"}";
    private const string Gained12 = "{\"status\":\"accepted\",\"body\":{\"result\":\"completed\",\"gained\":12,\"target\":10,\"cost\":\"精靈之翼 5 spent, 11525 left.\"}}";

    [Fact]
    public async Task TheQueueRunsTasksInOrderUntilEmptyAndReleasesTheLease()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        await rig.AddAsync("gather", "原木", 20);
        await rig.AddAsync("alter", "壯壯蘑菇粉末");
        Enqueue(rig, "execute_gathering", Completed, Gained12);
        Enqueue(rig, "execute_altering", Queued);
        // 上限 = (采集 20 个约 2 次 + 加工 1 项) × 5；与目录重算的值不同时拒绝，不发出命令。
        var changed = await Assert.ThrowsAsync<OperationException>(() => rig.RunQueueAsync(10));
        Assert.Equal(("life.costChanged", 15), (changed.Code, changed.Details!["ceiling"].GetInt32()));
        Assert.Empty(rig.Cli.Executed);

        // 两次采集各得 16、12 个，共 28 个达到目标即完成，再发出加工。
        await rig.RunQueueAsync(15);
        var done = await rig.IdleAsync();
        Assert.Equal(["execute_gathering", "execute_gathering", "execute_altering"], rig.Cli.Executed.Select(x => x.Command));
        Assert.Equal(LifeQueueState.Idle, done.Tasks!.State.KnownValue);
        Assert.All(done.Tasks.Tasks, task => Assert.Equal(LifeTaskState.Completed, task.State.KnownValue));
        Assert.Equal((28, "藥品加工設備"), (done.Tasks.Tasks[0].Done, done.Tasks.Tasks[1].Facility));
        Assert.Empty(rig.Controls.Leases.Snapshot());
        // 加工排队后重读加工队列与配方。
        for (var i = 0; i < 200 && !rig.Cli.Commands.Contains("get_alterable_items"); i++) await Task.Delay(10);
        Assert.Contains("get_altering_works", rig.Cli.Commands);
        Assert.Contains("get_alterable_items", rig.Cli.Commands);
    }

    [Fact]
    public async Task AFailureOrBlockPausesTheWholeQueueAndContinuingResumesThatTaskWithoutSkipping()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        await rig.AddAsync("gather", "原木", 30);
        await rig.AddAsync("alter", "壯壯蘑菇粉末");
        Enqueue(rig, "execute_gathering", Completed, Blocked);
        await rig.RunQueueAsync(20);
        var paused = await rig.IdleAsync();
        var queue = paused.Tasks!;
        var first = queue.Tasks[0];
        Assert.Equal((LifeQueueState.Paused, "gameCli.blocked", "unknown_modal", first.Id), (queue.State.KnownValue, queue.PauseCode, queue.PauseDetail, queue.PauseTaskId));
        Assert.Equal((LifeTaskState.Pending, 16, "gameCli.blocked"), (first.State.KnownValue, first.Done, first.LastCode));
        Assert.DoesNotContain("execute_altering", rig.Cli.Commands);
        Assert.Empty(rig.Controls.Leases.Snapshot());
        // 目标数量必须大于已采集的数量。
        Assert.Equal("request.invalid", Assert.Throws<OperationException>(() => rig.Life.UpdateTask(first.Id, new(queue.Revision, 16))).Code);

        // 继续：从这一项剩下的 14 个开始，再执行下一项；上限按剩余次数重算为 (14 个约 2 次 + 1) × 5。
        Enqueue(rig, "execute_gathering", Completed);
        Enqueue(rig, "execute_altering", Queued);
        await rig.RunQueueAsync(15);
        var done = await rig.IdleAsync();
        Assert.Equal(["execute_gathering", "execute_gathering", "execute_gathering", "execute_altering"], rig.Cli.Executed.Select(x => x.Command));
        Assert.Equal((LifeQueueState.Idle, 32), (done.Tasks!.State.KnownValue, done.Tasks.Tasks[0].Done));
    }

    [Fact]
    public async Task TasksCanBeReorderedResizedAndRemovedButTheRunningOneIsProtected()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        await rig.AddAsync("gather", "原木", 20);
        await rig.AddAsync("craft", "繽紛營火套組", 3);
        var snapshot = await rig.AddAsync("alter", "壯壯蘑菇粉末", 1);
        var ids = snapshot.Tasks!.Tasks.Select(x => x.Id).ToArray();
        var moved = rig.Life.UpdateTask(ids[2], new(snapshot.Tasks.Revision, 4, 0));
        Assert.Equal([ids[2], ids[0], ids[1]], moved.Tasks!.Tasks.Select(x => x.Id));
        Assert.Equal(4, moved.Tasks.Tasks[0].Count);
        Assert.Equal("revision.stale", Assert.Throws<OperationException>(() => rig.Life.UpdateTask(ids[0], new(snapshot.Tasks.Revision, 3))).Code);
        // 数量上限：采集目标 500 个，加工 50 项。
        Assert.Equal("request.invalid", Assert.Throws<OperationException>(() => rig.Life.UpdateTask(ids[0], new(moved.Tasks.Revision, LifeService.MaximumGatherCount + 1))).Code);
        Assert.Equal("request.invalid", Assert.Throws<OperationException>(() => rig.Life.UpdateTask(ids[2], new(moved.Tasks.Revision, LifeService.MaximumRounds + 1))).Code);
        // 钓鱼类采集物不能加入队列（按客户端表判断，先等表读完）。
        await rig.ReadyAsync();
        Assert.Equal("life.fishingNotSupported", (await Assert.ThrowsAsync<OperationException>(() => rig.AddAsync("gather", "鮭魚"))).Code);

        rig.Cli.Hang.Add("execute_altering");
        await rig.RunQueueAsync((4 + 2 + 1) * 5);
        Assert.True(rig.Cli.Entered.Wait(TimeSpan.FromSeconds(5)));
        var running = rig.Life.Current().Tasks!;
        Assert.Equal((ids[2], LifeQueueState.Running), (running.CurrentTaskId, running.State.KnownValue));
        Assert.Equal("life.taskRunning", Assert.Throws<OperationException>(() => rig.Life.RemoveTask(ids[2])).Code);
        Assert.Equal("life.taskRunning", Assert.Throws<OperationException>(() => rig.Life.UpdateTask(ids[2], new(running.Revision, null, 1))).Code);
        // 正在执行的加工可以改项数（不少于已完成加一）；其他任务可以删除。
        var resized = rig.Life.UpdateTask(ids[2], new(running.Revision, 2));
        Assert.Equal(2, resized.Tasks!.Tasks.Single(x => x.Id == ids[2]).Count);
        var removed = rig.Life.RemoveTask(ids[1]);
        Assert.DoesNotContain(removed.Tasks!.Tasks, x => x.Id == ids[1]);

        await rig.Life.CommandAsync(new("stop-current"));
        var stopped = await rig.IdleAsync();
        Assert.Equal((LifeQueueState.Paused, "life.stoppedByUser"), (stopped.Tasks!.State.KnownValue, stopped.Tasks.PauseCode));
        Assert.True(stopped.Tasks.Tasks.Single(x => x.Id == ids[2]).StateUnknown);
        Assert.Single(rig.Cli.Executed);
        await rig.Life.CommandAsync(new("clear-finished"));
        Assert.Equal([ids[2], ids[0]], rig.Life.Current().Tasks!.Tasks.Select(x => x.Id));
    }

    [Fact]
    public async Task PauseWaitsForTheRoundAndEmergencyStopPausesWithoutFurtherCommands()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        // 目标 50 个约 5 次；暂停后保留已采集的 16 个，继续时上限按剩余 34 个约 4 次重算。
        await rig.AddAsync("gather", "原木", 50);
        rig.Cli.Defaults["execute_gathering"] = Completed;
        rig.Cli.Held.Add("execute_gathering");
        await rig.RunQueueAsync(25);
        Assert.True(rig.Cli.Entered.Wait(TimeSpan.FromSeconds(5)));
        var pausing = await rig.Life.CommandAsync(new("pause"));
        Assert.Equal((LifeQueueState.Running, "life.pauseRequested"), (pausing.Tasks!.State.KnownValue, pausing.Tasks.PauseCode));
        rig.Cli.Release.Release();
        var paused = await rig.IdleAsync();
        Assert.Equal((LifeQueueState.Paused, "life.pausedByUser", 16), (paused.Tasks!.State.KnownValue, paused.Tasks.PauseCode, paused.Tasks.Tasks[0].Done));
        Assert.Single(rig.Cli.Executed);

        // 继续后按 F9：与 GameControlService.StopLocked 相同，先中断命令行，再撤销全部活动租约。
        rig.Cli.Held.Clear();
        rig.Cli.Entered.Reset();
        rig.Cli.Hang.Add("execute_gathering");
        await rig.RunQueueAsync(20);
        Assert.True(rig.Cli.Entered.Wait(TimeSpan.FromSeconds(5)));
        rig.Connector.EmergencyStop();
        foreach (var lease in rig.Controls.Leases.Snapshot()) rig.Controls.Leases.Release(lease.Id);
        var stopped = await rig.IdleAsync();
        Assert.Equal((LifeQueueState.Paused, "input.activityLeaseRequired"), (stopped.Tasks!.State.KnownValue, stopped.Tasks.PauseCode));
        Assert.True(stopped.Tasks.Tasks[0].StateUnknown);
        await Task.Delay(300);
        Assert.Equal(2, rig.Cli.Executed.Length);
    }

    [Fact]
    public async Task SpendingIsCappedByTheConfirmedCeilingAndAConfirmedAdditionExtendsIt()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        await rig.AddAsync("gather", "原木", 1);
        rig.Cli.Defaults["execute_gathering"] = Completed;
        rig.Cli.Held.Add("execute_gathering");
        await rig.RunQueueAsync(5);
        Assert.True(rig.Cli.Entered.Wait(TimeSpan.FromSeconds(5)));
        // 运行中加入：没有确认的任务超出上限时在它之前暂停；带确认花费的任务把花费并入上限。
        var unconfirmed = await rig.AddAsync("gather", "原木", 1);
        Assert.Equal(5, unconfirmed.Tasks!.ApprovedCost);
        Assert.Equal("life.costChanged", (await Assert.ThrowsAsync<OperationException>(() => rig.AddAsync("gather", "原木", 1, cost: "4 精靈之翼"))).Code);
        rig.Cli.Release.Release();
        var capped = await rig.IdleAsync();
        Assert.Equal((LifeQueueState.Paused, "life.costLimitReached"), (capped.Tasks!.State.KnownValue, capped.Tasks.PauseCode));
        Assert.Single(rig.Cli.Executed);

        rig.Cli.Entered.Reset();
        await rig.RunQueueAsync(5);
        Assert.True(rig.Cli.Entered.Wait(TimeSpan.FromSeconds(5)));
        // 目标 20 个约 2 次：上限并入 2 × 5。
        var confirmed = await rig.AddAsync("gather", "原木", 20, cost: Cost);
        Assert.Equal(15, confirmed.Tasks!.ApprovedCost);
        rig.Cli.Release.Release(3);
        var done = await rig.IdleAsync();
        Assert.Equal(LifeQueueState.Idle, done.Tasks!.State.KnownValue);
        Assert.Equal(4, rig.Cli.Executed.Length);
    }

    [Fact]
    public async Task TheQueueSurvivesARestartPausedAndNothingIsSentUntilTheUserContinues()
    {
        var root = Path.Combine(StorageTestWorkspace.Repository, "artifacts", "tmp", "life-queue-" + Guid.NewGuid().ToString("N"));
        var options = new StorageOptions(Path.Combine(root, "bootstrap"), true, DataRoot: Path.Combine(root, "data"));
        Directory.CreateDirectory(root);
        try
        {
            using var rig = new Rig { Tables = FixtureTables() };
            string first, second;
            using (var lease = DataRootLease.Open(options))
            await using (var database = SqliteStore.Open(lease, BackendApplication.DatabaseMigrations))
            {
                var store = new LifeQueueStore(database);
                rig.Restart(store);
                await rig.AddAsync("gather", "原木", 30);
                var added = await rig.AddAsync("alter", "壯壯蘑菇粉末");
                (first, second) = (added.Tasks!.Tasks[0].Id, added.Tasks.Tasks[1].Id);
                Enqueue(rig, "execute_gathering", Completed);
                await rig.RunQueueAsync(20);
                for (var i = 0; i < 500 && rig.Life.Current().Tasks!.Tasks[0].Done < 1; i++) await Task.Delay(10);
                rig.Cli.Hang.Add("execute_gathering");
                for (var i = 0; i < 500 && rig.Cli.Executed.Length < 2; i++) await Task.Delay(10);
                Assert.Equal(2, rig.Cli.Executed.Length);
                // 宿主退出：进行中的命令被取消，存储里留下退出前的队列。
                rig.Restart(new LifeQueueStore(null));
                await store.FlushAsync();
                store.Dispose();
            }
            var sent = rig.Cli.Executed.Length;
            using (var lease = DataRootLease.Open(options))
            await using (var database = SqliteStore.Open(lease, BackendApplication.DatabaseMigrations))
            {
                rig.Restart(new LifeQueueStore(database));
                var restored = rig.Life.Current().Tasks!;
                Assert.Equal((LifeQueueState.Paused, "life.hostRestarted", first), (restored.State.KnownValue, restored.PauseCode, restored.PauseTaskId));
                Assert.Equal([first, second], restored.Tasks.Select(x => x.Id));
                Assert.Equal((LifeTaskState.Pending, 16), (restored.Tasks[0].State.KnownValue, restored.Tasks[0].Done));
                await Task.Delay(200);
                Assert.Equal(sent, rig.Cli.Executed.Length);
                Assert.Empty(rig.Controls.Leases.Snapshot());

                // 宿主进程被直接结束时快照里的任务仍是“执行中”：读回后改为待执行并标记结果未知。
                var crashed = new LifeQueueStore(null);
                crashed.Save(new(LifeQueueStore.FormatVersion, [restored.Tasks[0] with { State = LifeTaskState.Running }], [], null, null, null));
                rig.Restart(crashed);
                var interrupted = rig.Life.Current().Tasks!.Tasks.Single();
                Assert.Equal((LifeTaskState.Pending, true, "life.hostRestarted"), (interrupted.State.KnownValue, interrupted.StateUnknown, interrupted.LastCode));
                rig.Restart(new LifeQueueStore(null));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CollectingRunsImmediatelyAndRestoresThePausedQueueAndQueueFullMarksTheFacility()
    {
        using var rig = new Rig { Tables = FixtureTables() };
        rig.Cli.Defaults["get_altering_works"] = Works;
        await rig.Life.RefreshAsync(new("works"));
        await rig.AddAsync("alter", "壯壯蘑菇粉末");
        Enqueue(rig, "execute_altering", "{\"status\":\"rejected\",\"body\":{\"error\":\"queue_full\",\"message\":\"m\"}}");
        await rig.RunQueueAsync(5);
        var full = await rig.IdleAsync();
        Assert.Equal((LifeQueueState.Paused, "gameCli.queueFull"), (full.Tasks!.State.KnownValue, full.Tasks.PauseCode));
        var facility = full.Facilities!.Single(x => x.Name == "藥品加工設備");
        Assert.Equal((true, "execute"), (facility.QueueFull, facility.QueueFullSource));
        // 暂停后的重读（队列与配方）完成后，标记仍在：这次配方读取没有得到结果。
        for (var i = 0; i < 200 && !rig.Cli.Commands.Contains("get_alterable_items"); i++) await Task.Delay(10);
        Assert.True(rig.Life.Current().Facilities!.Single(x => x.Name == "藥品加工設備").QueueFull);

        // 领取：立即执行这一项，结束后队列回到原来的暂停状态与原因。
        rig.Cli.Defaults["get_alterable_items"] = "{\"items\":[{\"DisplayName\":\"壯壯蘑菇粉末\",\"Alterable\":true,\"ProducedPerWork\":5}]}";
        Enqueue(rig, "complete_altering_work", "{\"collected\":1,\"rewards\":[],\"criticalRewards\":[],\"message\":\"ok\"}");
        await rig.StartAsync("collect", null, facility: "皮革加工設備", cost: null);
        var after = await rig.IdleAsync();
        Assert.Equal((LifeQueueState.Paused, "gameCli.queueFull"), (after.Tasks!.State.KnownValue, after.Tasks.PauseCode));
        Assert.Contains(after.Tasks.Tasks, x => x is { Kind: "collect", Immediate: true } && x.State.KnownValue == LifeTaskState.Completed);
        Assert.Equal(("complete_altering_work", "{\"displayName\":\"皮革+\"}"), rig.Cli.Executed[^1]);
        // 领取后重读队列与配方：配方不再报告 queue_full，设施的槽位已满标记清除（“＋”空槽重新出现）。
        Assert.Equal(2, rig.Cli.Commands.Count(x => x == "get_alterable_items"));
        Assert.Equal((false, (string?)null), (after.Facilities!.Single(x => x.Name == "藥品加工設備").QueueFull, after.Facilities!.Single(x => x.Name == "藥品加工設備").QueueFullSource));
    }
}
