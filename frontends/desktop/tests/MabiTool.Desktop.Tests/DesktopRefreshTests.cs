using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MabiTool.Desktop.Backend;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>
/// 后端适配层按主题、修订与可见性读取：摘要与最新捕获事件只读快照，修订未变不请求不通知，不可见不读、重新可见补读，
/// 用户操作立即读取，密集事件每秒至多读 60 轮且显示最后状态，最新捕获事件到达即读并记下显示延迟。替身后端见 <see cref="CombatStubHost"/>。
/// </summary>
public sealed class DesktopRefreshTests
{
    private const string Snapshot = "GET /api/v1/combat/snapshot";

    private static async Task<(LiveBackend Backend, CombatStubHost Stub, Func<int> Changes)> ConnectAsync(Action<LiveBackend>? before = null)
    {
        var stub = new CombatStubHost();
        var changes = 0;
        var backend = new LiveBackend(Path.Combine(Path.GetTempPath(), "moongate-missing-" + Guid.NewGuid().ToString("N"), "instance.json"),
            action => Dispatcher.UIThread.Post(action))
        {
            Handler = () => stub,
            Bootstrap = _ => Task.FromResult(stub.Bootstrap)
        };
        backend.Changed += (_, _) => changes++;
        before?.Invoke(backend);
        backend.Start();
        await Until(() => backend.ShownWorkspaceRevision == stub.WorkspaceRevision && backend.GetCombat().Skills.Count > 0, "连接替身后端并读到战斗数据");
        // 让连接后的首轮读取全部结束（含怪物效果、覆盖率），再开始计数。
        await Task.Delay(400, TestContext.Current.CancellationToken);
        return (backend, stub, () => changes);
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(seconds), "等待超时：" + what);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [AvaloniaFact]
    public async Task SummaryEventReadsOnlyTheSnapshotAndARepeatedRevisionReadsNothing()
    {
        var (backend, stub, changes) = await ConnectAsync();
        await using var _ = backend;
        stub.ResetCounters();
        stub.Checkpoint(true);
        await Until(() => backend.ShownWorkspaceRevision == stub.WorkspaceRevision, "摘要事件后读到新快照");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, stub.Count(Snapshot));
        Assert.Equal(0, stub.Count("GET /api/v1/combat/encounters"));
        Assert.Equal(0, stub.Count("GET /api/v1/combat/measurement"));
        Assert.Equal(0, stub.Count("GET /api/v1/combat/entities/{id}/effects"));

        stub.ResetCounters();
        var before = changes();
        stub.RepeatSummary();
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.Equal(0, stub.Count(Snapshot));
        Assert.Equal(before, changes());
    }

    // 客户端属性图标：界面没有使用时不读目录、不下载；使用后目录为 loading 时按短间隔重读，变为 ready 后补上图标，
    // 之后不再轮询目录，同一版本只下载一次。
    [AvaloniaFact]
    public async Task ClientIconsAreReadOnlyWhileUsedRetriedWhileLoadingAndDownloadedOncePerVersion()
    {
        const string catalogRoute = "GET /api/v1/client-icons", content = "GET /api/v1/client-icons/critical/content";
        var (backend, stub, _) = await ConnectAsync();
        await using var _ = backend;
        stub.IconPng = TestIcons.Png();
        stub.IconCatalog = TestIcons.Catalog(MabiTool.Contracts.ClientIconCatalogState.Loading, null);
        await Task.Delay(1200, TestContext.Current.CancellationToken);
        Assert.Equal(0, stub.Count(catalogRoute));

        var changes = 0;
        backend.ClientIconsChanged += (_, _) => changes++;
        backend.SetClientIconsInUse(true);
        await Until(() => stub.Count(catalogRoute) >= 2, "目录为 loading 时重读");
        Assert.Null(backend.GetClientIcon("critical"));
        Assert.Equal(0, stub.Count(content));

        stub.IconCatalog = TestIcons.Catalog(MabiTool.Contracts.ClientIconCatalogState.Ready, "v1");
        await Until(() => backend.GetClientIcon("critical") is not null && changes > 0, "目录 ready 后补上图标并通知界面");
        Assert.Equal("v1", backend.GetClientIcon("critical")!.Version);
        Assert.Null(backend.GetClientIcon("powerfulHit"));
        var reads = stub.Count(catalogRoute);
        await Task.Delay(2500, TestContext.Current.CancellationToken);
        Assert.Equal(reads, stub.Count(catalogRoute));
        Assert.Equal(1, stub.Count(content));
    }

    // 分段进行中停手：宿主不推送摘要事件，时长与 DPS 由兜底读取的快照推进（至多约 2 秒一步），不停在最后一次事件的值上。
    [AvaloniaFact]
    public async Task PausedSegmentStillAdvancesFromTheSnapshotWithoutEvents()
    {
        var (backend, stub, changes) = await ConnectAsync();
        await using var _ = backend;
        stub.Checkpoint(true);
        await Until(() => backend.ShownWorkspaceRevision == stub.WorkspaceRevision, "读到有伤害的分段");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var dps = backend.GetCombat().Kpis.First(tile => tile.Primary).Value;
        var before = changes();
        stub.ResetCounters();
        stub.Pause(3000);
        await Until(() => backend.GetCombat().Kpis.First(tile => tile.Primary).Value != dps, "停手后 DPS 随时长变化", seconds: 4);
        Assert.True(changes() > before);
        Assert.Equal(0, stub.Events.Values.Sum());
    }

    [AvaloniaFact]
    public async Task HiddenCombatViewsAreNotReadAndAreReadAgainWhenShown()
    {
        var (backend, stub, changes) = await ConnectAsync(live =>
        {
            live.SetSurfaceVisible("main", true);
            live.SetCombatPageVisible(true);
        });
        await using var _ = backend;
        // 主窗口最小化：页面停用，战斗浮窗元件也不显示。
        backend.SetCombatPageVisible(false);
        backend.SetSurfaceVisible("main", false);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        stub.ResetCounters();
        var shown = backend.ShownWorkspaceRevision;
        for (var i = 0; i < 5; i++)
        {
            stub.Checkpoint(true, CombatStubHost.LocalId, stub.CurrentTargetId!);
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.Equal(0, stub.Count(Snapshot));
        Assert.Equal(0, stub.Count("GET /api/v1/combat/entities/{id}/effects"));
        Assert.Equal(shown, backend.ShownWorkspaceRevision);
        // 全部界面隐藏：绑定按五秒一次读取（这 2.5 秒里至多一次）。
        Assert.True(stub.Count("GET /api/v1/game/binding") <= 1, $"隐藏期间读取绑定 {stub.Count("GET /api/v1/game/binding")} 次");

        // 战斗浮窗元件显示（视图挂在可见的浮窗上即算显示，与是否上锁无关）：与主窗口是否可见无关，立即补读并通知；
        // 浮窗算可见处，绑定与占用回到每秒。
        stub.ResetCounters();
        var before = changes();
        using var widget = new MabiTool.Desktop.Overlay.OverlayWidgetModel("combat.dps", backend);
        widget.SetShown(true);
        await Until(() => backend.ShownWorkspaceRevision == stub.WorkspaceRevision, "浮窗元件显示后补读");
        await Until(() => changes() > before, "补读后通知界面");
        Assert.Equal(1, stub.Count(Snapshot));
        await Task.Delay(2600, TestContext.Current.CancellationToken);
        Assert.True(stub.Count("GET /api/v1/game/binding") >= 2, $"浮窗显示时 2.6 秒内读取绑定 {stub.Count("GET /api/v1/game/binding")} 次");
    }

    [AvaloniaFact]
    public async Task WaitingForTheFirstSegmentPollsTheSnapshotEverySecond()
    {
        var stub = new CombatStubHost();
        stub.CloseEncounter();
        var backend = new LiveBackend(Path.Combine(Path.GetTempPath(), "moongate-missing-" + Guid.NewGuid().ToString("N"), "instance.json"),
            action => Dispatcher.UIThread.Post(action))
        {
            Handler = () => stub,
            Bootstrap = _ => Task.FromResult(stub.Bootstrap)
        };
        await using var _ = backend;
        backend.Start();
        await Until(() => backend.ShownWorkspaceRevision == stub.WorkspaceRevision, "连接替身后端");
        // 还没有当前分段：首个摘要事件丢失时也约每秒补读一次快照。
        stub.ResetCounters();
        await Task.Delay(2600, TestContext.Current.CancellationToken);
        Assert.InRange(stub.Count(Snapshot), 2, 4);
        // 有了当前分段后回到两秒。
        stub.Checkpoint(true);
        await Until(() => backend.GetCombat().Skills.Count > 0, "读到第一份分段数据");
        stub.ResetCounters();
        await Task.Delay(2600, TestContext.Current.CancellationToken);
        Assert.InRange(stub.Count(Snapshot), 1, 2);
    }

    // 快照随最新捕获先到、宿主检查点稍后才写入：当前分段的范围查询先回 404。查不到不能记成“不存在”，
    // 否则这一段剩下的时间所选目标都不再查询，页面一直停在没有结果。
    [AvaloniaFact]
    public async Task TargetInTheCurrentSegmentIsShownOnceTheCheckpointLands()
    {
        var (backend, stub, _) = await ConnectAsync();
        await using var _ = backend;
        stub.RangeMissingReplies = 3;
        await backend.SendIntentAsync("combat.target", "ent-m1", TestContext.Current.CancellationToken);
        await Until(() => backend.ShownRange is { HasRange: true, Problem: null } && backend.GetCombat() is { Pending: false, SelectedTarget: "ent-m1" },
            "检查点写入后显示所选目标的范围结果", 3);
        Assert.Equal(0, stub.RangeMissingReplies);
    }

    // 换目标的这一轮：范围结果一到就交给界面，不等覆盖率与首击的时间线查询（数据多时每次数百毫秒）。
    [AvaloniaFact]
    public async Task SwitchingTargetShowsTheRangeWithoutWaitingForTimelineReads()
    {
        var (backend, stub, _) = await ConnectAsync();
        await using var _ = backend;
        backend.SetCombatPageVisible(true);
        // 覆盖率同一分段至多每 2 秒读一次：等过这一间隔，换目标的同时分段有新伤害，这一轮要重读覆盖率（时间线查询 1.5 秒）。
        await Task.Delay(2300, TestContext.Current.CancellationToken);
        await Until(() => stub.TimelineInFlight == 0, "没有进行中的时间线查询");
        stub.TimelineDelay = TimeSpan.FromMilliseconds(1500);
        var timelines = stub.Count("POST /api/v1/combat/encounters/{id}/timeline/query");
        var clock = Stopwatch.StartNew();
        stub.Checkpoint(true);
        await backend.SendIntentAsync("combat.target", "ent-m1", TestContext.Current.CancellationToken);
        await Until(() => backend.ShownRange is { HasRange: true } && backend.GetCombat() is { Pending: false, SelectedTarget: "ent-m1" }, "显示所选目标的范围结果");
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(700), $"换目标后 {clock.Elapsed.TotalMilliseconds:0} ms 才显示");
        // 覆盖率随后读到再更新，选择不变。
        await Until(() => stub.Count("POST /api/v1/combat/encounters/{id}/timeline/query") > timelines && stub.TimelineInFlight == 0, "随后读取时间线");
        Assert.Equal("ent-m1", backend.GetCombat().SelectedTarget);
    }

    [AvaloniaFact]
    public async Task UserSelectionIsReadWithoutWaitingForTheCoalescingWindow()
    {
        var (backend, stub, _) = await ConnectAsync();
        await using var _ = backend;
        stub.ResetCounters();
        var clock = Stopwatch.StartNew();
        await backend.SendIntentAsync("combat.scope", "observation", TestContext.Current.CancellationToken);
        await Until(() => stub.Count("POST /api/v1/combat/ranges/query") > 0, "切换范围后读取整场结果");
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(150), $"用户操作后 {clock.Elapsed.TotalMilliseconds:0} ms 才读取");
        await Until(() => backend.GetCombat() is { Pending: false, SelectedScope: "observation" }, "页面显示整场结果");
    }

    [AvaloniaFact]
    public async Task EventsWithinOneFrameAreMergedIntoTheNextRoundAndTheLastStateIsShown()
    {
        var (backend, stub, _) = await ConnectAsync();
        await using var _ = backend;
        stub.ResetCounters();
        var rounds = backend.CombatRounds;
        // 两个检查点的摘要与效果事件（共四条）几乎同时到达：第一条立即读，读取进行中或帧间隔内到达的并入下一轮。
        stub.Checkpoint(true, CombatStubHost.LocalId);
        stub.Checkpoint(true, CombatStubHost.LocalId, stub.CurrentTargetId!);
        await Until(() => backend.ShownWorkspaceRevision == stub.WorkspaceRevision, "显示最后一个检查点的快照");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.InRange(backend.CombatRounds - rounds, 1, 2);
        Assert.Equal(backend.CombatRounds - rounds, stub.Count(Snapshot));
        Assert.Equal(1, stub.Count("GET /api/v1/combat/entities/{id}/effects"));
        Assert.Equal(stub.WorkspaceRevision, backend.ShownWorkspaceRevision);
    }

    // 最新捕获事件：到达即读快照（不等合并窗口）；密集到达时每秒至多读 60 轮、不丢最后的状态；
    // 界面显示后按事件的抓包时点记下显示延迟。
    [AvaloniaFact]
    public async Task LiveEventsAreReadAtOnceCappedAtSixtyRoundsAndRecordTheDisplayLatency()
    {
        var (backend, stub, _) = await ConnectAsync();
        await using var _ = backend;
        stub.ResetCounters();
        var clock = Stopwatch.StartNew();
        var revision = stub.Hit(live: true);
        await Until(() => backend.ShownWorkspaceRevision == revision, "最新捕获事件后读到新快照");
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(150), $"最新捕获事件后 {clock.Elapsed.TotalMilliseconds:0} ms 才显示");
        await Until(() => backend.DisplayLatency() is { Samples: >= 1 }, "界面显示后记下显示延迟");
        var latency = backend.DisplayLatency()!;
        Assert.InRange(latency.MedianMs, 0, 1000);

        // 400 ms 内 40 条最新捕获事件：读取轮数不超过帧上限，最后一条的修订一定显示。
        stub.ResetCounters();
        clock.Restart();
        for (var i = 0; i < 40; i++)
        {
            revision = stub.Hit(live: true);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await Until(() => backend.ShownWorkspaceRevision == revision, "显示最后一条最新捕获事件的修订");
        var reads = stub.Count(Snapshot);
        Assert.True(reads <= clock.Elapsed.TotalMilliseconds / LiveBackend.FrameInterval.TotalMilliseconds + 2,
            $"{clock.Elapsed.TotalMilliseconds:0} ms 内读了 {reads} 次快照");
        Assert.True(reads >= 2, "密集事件期间仍按帧持续读取");
    }
}
