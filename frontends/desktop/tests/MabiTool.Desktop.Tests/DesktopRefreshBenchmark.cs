using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Shell;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>
/// 桌面刷新开销的基准：替身后端按现场节奏推送战斗事件，量出按路由的请求数、每个事件引发的请求、界面线程上处理后端通知的
/// 耗时与分配、各页面模型的属性通知与集合变更次数，以及战斗视图的托管内存分项。默认跳过；设置
/// <c>MOONGATE_DESKTOP_BENCHMARK=&lt;标签&gt;</c> 时运行，结果写到 <c>artifacts/reports/desktop-refresh-benchmark-&lt;标签&gt;.json</c>。
/// 场景时长取 <c>MOONGATE_DESKTOP_BENCHMARK_SECONDS</c>（默认 30）。
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class DesktopRefreshBenchmark
{
    private static readonly string[] WidgetIds =
        ["combat.dps", "combat.skills", "combat.self-effects", "combat.target-effects", "combat.measurement", "combat.coverage"];

    [AvaloniaFact]
    public async Task MeasuresRefreshCostUnderRealisticCadence()
    {
        var label = Environment.GetEnvironmentVariable("MOONGATE_DESKTOP_BENCHMARK");
        Assert.SkipWhen(string.IsNullOrEmpty(label), "设置 MOONGATE_DESKTOP_BENCHMARK=<标签> 时运行");
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("MOONGATE_DESKTOP_BENCHMARK_SECONDS"), out var value) ? value : 30;
        var report = new Dictionary<string, object?>
        {
            ["label"] = label,
            ["measuredAt"] = DateTimeOffset.Now.ToString("O"),
            ["runtime"] = Environment.Version.ToString(),
            ["os"] = Environment.OSVersion.VersionString,
            ["processors"] = Environment.ProcessorCount,
            ["conditions"] = "Avalonia 无界面平台（不显示窗口），主窗口打开在 DPS检测页并订阅后端；另建 6 个战斗浮窗元件模型。" +
                "替身后端只实现战斗与常驻刷新接口，其余路由回 404。boss：每 200 ms 一个检查点（摘要每次、效果事件每次交替本机与当前目标），" +
                "第 10–13 秒停手（分段仍在进行、只有时长增长，与宿主一致不发摘要事件，每秒一条本机效果事件），场景中段一次分段结算，第 4 秒起一次 20 秒定时测量（与宿主一样只在阶段变化时推送测量事件，计时中的累计只改读取结果）；town：没有进行中的分段，每 10 秒一条本机效果事件；" +
                "town-hidden：同 town，主窗口最小化、浮窗元件不显示。每个场景先预热 3 秒再计数。界面线程耗时与分配只计后端通知送到界面线程后的处理" +
                "（含各订阅者重读、映射与属性通知触发的绑定更新），不含排版与绘制；进程分配已扣除替身后端序列化响应的分配。",
            ["seconds"] = seconds,
            ["scenarios"] = new[]
            {
                await RunAsync("boss", seconds, visible: true, Boss),
                await RunAsync("town", seconds, visible: true, Town),
                await RunAsync("town-hidden", seconds, visible: false, Town)
            },
            ["memory"] = await MemoryAsync()
        };
        var path = Path.Combine(RepositoryRoot(), "artifacts", "reports", $"desktop-refresh-benchmark-{label}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            TestContext.Current.CancellationToken);
    }

    private static async Task Boss(CombatStubHost stub, int seconds, CancellationToken token)
    {
        var ticks = seconds * 5;
        var clock = Stopwatch.StartNew();
        var measureStart = 20; var measureRun = 25; var measureEnd = Math.Min(125, ticks - 15);
        for (var tick = 0; tick < ticks; tick++)
        {
            var target = stub.CurrentTargetId!;
            // 第 10–13 秒停手：分段仍在进行，宿主不发摘要事件，只有本机效果每秒变化一次。
            if (tick is >= 50 and < 65)
            {
                stub.Pause(200);
                if (tick % 5 == 0) stub.Checkpoint(false, CombatStubHost.LocalId);
                await Until(clock, (tick + 1) * 200, token);
                continue;
            }
            string[] effects = tick % 5 == 0 ? [CombatStubHost.LocalId, target] : tick % 2 == 0 ? [CombatStubHost.LocalId] : [target];
            stub.Checkpoint(true, effects);
            if (tick == ticks * 2 / 3) stub.CloseEncounter();
            if (tick == measureStart) stub.Measure("waiting", "enc-m1");
            else if (tick == measureRun) stub.Measure("running", "enc-m1", 20_000);
            else if (tick > measureRun && tick < measureEnd && (tick - measureRun) % 5 == 0)
                stub.Measure("running", "enc-m1", 20_000 - (tick - measureRun) * 200, publish: false);
            else if (tick == measureEnd) stub.Measure("idle", "enc-m1", finish: true);
            await Until(clock, (tick + 1) * 200, token);
        }
    }

    private static async Task Town(CombatStubHost stub, int seconds, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        for (var second = 0; second < seconds; second++)
        {
            if (second % 10 == 5) stub.Checkpoint(false, CombatStubHost.LocalId);
            await Until(clock, (second + 1) * 1000, token);
        }
    }

    private static async Task Until(Stopwatch clock, long milliseconds, CancellationToken token)
    {
        var wait = milliseconds - clock.ElapsedMilliseconds;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), token);
    }

    private sealed class UiStats
    {
        public int Callbacks;
        public long Ticks, MaxTicks, Bytes;
        public void Reset() { Callbacks = 0; Ticks = MaxTicks = Bytes = 0; }
    }

    private sealed class NotifyCounter
    {
        public int Properties, WholeObject, Collections;
        public void Reset() { Properties = WholeObject = Collections = 0; }

        public void Watch(INotifyPropertyChanged model)
        {
            model.PropertyChanged += (_, e) =>
            {
                Properties++;
                if (string.IsNullOrEmpty(e.PropertyName)) WholeObject++;
            };
        }

        public void WatchCollections(object model)
        {
            foreach (var property in model.GetType().GetProperties())
                if (property.GetIndexParameters().Length == 0 && property.GetValue(model) is INotifyCollectionChanged list)
                    list.CollectionChanged += (_, _) => Collections++;
        }

        public Dictionary<string, int> Result(double seconds) => new()
        {
            ["propertyChanged"] = Properties, ["wholeObjectChanged"] = WholeObject, ["collectionChanged"] = Collections,
            ["propertyChangedPerSecond"] = (int)Math.Round(Properties / seconds)
        };
    }

    private static async Task<Dictionary<string, object?>> RunAsync(string name, int seconds, bool visible,
        Func<CombatStubHost, int, CancellationToken, Task> script)
    {
        var token = TestContext.Current.CancellationToken;
        var stub = new CombatStubHost();
        if (name.StartsWith("town", StringComparison.Ordinal)) stub.CloseEncounter();
        var ui = new UiStats();
        var changes = 0;
        void Dispatch(Action action) => Dispatcher.UIThread.Post(() =>
        {
            var start = Stopwatch.GetTimestamp();
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            action();
            var spent = Stopwatch.GetTimestamp() - start;
            ui.Callbacks++; ui.Ticks += spent; ui.MaxTicks = Math.Max(ui.MaxTicks, spent);
            ui.Bytes += GC.GetAllocatedBytesForCurrentThread() - bytes;
        });
        await using var backend = new LiveBackend(Path.Combine(Path.GetTempPath(), "moongate-missing-" + Guid.NewGuid().ToString("N"), "instance.json"), Dispatch)
        {
            Handler = () => stub,
            Bootstrap = _ => Task.FromResult(stub.Bootstrap)
        };
        backend.Changed += (_, _) => changes++;
        backend.Start();
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        var widgets = WidgetIds.Select(id => new OverlayWidgetModel(id, backend)).ToList();
        try
        {
            var wait = Stopwatch.StartNew();
            while (!backend.GetGameBinding().Confirmed || backend.GetCombat().History.Count == 0)
            {
                Assert.True(wait.Elapsed < TimeSpan.FromSeconds(20), "替身后端未连接");
                await Task.Delay(100, token);
            }
            var page = (CombatPageModel)window.GetVisualDescendants().OfType<CombatPage>().Single().DataContext!;
            var shell = (ShellViewModel)window.DataContext!;
            foreach (var widget in widgets) widget.SetShown(visible);
            if (!visible) window.WindowState = WindowState.Minimized;
            var counters = new Dictionary<string, NotifyCounter> { ["shell"] = new(), ["combatPage"] = new(), ["overlayWidgets"] = new() };
            counters["shell"].Watch(shell);
            counters["combatPage"].Watch(page);
            counters["combatPage"].WatchCollections(page);
            foreach (var widget in widgets) counters["overlayWidgets"].Watch(widget);

            await Task.Delay(3000, token);
            stub.ResetCounters(); ui.Reset(); changes = 0;
            foreach (var counter in counters.Values) counter.Reset();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var clock = Stopwatch.StartNew();
            await script(stub, seconds, token);
            await Task.Delay(500, token);
            var elapsed = clock.Elapsed.TotalSeconds;
            var processBytes = GC.GetTotalAllocatedBytes(true) - allocated - stub.StubAllocatedBytes;
            var events = stub.Events.Values.Sum();
            return new()
            {
                ["name"] = name,
                ["elapsedSeconds"] = Math.Round(elapsed, 2),
                ["events"] = stub.Events.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
                ["requestsTotal"] = stub.TotalRequests,
                ["requestsPerSecond"] = Math.Round(stub.TotalRequests / elapsed, 2),
                ["requestsPerEvent"] = events == 0 ? null : Math.Round((double)stub.TotalRequests / events, 2),
                ["requestsByRoute"] = stub.Requests.OrderByDescending(item => item.Value).ToDictionary(item => item.Key, item => item.Value),
                ["changedNotifications"] = changes,
                ["uiCallbacks"] = ui.Callbacks,
                ["uiThreadMs"] = Math.Round(ui.Ticks * 1000.0 / Stopwatch.Frequency, 1),
                ["uiThreadMsPerSecond"] = Math.Round(ui.Ticks * 1000.0 / Stopwatch.Frequency / elapsed, 2),
                ["uiThreadMaxCallbackMs"] = Math.Round(ui.MaxTicks * 1000.0 / Stopwatch.Frequency, 2),
                ["uiThreadAllocatedBytes"] = ui.Bytes,
                ["processAllocatedBytesExcludingStub"] = processBytes,
                ["processAllocatedBytesPerSecond"] = (long)(processBytes / elapsed),
                ["notifications"] = counters.ToDictionary(item => item.Key, item => item.Value.Result(elapsed))
            };
        }
        finally
        {
            foreach (var widget in widgets) widget.Dispose();
            window.Close();
        }
    }

    /// <summary>战斗视图的托管堆分项：每一步前后各做一次完整回收，取 <see cref="GC.GetTotalMemory"/> 之差。</summary>
    private static async Task<Dictionary<string, object?>> MemoryAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var stub = new CombatStubHost();
        for (var i = 0; i < 50; i++) stub.Checkpoint(true, CombatStubHost.LocalId);
        await using var backend = new LiveBackend(Path.Combine(Path.GetTempPath(), "moongate-missing-" + Guid.NewGuid().ToString("N"), "instance.json"),
            action => Dispatcher.UIThread.Post(action))
        {
            Handler = () => stub,
            Bootstrap = _ => Task.FromResult(stub.Bootstrap)
        };
        static long Heap()
        {
            for (var i = 0; i < 2; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            return GC.GetTotalMemory(true);
        }
        async Task Settle(int milliseconds = 2500) => await Task.Delay(milliseconds, token);
        var result = new Dictionary<string, object?>();
        var before = Heap();
        backend.Start();
        await Settle();
        result["backendStateWith25RecordsBytes"] = Heap() - before;

        before = Heap();
        var window = LayoutProbe.Open(backend, 1280, 860, "combat-observation");
        await Settle(1000);
        result["mainWindowWithCombatPageBytes"] = Heap() - before;

        before = Heap();
        LayoutProbe.Select(window, "shell.settings");
        await Settle(1000);
        result["combatPageCachedAfterLeavingBytes"] = Heap() - before;
        LayoutProbe.Select(window, "combat-observation");

        // 明细窗口与记录明细窗口各开关三次取平均：单次差值受 Avalonia 内部缓存影响，波动约 ±0.2 MB。
        long detailOpen = 0, detailReleased = 0, recordOpen = 0, recordReleased = 0;
        for (var round = 0; round < 3; round++)
        {
            before = Heap();
            var detail = new CombatPageModel(backend, page: false, detail: true);
            backend.SetCombatDetailOpen(true);
            await Settle();
            var opened = Heap();
            detailOpen += opened - before;
            backend.SetCombatDetailOpen(false);
            detail.Dispose();
            detail = null;
            await Settle(500);
            detailReleased += opened - Heap();

            before = Heap();
            var record = new CombatPageModel(backend, page: false, record: "enc-h0");
            backend.SetCombatRecordOpen("enc-h0", true);
            await Settle();
            opened = Heap();
            recordOpen += opened - before;
            backend.SetCombatRecordOpen("enc-h0", false);
            record.Dispose();
            record = null;
            await Settle(500);
            recordReleased += opened - Heap();
        }
        result["detailWindowOpenBytes"] = detailOpen / 3;
        result["detailWindowClosedReleasedBytes"] = detailReleased / 3;
        result["recordDetailOpenBytes"] = recordOpen / 3;
        result["recordDetailClosedReleasedBytes"] = recordReleased / 3;
        window.Close();
        result["note"] = "正值为该步之后托管堆的增量；Released 项为关闭后回收的字节数。字体与素材在应用启动时加载，测试进程中与其他用例共用，未单独分项。";
        return result;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
