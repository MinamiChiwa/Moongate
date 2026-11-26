using Avalonia.Headless.XUnit;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Life;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>
/// “生活”页与生活浮窗：加工倒计时、设施槽位圆点与领取、空槽与制作的二级菜单、采集折叠、总队列的确认与编辑、
/// 阻塞与暂停原因的文字、停用条件、专注钓鱼与队列互斥、浮窗交互发出的命令。
/// </summary>
public sealed class LifeTests
{
    internal static LifeCommandInfo Command(string name, bool available = true, string? cost = "5 精靈之翼") =>
        new(name, available, available ? null : "gameCli.commandMissing", cost is null ? null : 5, cost is null ? null : "精靈之翼", cost, name.StartsWith("execute_", StringComparison.Ordinal) || name == "complete_altering_work");

    private static readonly LifeSectionStatus Loaded = new(LifeReadState.Loaded, null, DateTimeOffset.UtcNow);
    private static readonly LifeSectionStatus Idle = new(LifeReadState.NotRead, null, null);

    internal static LifeTask Task(string id, string kind, string target, int count = 1, int done = 0, LifeTaskState state = LifeTaskState.Pending,
        string? facility = null, int? variant = null, string? lastCode = null, string? lastDetail = null, bool unknown = false, bool immediate = false,
        IReadOnlyList<LifeRoundResult>? results = null) =>
        new(id, kind, target, facility, variant, count, done, state, immediate, lastCode, lastDetail, unknown, DateTimeOffset.UtcNow, null, null, results ?? []);

    internal static LifeTaskQueue Queue(IReadOnlyList<LifeTask>? tasks = null, LifeQueueState state = LifeQueueState.Idle, string? current = null,
        string? pauseCode = null, string? pauseDetail = null, string? pauseTask = null, ulong revision = 7) =>
        new(new(revision), state, pauseCode, pauseDetail, pauseTask, current, current is null ? null : 25, current is null ? null : 5, "精靈之翼", 100, tasks ?? []);

    internal static LifeSnapshot Snapshot(string epoch = "epoch-1", IReadOnlyList<LifeGatherItem>? gather = null, IReadOnlyList<LifeAlterRecipe>? alter = null,
        LifeAlterQueue? queue = null, IReadOnlyList<LifeCraftRecipe>? craft = null, LifeAction? action = null, LifeOccupancy? occupied = null,
        GameConnectorStatus connector = GameConnectorStatus.Connected, IReadOnlyList<LifeCommandInfo>? commands = null, LifeTaskQueue? tasks = null,
        IReadOnlyList<LifeFacility>? facilities = null) =>
        new(epoch, new(1), connector, connector == GameConnectorStatus.Connected ? "available" : "gameCli.disabled", LifeClientState.Ready, null,
            gather is null ? Idle : Loaded, null, gather ?? [], alter is null ? Idle : Loaded, null, alter ?? [], queue is null ? Idle : Loaded, queue,
            craft is null ? Idle : Loaded, null, true, craft ?? [],
            commands ?? [Command("get_gatherable_items", cost: null), Command("execute_gathering"), Command("execute_altering"), Command("complete_altering_work", cost: null), Command("execute_crafting")],
            action, occupied, tasks ?? Queue(), facilities);

    internal static LifeGatherItem Tree(string name = "原木", string? group = "伐木", string? reason = null, bool fishing = false) => new(name, true, group, reason, fishing);

    /// <summary>排版检查用的样本快照：多个设施（含已满、已完成与加工中）、分组与“其他”、钓鱼项、多设施制作配方、暂停在被阻塞的一项的总队列。</summary>
    internal static LifeSnapshot LayoutSnapshot(bool longNames = false)
    {
        var tail = longNames ? "（名字非常非常長的物品名稱用來檢查截斷與換行是否正常）" : string.Empty;
        var works = new LifeAlterQueue(2, [
            new("皮革+" + tail, "皮革加工設備", "Completed", true, 0), new("皮革", "皮革加工設備", "InProgress", false, 3725),
            new("合金鋼錠", "金屬加工設備", "InProgress", false, 40), new("合金鋼錠", "金屬加工設備", "NotStarted", false, null),
            new("壯壯蘑菇粉末", "藥品加工設備" + tail, "Completed", true, 0)]);
        var blocked = new LifeRoundResult(3, LifeRoundOutcome.Rejected, "gameCli.blocked", "A blocking UI", "unknown_modal", null, null, null, null, null, null, null, [], null, null);
        var done = new LifeRoundResult(1, LifeRoundOutcome.Completed, null, null, null, 16, 10, "精靈之翼 5 spent, 11530 left.", 5, 11530, "精靈之翼", null, [], null, null);
        var tasks = Queue([
            Task("t0", "collect", "皮革加工設備", state: LifeTaskState.Completed, facility: "皮革加工設備", immediate: true),
            Task("t1", "gather", "原木" + tail, 50, 16, lastCode: "gameCli.blocked", lastDetail: "unknown_modal", unknown: true, results: [done, blocked]),
            Task("t2", "alter", "皮革+", 2, facility: "皮革加工設備"),
            Task("t3", "craft", "繽紛營火套組" + tail, 3, facility: "立即製作", variant: 2)
        ], LifeQueueState.Paused, pauseCode: "gameCli.blocked", pauseDetail: "unknown_modal", pauseTask: "t1");
        return Snapshot(
            gather: [Tree("原木" + tail), Tree("堅硬原木"), Tree("鮭魚" + tail, "釣魚", null, true), Tree("樹枝", null, "life.gatherMultiple"), Tree("鐵礦石", "採礦")],
            alter: [new("壯壯蘑菇粉末" + tail, false, 5, "not_enough_ingredient", [new("壯壯蘑菇汁液" + tail, 8, 0), new("水", 2, 1)], "藥品加工設備" + tail, null),
                new("皮革+", true, 3, null, [], "皮革加工設備", null), new("木材", false, 5, "queue_full", [], "木材加工設備", null)],
            queue: works,
            craft: [new("繽紛營火套組" + tail, 2, true, 1, 95.5, 4, null, [new("原木", 4, 9), new("樹枝" + tail, 2, 1)], "立即製作", null),
                new("長劍", null, false, null, null, null, "insufficient_facility_level", [], "武器製作台", null),
                new("木椅", null, true, 1, 80, null, null, [new("木材", 5, 12)], null, "life.craftNotInClient")],
            tasks: tasks,
            facilities: [new("藥品加工設備" + tail, false, null), new("皮革加工設備", false, null), new("木材加工設備", true, "recipes"), new("金屬加工設備", false, null)]);
    }

    // 本页的静态状态（不再询问、完成提示、自动领取、展开状态）跨用例保持，每个用例开始时复位。
    private static void Reset()
    {
        LifeConfirmPolicy.Reset();
        LifePageModel.ResetAnnouncements();
        LifePreference.SetAutoCollect(false);
        UiText.Apply(Avalonia.Application.Current!, UiLanguage.SimplifiedChinese);
    }

    private static readonly DateTimeOffset ReadTime = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CountdownKeepsTheLocalValueForSmallServerJumpsAndTakesLargerOnesAndNeverInventsZero()
    {
        var clock = ReadTime;
        var countdown = new LifeCountdown(() => clock);
        LifeAlterWork Work(string facility, string name, double? seconds, bool done = false) => new(name, facility, done ? "Completed" : "InProgress", done, seconds);

        countdown.Update([Work("藥品", "粉末", 100), Work("藥品", "粉末", 200), Work("皮革", "皮革+", null), Work("皮革", "皮革", 0, done: true)], ReadTime);
        Assert.Equal((100d, 200d), (countdown.Remaining("藥品", "粉末", 0), countdown.Remaining("藥品", "粉末", 1)));
        // 未知与已完成的项目没有倒计时（不显示 0）。
        Assert.Null(countdown.Remaining("皮革", "皮革+", 0));
        Assert.Null(countdown.Remaining("皮革", "皮革", 0));

        clock = ReadTime.AddSeconds(10);
        Assert.Equal(90d, countdown.Remaining("藥品", "粉末", 0));
        // 10 秒后的新读取比本地值（90）多 2 秒：保留本地值；多 3 秒内同样保留，防止数字倒退。
        countdown.Update([Work("藥品", "粉末", 92), Work("藥品", "粉末", 193)], clock);
        Assert.Equal(90d, countdown.Remaining("藥品", "粉末", 0));
        Assert.Equal(190d, countdown.Remaining("藥品", "粉末", 1));
        // 多出超过 3 秒（例如游戏里加速或换了批次）或服务端值更小：采用服务端值。
        countdown.Update([Work("藥品", "粉末", 120), Work("藥品", "粉末", 150)], clock);
        Assert.Equal((120d, 150d), (countdown.Remaining("藥品", "粉末", 0), countdown.Remaining("藥品", "粉末", 1)));
        // 到点后不为负，并提示需要重读；队列里消失的项目被清除。
        clock = ReadTime.AddSeconds(10 + 121);
        Assert.Equal(0d, countdown.Remaining("藥品", "粉末", 0));
        Assert.True(countdown.AnyDue);
        countdown.Update([], clock);
        Assert.False(countdown.AnyDue);
        Assert.Null(countdown.Remaining("藥品", "粉末", 1));
    }

    [Fact]
    public void CountdownKeepsRunningWhenTheHostRepeatsTheSameReading()
    {
        var clock = ReadTime;
        var countdown = new LifeCountdown(() => clock);
        LifeAlterWork Work(double seconds) => new("粉末", "藥品", "InProgress", false, seconds);

        // 快照没有读取时刻时按收到快照的时刻起算。
        countdown.Update([Work(100)]);
        // 宿主在两次命令行读取之间每次快照都原样给出上次读取的剩余时间：本地继续倒数，不回到 100。
        clock = ReadTime.AddSeconds(5);
        countdown.Update([Work(100)]);
        Assert.Equal(95d, countdown.Remaining("藥品", "粉末", 0));
        clock = ReadTime.AddSeconds(20);
        countdown.Update([Work(100)]);
        Assert.Equal(80d, countdown.Remaining("藥品", "粉末", 0));
        // 新一次读取给出不同的值时按原规则：比本地值小则采用服务端值。
        countdown.Update([Work(78)]);
        Assert.Equal(78d, countdown.Remaining("藥品", "粉末", 0));
    }

    [Fact]
    public void CountdownFollowsTheHostReadTimeOnlyTheWorkingItemCountsDownAndQueuedItemsAddUp()
    {
        var clock = ReadTime;
        LifeAlterWork Work(string name, string state, double? seconds) => new(name, "木材加工設備", state, state == "Completed", seconds);
        IReadOnlyList<LifeAlterWork> works = [Work("木板", "Completed", 0), Work("木材", "InProgress", 120), Work("木材", "NotStarted", 300), Work("木板", "NotStarted", 300)];
        var countdown = new LifeCountdown(() => clock);
        countdown.Update(works, ReadTime);

        // 设施内逐项加工：当前一项剩 2 分钟，全部完成 = 2 + 5 + 5 分钟；排队项完成还需 = 当前剩余 + 它及前面排队项的时长。
        Assert.Equal(new LifeFacilityTimeline(1, 2, 120, 720), countdown.Facility("木材加工設備"));
        Assert.Equal((120d, 420d, 720d), (countdown.Remaining("木材加工設備", "木材", 0), countdown.Remaining("木材加工設備", "木材", 1), countdown.Remaining("木材加工設備", "木板", 1)));
        Assert.Null(countdown.Remaining("木材加工設備", "木板", 0));

        // 30 秒后切换页面再回来：新建的倒计时按同一读取时刻推算，不回到读取时的 120 秒。
        clock = ReadTime.AddSeconds(30);
        var reopened = new LifeCountdown(() => clock);
        reopened.Update(works, ReadTime);
        Assert.Equal(new LifeFacilityTimeline(1, 2, 90, 690), reopened.Facility("木材加工設備"));
        Assert.Equal(390d, reopened.Remaining("木材加工設備", "木材", 1));

        // 加工中的一项到点：只有它到 0 并提示重读，排队项的时长不减少。
        clock = ReadTime.AddSeconds(125);
        Assert.True(reopened.AnyDue);
        Assert.Equal(new LifeFacilityTimeline(1, 2, 0, 600), reopened.Facility("木材加工設備"));

        // 只有排队项目时（加工中的一项已领取或读到前尚未开始）：时长固定，过多久都不会到点，也不显示“即将完成”。
        var queuedOnly = new LifeCountdown(() => clock);
        queuedOnly.Update([Work("木材", "NotStarted", 30)], ReadTime);
        Assert.False(queuedOnly.AnyDue);
        Assert.Equal((new LifeFacilityTimeline(0, 1, null, 30), 30d), (queuedOnly.Facility("木材加工設備"), queuedOnly.Remaining("木材加工設備", "木材", 0)));
    }

    [Fact]
    public void RememberingAppliesToOneBackendRunOnly()
    {
        LifeConfirmPolicy.Reset();
        Assert.True(LifeConfirmPolicy.ShouldAsk("epoch-1"));
        LifeConfirmPolicy.Remember("epoch-1");
        Assert.False(LifeConfirmPolicy.ShouldAsk("epoch-1"));
        // 宿主重启（后端代次变化）后恢复每次询问；没有代次时总是询问。
        Assert.True(LifeConfirmPolicy.ShouldAsk("epoch-2"));
        Assert.True(LifeConfirmPolicy.ShouldAsk(null));
        LifeConfirmPolicy.Reset();
        Assert.True(LifeConfirmPolicy.ShouldAsk("epoch-1"));
    }

    [AvaloniaFact]
    public void StartingTheQueueAsksWithEachTaskCostAndTheCeilingAndRememberingSkipsLaterPrompts()
    {
        Reset();
        var tasks = Queue([Task("a", "gather", "原木", 25), Task("b", "alter", "皮革+", 2, facility: "皮革加工設備"), Task("c", "collect", "皮革加工設備", facility: "皮革加工設備")]);
        var backend = new SampleBackend { Life = Snapshot(tasks: tasks) };
        using var model = new LifePageModel(backend);
        Assert.True(model.CanStart);
        Assert.Equal("开始", model.StartText);

        model.StartCommand.Execute(null);
        Assert.True(model.ConfirmOpen);
        Assert.Empty(backend.LifeQueueCommands);
        Assert.Equal("确认开始队列", model.ConfirmTitle);
        // 采集写目标数量与估算的调用次数（剩余 25 个 ÷ 10 向上取整为 3 次），整队上限按调用次数 × 单次花费计算。
        Assert.Contains("1. 采集 原木 × 25 个：目标 25 个，约 3 次调用 × 每次 5 精靈之翼 = 15 精靈之翼", model.ConfirmLines);
        Assert.Contains("采集每次以 10 个为目标，实得多于 10 个时调用次数更少。", model.ConfirmLines);
        Assert.Contains("3. 领取 皮革加工設備：不花费精靈之翼", model.ConfirmLines);
        Assert.Contains("整队花费上限：25 精靈之翼", model.ConfirmLines);
        Assert.Contains("执行会自动移动角色。", model.ConfirmLines);

        // 取消不发出任何请求。
        model.CancelCommand.Execute(null);
        Assert.False(model.ConfirmOpen);
        Assert.Empty(backend.LifeQueueCommands);

        // 确认并选择本次运行不再询问：请求带上确认框写的整队上限。
        model.StartCommand.Execute(null);
        model.ConfirmRemember = true;
        model.ConfirmCommand.Execute(null);
        Assert.Equal(("start", (int?)25), (backend.LifeQueueCommands.Single().Action, backend.LifeQueueCommands.Single().ExpectedCostCeiling));

        // 之后直接发出；换了一次后端运行（代次变化）后恢复询问。
        model.StartCommand.Execute(null);
        Assert.False(model.ConfirmOpen);
        Assert.Equal(2, backend.LifeQueueCommands.Count);
        backend.PublishLife(Snapshot("epoch-2", tasks: tasks));
        model.StartCommand.Execute(null);
        Assert.True(model.ConfirmOpen);
        Assert.Equal(2, backend.LifeQueueCommands.Count);
    }

    [AvaloniaFact]
    public void FacilityDotsShowWorkStatesScheduledTasksAndAnEmptySlotAndCompletionIsAnnouncedOnce()
    {
        Reset();
        var works = new LifeAlterQueue(1, [new("皮革+", "皮革加工設備", "Completed", true, 0), new("皮革", "皮革加工設備", "InProgress", false, 125.5),
            new("木材", "木材加工設備", "NotStarted", false, null)]);
        var tasks = Queue([Task("q", "alter", "皮革+", 2, 1, facility: "皮革加工設備")]);
        var facilities = new[] { new LifeFacility("皮革加工設備", false, null), new LifeFacility("木材加工設備", true, "execute") };
        var backend = new SampleBackend { Life = Snapshot(queue: works, tasks: tasks, facilities: facilities) with { Works = new(LifeReadState.Loaded, null, ReadTime) } };
        using var model = new LifePageModel(backend, () => ReadTime);

        Assert.Equal(["皮革加工設備", "木材加工設備"], model.Facilities.Select(x => x.Name));
        var leather = model.Facilities[0];
        // 已完成、加工中、总队列里剩下的 1 项已安排、一个空槽；设施名不带等级（命令行不提供）。
        Assert.Equal([LifeDotState.Done, LifeDotState.Working, LifeDotState.Scheduled, LifeDotState.Empty], leather.Dots.Select(x => x.State));
        Assert.Equal("1 项待领取 · 当前剩余 02:06", leather.Subtitle);
        Assert.True(leather.CollectEnabled);
        Assert.Contains("剩余 02:06", leather.Dots[1].Tip);
        // 槽位已满：没有空槽，写明已满；游戏里排队未开始的加工是“已安排”。
        var wood = model.Facilities[1];
        Assert.Equal([LifeDotState.Scheduled], wood.Dots.Select(x => x.State));
        Assert.Equal("槽位已满", wood.FullNote);
        // 没有已完成的加工：“全部领取”仍在，停用并写明原因。
        Assert.Equal((false, "全部领取", "没有已完成的加工"), (wood.CollectEnabled, wood.CollectText, wood.CollectTip));
        // 左上角状态给出完成提示（不弹窗、不响铃）；同一批完成不重复提示。
        Assert.Equal("加工完成：皮革加工設備 1 项 待领取", backend.LastNotice);
        backend.LastNotice = null;
        backend.PublishLife(Snapshot(queue: works, tasks: tasks, facilities: facilities));
        Assert.Null(backend.LastNotice);
        // 刷新沿用同一个设施块（控件不重建）。
        Assert.Same(leather, model.Facilities[0]);
        Assert.False(model.ConfirmOpen);
    }

    [AvaloniaFact]
    public void FacilityTileShowsTheSerialTimesAndReopeningThePageKeepsCountingFromTheReadTime()
    {
        Reset();
        var works = new LifeAlterQueue(0, [new("木材", "木材加工設備", "InProgress", false, 120), new("木材", "木材加工設備", "NotStarted", false, 300),
            new("木板", "木材加工設備", "NotStarted", false, 300)]);
        var backend = new SampleBackend { Life = Snapshot(queue: works, facilities: [new("木材加工設備", false, null)]) with { Works = new(LifeReadState.Loaded, null, ReadTime) } };
        var clock = ReadTime;
        using (var model = new LifePageModel(backend, () => clock))
        {
            var wood = model.Facilities.Single();
            Assert.Equal("当前剩余 02:00 · 全部完成 12:00", wood.Subtitle);
            Assert.Equal(("木材：剩余 02:00", "木材：排队中，约 07:00 后完成", "木板：排队中，约 12:00 后完成"), (wood.Dots[0].Tip, wood.Dots[1].Tip, wood.Dots[2].Tip));
        }
        // 30 秒后切换回本页（页面不缓存，新建模型）：按宿主读取时刻继续倒数，不回到读取时的数值。
        clock = ReadTime.AddSeconds(30);
        using var reopened = new LifePageModel(backend, () => clock);
        Assert.Equal("当前剩余 01:30 · 全部完成 11:30", reopened.Facilities.Single().Subtitle);
    }

    [AvaloniaFact]
    public void TheEmptySlotMenuListsOnlyThatFacilitysRecipesAndAddsToTheQueue()
    {
        Reset();
        var alter = new LifeAlterRecipe[]
        {
            new("皮革+", true, 3, null, [], "皮革加工設備", null), new("皮革", false, 2, "not_enough_ingredient", [new("獸皮", 4, 1)], "皮革加工設備", null),
            new("木材", true, 5, null, [], "木材加工設備", null)
        };
        var backend = new SampleBackend { Life = Snapshot(alter: alter, queue: new(0, []), facilities: [new("皮革加工設備", false, null), new("木材加工設備", false, null)]) };
        using var model = new LifePageModel(backend);
        Assert.Null(model.Picker);

        model.Facilities[0].Dots.Single(x => x.IsEmpty).Command!.Execute(null);
        var picker = model.Picker!;
        Assert.Equal(["皮革+", "皮革"], picker.Items.Select(x => x.Name));
        Assert.Contains("材料不足", picker.Items[1].Status);
        Assert.Equal(("缺：獸皮 1/4", false), (picker.Items[1].Materials, picker.Items[1].CanAdd));
        Assert.True(picker.Items[0].CanAdd);
        picker.Items[0].CountText = "2";
        picker.Items[0].AddCommand.Execute(picker.Items[0]);
        var add = Assert.Single(backend.LifeTaskAdds);
        Assert.Equal(("alter", "皮革+", "皮革加工設備", 2, (string?)null), (add.Kind, add.DisplayName, add.Facility, add.Count, add.ExpectedCost));
        // 加入后菜单收起；空槽的安排进入总队列，不发出执行命令。
        Assert.False(model.PickerOpen);
        Assert.Empty(backend.LifeActions);
        Assert.Empty(backend.LifeQueueCommands);
    }

    [AvaloniaFact]
    public void CollectRunsImmediatelyWhenIdleAndQueuesNextWhileRunningAndTheCraftMenuIsUnverifiedAndCapsTheCount()
    {
        Reset();
        var works = new LifeAlterQueue(1, [new("皮革+", "皮革加工設備", "Completed", true, 0)]);
        var recipe = new LifeCraftRecipe("繽紛營火套組", 2, true, 1, 95.5, null, null, [new("原木", 4, 9)], "立即製作", null);
        var backend = new SampleBackend { Life = Snapshot(queue: works, craft: [recipe], facilities: [new("皮革加工設備", false, null)]) };
        using var model = new LifePageModel(backend);

        model.Facilities[0].CollectCommand.Execute(null);
        var collect = Assert.Single(backend.LifeActions);
        Assert.Equal(("collect", null, "皮革加工設備", null), (collect.Kind, collect.DisplayName, collect.Facility, collect.ExpectedCost));
        // 队列执行中：领取排到当前任务之后，不另起执行。
        var running = Queue([Task("r", "gather", "原木", 3, state: LifeTaskState.Running)], LifeQueueState.Running, current: "r");
        backend.PublishLife(Snapshot(queue: works, craft: [recipe], tasks: running, facilities: [new("皮革加工設備", false, null)], occupied: new("life", true)));
        model.Facilities[0].CollectCommand.Execute(null);
        Assert.Equal(("collect", "皮革加工設備", "next"), (backend.LifeTaskAdds.Single().Kind, backend.LifeTaskAdds.Single().Facility, backend.LifeTaskAdds.Single().Position));
        Assert.Single(backend.LifeActions);

        backend.PublishLife(Snapshot(queue: works, craft: [recipe], facilities: [new("皮革加工設備", false, null)]));
        var tile = model.CraftGroups.Single();
        Assert.Equal(("立即製作", "1 个配方 · 可制作 1"), (tile.Name, tile.Summary));
        tile.OpenCommand.Execute(null);
        var item = model.Picker!.Items.Single();
        Assert.Contains("未经实机确认", model.Picker.Note);
        Assert.Contains("成功率 95.5%", item.Status);
        Assert.Contains("原木 9/4", item.Materials);
        Assert.Equal("繽紛營火套組（版本 2）", item.Name);
        item.CountText = "0";
        item.AddCommand.Execute(item);
        Assert.True(model.MessageFailed);
        Assert.Single(backend.LifeTaskAdds);
        item.CountText = "3";
        item.AddCommand.Execute(item);
        var craft = backend.LifeTaskAdds[^1];
        Assert.Equal(("craft", "繽紛營火套組", 3, 2, "立即製作"), (craft.Kind, craft.DisplayName, craft.Count, craft.Variant, craft.Facility));
    }

    [AvaloniaFact]
    public void RecipeMenuSearchMatchesRecipeAndMaterialNamesIgnoringCaseAndWidthAndKeepsTypedCounts()
    {
        Reset();
        var craft = new LifeCraftRecipe[]
        {
            new("繽紛營火套組", 2, true, 1, 95.5, null, null, [new("原木", 4, 9)], "立即製作", null),
            new("Iron Sword", null, true, 1, 80, null, null, [new("鐵錠", 2, 5)], "立即製作", null),
            new("木椅", null, true, 1, 80, null, null, [new("木材", 5, 12)], "立即製作", null)
        };
        var backend = new SampleBackend { Life = Snapshot(craft: craft) };
        using var model = new LifePageModel(backend);
        model.CraftGroups.Single().OpenCommand.Execute(null);
        var picker = model.Picker!;
        var chair = picker.Items[2];
        chair.CountText = "4";

        // 材料名命中；全角与大小写不同的配方名命中。
        picker.Search = "原木";
        Assert.Equal(["繽紛營火套組（版本 2）"], picker.Items.Select(x => x.Name));
        picker.Search = "ｉｒｏｎ";
        Assert.Equal(["Iron Sword"], picker.Items.Select(x => x.Name));
        // 没有匹配时写明，不显示“没有配方”。
        picker.Search = "不存在";
        Assert.Empty(picker.Items);
        Assert.Equal((true, "没有匹配的配方"), (picker.HasEmpty, picker.EmptyText));
        // 清空恢复全部；行是同一批对象，填好的数量保留。
        picker.Search = "";
        Assert.Equal(3, picker.Items.Count);
        Assert.Same(chair, picker.Items[2]);
        Assert.Equal(("4", false), (chair.CountText, picker.HasEmpty));
    }

    [AvaloniaFact]
    public void SkillGroupsStartCollapsedBuildRowsOnlyWhenExpandedAndFishingPointsToFocusedFishing()
    {
        Reset();
        var backend = new SampleBackend
        {
            Life = Snapshot(gather: [Tree("堅硬原木", null, "life.gatherNotInClient"), Tree(), Tree("鮭魚", "釣魚", null, true), Tree("樹枝", null, "life.gatherMultiple")])
        };
        using var model = new LifePageModel(backend);

        Assert.Equal(["伐木", "釣魚", "其他"], model.SkillGroups.Select(x => x.Name));
        Assert.All(model.SkillGroups, group => Assert.Empty(group.Items));
        Assert.Equal("2 项", model.SkillGroups[2].CountText);

        var other = model.SkillGroups[2];
        other.ToggleCommand.Execute(null);
        Assert.True(other.IsExpanded);
        Assert.Equal(["堅硬原木", "樹枝"], other.Items.Select(x => x.Name));
        Assert.Contains("客户端表里没有找到掉落该物品的采集点", other.Reason);
        Assert.Contains("多个生活技能", other.Reason);

        var fishing = model.SkillGroups[1];
        fishing.ToggleCommand.Execute(null);
        var fish = fishing.Items.Single();
        Assert.Equal((true, false, "钓鱼请使用专注钓鱼"), (fish.Fishing, fish.CanAdd, fish.ToolText));
        var focused = 0;
        model.FishingFocusRequested += (_, _) => focused++;
        fish.FishingCommand.Execute(null);
        Assert.Equal(1, focused);

        model.SkillGroups[0].ToggleCommand.Execute(null);
        var tree = model.SkillGroups[0].Items.Single();
        // 目标数量框默认 10 个（命令行每次以 10 个为目标）；超过 500 个在本地提示，不发出请求。
        Assert.Equal(("10", "个"), (tree.CountText, model.GatherCountLabel));
        tree.CountText = "501";
        tree.AddCommand.Execute(tree);
        Assert.Equal((true, "数量需要是 1 到 500 的整数"), (model.MessageFailed, model.Message));
        Assert.Empty(backend.LifeTaskAdds);
        tree.CountText = "30";
        tree.AddCommand.Execute(tree);
        Assert.Equal(("gather", "原木", 30), (backend.LifeTaskAdds.Single().Kind, backend.LifeTaskAdds.Single().DisplayName, backend.LifeTaskAdds.Single().Count));
        // 刷新保持展开状态与已生成的行。
        backend.PublishLife(Snapshot(gather: [Tree("堅硬原木", null, "life.gatherNotInClient"), Tree(), Tree("鮭魚", "釣魚", null, true), Tree("樹枝", null, "life.gatherMultiple")]));
        Assert.True(model.SkillGroups[0].IsExpanded);
        Assert.Same(tree, model.SkillGroups[0].Items.Single());
        Assert.Equal("30", tree.CountText);
    }

    [AvaloniaFact]
    public void QueueRowsCanBeEditedReorderedAndRemovedAndAddingWhileRunningAsksForTheCost()
    {
        Reset();
        var tasks = Queue([Task("r", "gather", "原木", 50, 32, LifeTaskState.Running), Task("a", "alter", "皮革+", 2, facility: "皮革加工設備"),
            Task("b", "craft", "繽紛營火套組", 1, facility: "立即製作"), Task("d", "collect", "皮革加工設備", state: LifeTaskState.Completed, facility: "皮革加工設備", immediate: true)],
            LifeQueueState.Running, current: "r");
        var backend = new SampleBackend { Life = Snapshot(gather: [Tree()], tasks: tasks, occupied: new("life", true)) };
        using var model = new LifePageModel(backend);

        var rows = model.Tasks.ToDictionary(x => x.Id);
        Assert.Equal(["r", "a", "b", "d"], model.Tasks.Select(x => x.Id));
        Assert.Equal((true, false, false), (rows["r"].IsRunning, rows["r"].CanEdit, rows["r"].CanRemove));
        Assert.Equal(("已采集 32 / 50 个", 0.64, "执行中"), (rows["r"].ProgressText, rows["r"].Progress, rows["r"].StateText));
        Assert.Equal((true, true, false, true), (rows["a"].CanEdit, rows["a"].CanMove, rows["a"].CanUp, rows["a"].CanDown));
        Assert.Equal(("已完成", false), (rows["d"].StateText, rows["d"].CanMove));
        Assert.True(model.CanPause && model.CanStopCurrent && !model.CanStart);

        // 下移、数量加一、数量框改值、删除：都带页面所见的队列修订。
        rows["a"].DownCommand.Execute(null);
        rows["a"].PlusCommand.Execute(null);
        rows["b"].CountText = "4";
        model.CommitCount(rows["b"]);
        rows["b"].CountText = "x";
        model.CommitCount(rows["b"]);
        Assert.True(model.MessageFailed);
        Assert.Equal("1", rows["b"].CountText);
        rows["d"].RemoveCommand.Execute(null);
        Assert.Equal([("a", 7UL, (int?)null, (int?)1), ("a", 7UL, 3, null), ("b", 7UL, 4, null)],
            backend.LifeTaskUpdates.Select(x => (x.Id, x.Request.ExpectedRevision.Value, x.Request.Count, x.Request.Index)));
        Assert.Equal(["d"], backend.LifeTaskRemovals);
        model.Move("b", 0);
        Assert.Equal(("b", (int?)0), (backend.LifeTaskUpdates[^1].Id, backend.LifeTaskUpdates[^1].Request.Index));

        // 运行中加入任务：先确认这项的花费，确认后带上单次花费文字（并入已确认的上限）。
        model.SkillGroups[0].ToggleCommand.Execute(null);
        var tree = model.SkillGroups[0].Items.Single();
        tree.AddCommand.Execute(tree);
        Assert.True(model.ConfirmOpen);
        Assert.Equal("确认加入队列", model.ConfirmTitle);
        Assert.Contains(model.ConfirmLines, line => line.Contains("目标 10 个，约 1 次调用") && line.Contains("并入队列的花费上限 5 精靈之翼"));
        Assert.Empty(backend.LifeTaskAdds);
        model.ConfirmCommand.Execute(null);
        Assert.Equal(("gather", "5 精靈之翼"), (backend.LifeTaskAdds.Single().Kind, backend.LifeTaskAdds.Single().ExpectedCost));

        model.PauseCommand.Execute(null);
        model.StopCurrentCommand.Execute(null);
        Assert.Equal(["pause", "stop-current"], backend.LifeQueueCommands.Select(x => x.Action));
    }

    [AvaloniaFact]
    public void QueueControlsFollowTheStateAndPauseReasonsAndLocksAreShownInChinese()
    {
        Reset();
        var pending = Queue([Task("a", "gather", "原木", 5)]);
        var backend = new SampleBackend { Life = Snapshot(gather: [Tree()], tasks: pending, occupied: new("rooms-12", false)) };
        using var model = new LifePageModel(backend);
        Assert.False(model.CanStart);
        Assert.Contains("合奏", model.Blocked);
        model.StartCommand.Execute(null);
        Assert.False(model.ConfirmOpen);

        backend.PublishLife(Snapshot(gather: [Tree()], tasks: pending, commands: [Command("execute_gathering", available: false)]));
        Assert.Contains("采集不可用：游戏不支持所需指令", model.Blocked);

        // 暂停在被阻塞的一项：写明做法、停在哪一项与已完成多少，游戏里的结果未知时注明；按钮为“继续”。
        var blocked = new LifeRoundResult(3, LifeRoundOutcome.Rejected, "gameCli.blocked", "A blocking UI", "unknown_modal", null, null, null, null, null, null, null, [], null, null);
        var paused = Queue([Task("a", "gather", "原木", 50, 16, lastCode: "gameCli.blocked", lastDetail: "unknown_modal", unknown: true, results: [blocked])],
            LifeQueueState.Paused, pauseCode: "gameCli.blocked", pauseDetail: "unknown_modal", pauseTask: "a");
        backend.PublishLife(Snapshot(gather: [Tree()], tasks: paused));
        Assert.Equal(("已暂停", "继续"), (model.QueueState, model.StartText));
        Assert.StartsWith("游戏画面上有窗口挡住，请关闭后重试", model.QueuePause);
        Assert.Contains("停在：采集 原木 × 50 个，已采集 16 / 50 个", model.QueuePause);
        Assert.Contains("游戏里的结果未知", model.QueuePause);
        var row = model.Tasks.Single();
        Assert.Equal(("暂停在这一项", true), (row.StateText, row.IsPausedHere));
        Assert.StartsWith("游戏画面上有窗口挡住", row.Result);
        Assert.True(model.CanStart);

        backend.PublishLife(Snapshot(gather: [Tree()], tasks: paused with { PauseCode = "life.hostRestarted", PauseDetail = null }));
        Assert.StartsWith("工具重启后暂停", model.QueuePause);
        backend.PublishLife(Snapshot(gather: [Tree()], tasks: paused with { PauseCode = "gameCli.notEnoughCurrency", PauseDetail = null }));
        Assert.StartsWith("精靈之翼不足", model.QueuePause);
        Assert.All(LifeText.KnownReasons, code => Assert.False(string.IsNullOrWhiteSpace(LifeText.Reason(code)) || LifeText.Reason(code).Contains(code, StringComparison.Ordinal), code));
    }

    [AvaloniaFact]
    public void PageReadsOnEntryAndTheTraditionalTextIsUsedWithTheGameNamesUntouched()
    {
        Reset();
        var backend = new SampleBackend { Life = Snapshot(gather: [Tree()]) };
        using var model = new LifePageModel(backend);
        Assert.Equal(("all", (string?)null), backend.LifeRefreshes.Single());
        try
        {
            UiText.Apply(Avalonia.Application.Current!, UiLanguage.TraditionalChinese);
            Assert.Equal("採集", model.GatherTitle);
            Assert.Equal("總佇列", model.QueueTitle);
            Assert.Equal("伐木", model.SkillGroups[0].Name);
            Assert.Equal("1 項", model.SkillGroups[0].CountText);
            Assert.Equal("專注釣魚", model.Fishing.Title);
        }
        finally { UiText.Apply(Avalonia.Application.Current!, UiLanguage.SimplifiedChinese); }
    }

    [AvaloniaFact]
    public void OverlayCollectArrangePauseAndContinueSendTheSameRequestsAsThePage()
    {
        Reset();
        var works = new LifeAlterQueue(1, [new("皮革+", "皮革加工設備", "Completed", true, 0)]);
        var alter = new LifeAlterRecipe[] { new("皮革+", true, 3, null, [], "皮革加工設備", null), new("木材", true, 5, null, [], "木材加工設備", null) };
        var pending = Queue([Task("a", "gather", "原木", 20)], LifeQueueState.Paused, pauseCode: "life.pausedByUser", pauseTask: "a");
        var backend = new SampleBackend { Life = Snapshot(alter: alter, queue: works, tasks: pending, facilities: [new("皮革加工設備", false, null), new("木材加工設備", false, null)]) };
        using var overlay = new LifeOverlayData(backend);

        var leather = overlay.Facilities[0];
        Assert.True(leather.CollectEnabled);
        // 没有已完成项目的设施：“领取”停用并写明原因。
        Assert.Equal((false, "没有已完成的加工"), (overlay.Facilities[1].CollectEnabled, overlay.Facilities[1].CollectTip));
        leather.CollectCommand.Execute(null);
        Assert.Equal(("collect", "皮革加工設備"), (backend.LifeActions.Single().Kind, backend.LifeActions.Single().Facility));

        // 空槽：在元件内打开该设施的配方菜单，只列该设施的配方；搜索框按配方名过滤（与页面同一套规则）；加入总队列 1 项后菜单关闭。
        leather.Dots.Single(x => x.IsEmpty).Command!.Execute(null);
        Assert.True(overlay.MenuOpen);
        Assert.Equal(["皮革+"], overlay.Menu.Select(x => x.Name));
        overlay.MenuSearch = "木材";
        Assert.Equal((0, true, "没有匹配的配方"), (overlay.Menu.Count, overlay.MenuHasEmpty, overlay.MenuEmptyText));
        overlay.MenuSearch = "皮革＋";
        Assert.Equal(["皮革+"], overlay.Menu.Select(x => x.Name));
        overlay.Menu[0].AddCommand.Execute(null);
        Assert.Equal(("alter", "皮革+", "皮革加工設備", 1), (backend.LifeTaskAdds.Single().Kind, backend.LifeTaskAdds.Single().DisplayName, backend.LifeTaskAdds.Single().Facility, backend.LifeTaskAdds.Single().Count));
        Assert.False(overlay.MenuOpen);
        // 元件上锁：菜单关闭。
        leather.Dots.Single(x => x.IsEmpty).Command!.Execute(null);
        overlay.CloseTransient();
        Assert.False(overlay.MenuOpen);

        // 继续：先在元件内确认整队上限，确认后发出开始；运行中可暂停与停止当前。
        Assert.Equal("继续", overlay.ContinueText);
        overlay.ContinueCommand.Execute(null);
        Assert.True(overlay.ConfirmOpen);
        Assert.Contains(overlay.ConfirmLines, line => line.Contains("整队花费上限 10 精靈之翼"));
        Assert.Empty(backend.LifeQueueCommands);
        overlay.ConfirmCommand.Execute(null);
        Assert.Equal(("start", (int?)10), (backend.LifeQueueCommands.Single().Action, backend.LifeQueueCommands.Single().ExpectedCostCeiling));

        backend.PublishLife(Snapshot(alter: alter, queue: works, tasks: Queue([Task("a", "gather", "原木", 20, 10, LifeTaskState.Running), Task("b", "gather", "鐵礦石", 10)],
            LifeQueueState.Running, current: "a"), occupied: new("life", true)));
        Assert.Equal(("采集 原木 × 20 个 · 已采集 10 / 20 个", "下一项：采集 鐵礦石 × 10 个", 0.5, true),
            (overlay.CurrentText, overlay.NextText, overlay.CurrentProgress, overlay.HasCurrentProgress));
        Assert.True(overlay.CanPause && overlay.CanStop && !overlay.ShowContinue);
        overlay.PauseCommand.Execute(null);
        overlay.StopCommand.Execute(null);
        Assert.Equal(["start", "pause", "stop-current"], backend.LifeQueueCommands.Select(x => x.Action));
    }

    [Fact]
    public void FishingDurationBoxesConvertToSecondsAndBlankMeansUntilStopAndOutOfRangeIsRejectedLocally()
    {
        Assert.True(LifeFishingPanel.TryDuration("", " ", "", out var none, out var invalid));
        Assert.Equal((null, false), (none, invalid));
        Assert.True(LifeFishingPanel.TryDuration("1", "30", "", out var total, out _));
        Assert.Equal(5400, total);
        Assert.True(LifeFishingPanel.TryDuration("", "1", "", out total, out _));
        Assert.Equal(60, total);
        Assert.True(LifeFishingPanel.TryDuration("24", "", "0", out total, out _));
        Assert.Equal(86400, total);
        // 不足 60 秒、超过 24 小时、不是整数都不通过。
        foreach (var (hours, minutes, seconds) in new[] { ("", "", "59"), ("24", "0", "1"), ("", "", "0"), ("x", "", ""), ("", "-5", ""), ("", "1.5", "") })
        {
            Assert.False(LifeFishingPanel.TryDuration(hours, minutes, seconds, out var value, out var bad));
            Assert.True(bad);
            Assert.Null(value);
        }
    }

    [AvaloniaFact]
    public void FishingStartIsDisabledWithTheBackendReasonAndAnOutOfRangeDurationSendsNothing()
    {
        Reset();
        var backend = new SampleBackend { Life = Snapshot(gather: [Tree()]), FishingState = new(FishingRead.Loaded, false, "life.fishing.layoutUnsupported", null) };
        using var model = new LifePageModel(backend);
        Assert.False(model.Fishing.CanStart);
        Assert.Contains("1280×960", model.Fishing.StartBlocked);
        model.Fishing.StartCommand.Execute(null);
        Assert.Empty(backend.FishingCommands);
        Assert.False(model.ConfirmOpen);

        // 没有读到钓鱼状态时写“未读取”，不是可用。
        backend.FishingState = FishingView.NotRead;
        backend.PublishLife(Snapshot(gather: [Tree()]));
        Assert.False(model.Fishing.CanStart);
        Assert.Equal("未读取", model.Fishing.StartBlocked);

        backend.FishingState = new(FishingRead.Loaded, true, null, null);
        backend.PublishLife(Snapshot(gather: [Tree()]));
        Assert.True(model.Fishing.CanStart);
        model.Fishing.Seconds = "30";
        model.Fishing.StartCommand.Execute(null);
        Assert.True(model.Fishing.HasDurationError);
        Assert.False(model.ConfirmOpen);
        Assert.Empty(backend.FishingCommands);
        // 改动输入后提示消失。
        model.Fishing.Seconds = "90";
        Assert.False(model.Fishing.HasDurationError);
    }

    [AvaloniaFact]
    public void FishingStartAsksFirstThenRunsAndStopAndCancelReachTheBackendWithTheEndDisplayed()
    {
        Reset();
        var backend = new SampleBackend { Life = Snapshot(gather: [Tree()], tasks: Queue([Task("a", "gather", "原木", 1)])) };
        using var model = new LifePageModel(backend);
        model.Fishing.Hours = "1";
        model.Fishing.Minutes = "30";

        model.Fishing.StartCommand.Execute(null);
        Assert.True(model.ConfirmOpen);
        Assert.Empty(backend.FishingCommands);
        Assert.Contains("确认开始专注钓鱼", model.ConfirmTitle);
        Assert.Contains(model.ConfirmLines, text => text.Contains("F9"));
        Assert.Contains(model.ConfirmLines, text => text.Contains("合奏、演奏、染色"));
        // 取消不发请求。
        model.CancelCommand.Execute(null);
        Assert.False(model.ConfirmOpen);
        Assert.Empty(backend.FishingCommands);

        // 确认并选择本次运行不再询问：发出带目标秒数的开始请求；首次抛竿前显示“未开始计时”，不是 0。
        model.Fishing.StartCommand.Execute(null);
        model.ConfirmRemember = true;
        model.ConfirmCommand.Execute(null);
        Assert.Equal([("start", (int?)5400)], backend.FishingCommands);
        Assert.True(model.Fishing.IsRunning);
        Assert.Equal("等待上钩", model.Fishing.PhaseText);
        Assert.StartsWith("未开始计时", model.Fishing.ElapsedText);
        Assert.DoesNotContain("00:00:00", model.Fishing.ElapsedText);
        Assert.False(model.Fishing.CanEditDuration);

        // 收竿结束：离开姿态没有画面证据时写明未能确认。
        model.Fishing.StopCommand.Execute(null);
        Assert.Equal(("stop", (int?)null), backend.FishingCommands[1]);
        Assert.False(model.Fishing.IsRunning);
        Assert.Contains("已按你的要求收竿结束", model.Fishing.EndText);
        Assert.Contains("未能确认已离开钓鱼姿态", model.Fishing.EndText);

        // 之后直接开始（不再询问）；立即停止发出取消。
        model.Fishing.Hours = ""; model.Fishing.Minutes = "";
        model.Fishing.StartCommand.Execute(null);
        Assert.False(model.ConfirmOpen);
        Assert.Equal(("start", (int?)null), backend.FishingCommands[2]);
        model.Fishing.CancelCommand.Execute(null);
        Assert.Equal("cancel", backend.FishingCommands[3].Action);
        Assert.Contains("已立即停止", model.Fishing.EndText);

        // 总队列的确认类别不受钓鱼“不再询问”影响。
        model.StartCommand.Execute(null);
        Assert.True(model.ConfirmOpen);
    }

    [AvaloniaFact]
    public void FishingAndTheQueueExcludeEachOtherAndTheElapsedDisplayFollowsTheClock()
    {
        Reset();
        var gather = new[] { Tree() };
        var pending = Queue([Task("a", "gather", "原木", 5)]);
        var backend = new SampleBackend { Life = Snapshot(gather: gather, tasks: pending) };
        using var model = new LifePageModel(backend);
        Assert.True(model.CanStart);

        // 钓鱼进行中：队列不能开始并写明原因（活动锁的占用者同样是生活模块），队列仍可编辑。
        backend.FishingState = backend.FishingState with { Run = FishingRun(clockStarted: true, elapsed: 3725.5, target: 7200) };
        backend.PublishLife(Snapshot(gather: gather, tasks: pending, occupied: new("life", true)));
        Assert.False(model.CanStart);
        Assert.Contains("专注钓鱼进行中", model.Blocked);
        Assert.True(model.Tasks.Single().CanEdit);
        Assert.Contains("已用 01:02", model.Fishing.ElapsedText);
        Assert.Contains("目标 02:00:00", model.Fishing.ElapsedText);
        Assert.False(model.Fishing.CanStart);

        // 钓鱼结束后队列恢复；队列执行中开始钓鱼按钮停用并写明正在执行的任务。
        backend.FishingState = backend.FishingState with { Run = FishingRun("succeeded", false, true, 100, 7200, "duration-reached", FishingPoseState.Observed) };
        backend.PublishLife(Snapshot(gather: gather, tasks: pending));
        Assert.True(model.CanStart);
        Assert.Contains("已到设定时长", model.Fishing.EndText);
        Assert.Contains("已确认离开钓鱼姿态", model.Fishing.EndText);
        Assert.True(model.Fishing.CanStart);

        backend.PublishLife(Snapshot(gather: gather, tasks: Queue([Task("a", "gather", "原木", 5, 2, LifeTaskState.Running)], LifeQueueState.Running, current: "a"),
            occupied: new("life", true)));
        Assert.False(model.Fishing.CanStart);
        Assert.Contains("采集 原木 × 5 个", model.Fishing.StartBlocked);

        // 其他功能占用活动锁时同样停用，并点名占用者。
        backend.PublishLife(Snapshot(gather: gather, tasks: pending, occupied: new("rooms-12", false)));
        Assert.False(model.Fishing.CanStart);
        Assert.Contains("合奏", model.Fishing.StartBlocked);
    }

    private static FishingRunView FishingRun(string phase = "waiting-bite", bool active = true, bool clockStarted = false, double? elapsed = null, int? target = 5400,
        string? stopReason = null, FishingPoseState? pose = null) =>
        new("run-1", "op-1", phase, active, clockStarted, elapsed, target, new(0, 0, 0, 0), 0, null, stopReason, pose, DateTimeOffset.UtcNow);
}
