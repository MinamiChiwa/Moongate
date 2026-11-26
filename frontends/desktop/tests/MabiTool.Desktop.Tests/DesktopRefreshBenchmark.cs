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
        ["combat.dps", "combat.skills", "combat.self-effects", "combat.target-effects", "combat.measurement"];

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

    /// <summary>
    /// 战斗数据从后端事件到界面显示的延迟：替身后端按命中节奏推送唤醒事件（桌面订阅了 combat.live.changed 时推送它，否则推送摘要事件），
    /// 每次推送记下时刻；界面线程处理完后端通知后，凡修订不新于界面已显示的工作区修订的推送都记为已显示。默认跳过；设置
    /// <c>MOONGATE_DESKTOP_LATENCY=&lt;标签&gt;</c> 时运行，结果写到 <c>artifacts/reports/desktop-display-latency-&lt;标签&gt;.json</c>。
    /// </summary>
    [AvaloniaFact]
    public async Task MeasuresDisplayLatency()
    {
        var label = Environment.GetEnvironmentVariable("MOONGATE_DESKTOP_LATENCY");
        Assert.SkipWhen(string.IsNullOrEmpty(label), "设置 MOONGATE_DESKTOP_LATENCY=<标签> 时运行");
        var live = typeof(LiveBackend).GetField("DesktopEvents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null)
            is MabiTool.Contracts.EventStreamOptions options && options.Topics.Contains("combat.live.changed");
        var report = new Dictionary<string, object?>
        {
            ["label"] = label,
            ["measuredAt"] = DateTimeOffset.Now.ToString("O"),
            ["runtime"] = Environment.Version.ToString(),
            ["os"] = Environment.OSVersion.VersionString,
            ["processors"] = Environment.ProcessorCount,
            ["wakeEvent"] = live ? "combat.live.changed" : "combat.summary.changed",
            ["conditions"] = "Avalonia 无界面平台（不显示窗口），主窗口打开在 DPS检测页并订阅后端，另建 6 个战斗浮窗元件模型；替身后端每次命中工作区修订加一并推送唤醒事件。" +
                "dense：每 10–40 ms 一次命中（平均约 40 次/秒），20 秒；sparse：每 500 ms 一次，10 秒；overlay：主窗口最小化、只显示浮窗元件，dense 节奏 10 秒；" +
                "idle：停手 10 秒（只计请求与通知）。延迟为推送到界面线程处理完后端通知、该修订已显示的时刻。",
            ["scenarios"] = new[]
            {
                await LatencyAsync("dense", live, 20, visible: true, overlay: true, Dense),
                await LatencyAsync("sparse", live, 10, visible: true, overlay: true, Sparse),
                await LatencyAsync("overlay", live, 10, visible: false, overlay: true, Dense),
                await LatencyAsync("idle", live, 10, visible: true, overlay: true, (_, _, _, _) => Task.CompletedTask)
            }
        };
        var path = Path.Combine(RepositoryRoot(), "artifacts", "reports", $"desktop-display-latency-{label}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 真实宿主下的抓包到界面显示：连接 <c>BackendRuntimeProbe --live-replay … --serve 连接文件</c> 起的进程内宿主（实采样本按本机单调时钟回放，
    /// 抓包时点即本机 QPC），主窗口开在 DPS检测页并显示 6 个战斗浮窗元件，记下 LiveBackend 的每个显示延迟样本（显示时刻 − 抓包时点）、
    /// 相邻两次显示新数据的间隔、界面线程处理通知的耗时与按路由的请求数。默认跳过；设置 <c>MOONGATE_DESKTOP_LIVE_HOST=&lt;连接文件&gt;</c>
    /// 时运行（<c>MOONGATE_DESKTOP_LIVE_OVERLAY_ONLY=1</c> 时不打开 DPS检测页、主窗口最小化，只显示浮窗元件），结果写到
    /// <c>artifacts/reports/desktop-live-host-&lt;MOONGATE_DESKTOP_LATENCY&gt;.json</c>；宿主回放结束（删除连接文件）后返回。
    /// </summary>
    [AvaloniaFact]
    public async Task MeasuresLiveHostLatency()
    {
        var connection = Environment.GetEnvironmentVariable("MOONGATE_DESKTOP_LIVE_HOST");
        Assert.SkipWhen(string.IsNullOrEmpty(connection), "设置 MOONGATE_DESKTOP_LIVE_HOST=<连接文件> 时运行");
        var label = Environment.GetEnvironmentVariable("MOONGATE_DESKTOP_LATENCY") ?? "live";
        var overlayOnly = Environment.GetEnvironmentVariable("MOONGATE_DESKTOP_LIVE_OVERLAY_ONLY") == "1";
        var switching = !overlayOnly && Environment.GetEnvironmentVariable("MOONGATE_DESKTOP_LIVE_SWITCH") == "1";
        var switches = new List<(string Step, double Ms, bool Done)>();
        // 选了目标或整场、页面已采用结果却没有范围结果的时段（采样间隔 50 ms）：一直不更新的表现。
        double heldMs = 0, missingMs = 0, longestMissingMs = 0, currentMissingMs = 0;
        var fighting = false;
        var missingProblems = new Dictionary<string, int>(StringComparer.Ordinal);
        var token = TestContext.Current.CancellationToken;
        var wait = Stopwatch.StartNew();
        while (!File.Exists(connection))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(120), "宿主未写出连接文件");
            await Task.Delay(200, token);
        }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(connection, token));
        var address = document.RootElement.GetProperty("address").GetString()!.TrimEnd('/') + "/";
        var secret = document.RootElement.GetProperty("secret").GetString()!;
        var routes = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, double Ms)>(StringComparer.Ordinal);
        var samples = new List<(double At, double Latency)>();
        var clock = Stopwatch.StartNew();
        var ui = new UiStats();
        var changes = 0;
        void Dispatch(Action action) => Dispatcher.UIThread.Post(() =>
        {
            var start = Stopwatch.GetTimestamp();
            action();
            var spent = Stopwatch.GetTimestamp() - start;
            ui.Callbacks++; ui.Ticks += spent; ui.MaxTicks = Math.Max(ui.MaxTicks, spent);
        });
        await using var backend = new LiveBackend(Path.Combine(Path.GetTempPath(), "moongate-missing-" + Guid.NewGuid().ToString("N"), "instance.json"), Dispatch)
        {
            Handler = () => new RouteCounter(routes),
            Bootstrap = _ => Task.FromResult(new MabiTool.Contracts.BootstrapResponse(1, new string('a', 32), address,
                new(secret, new MabiTool.Contracts.AuthSession("owner", "moongate-desktop", [], null, null, true)))),
            LatencyObserved = latency => { lock (samples) samples.Add((clock.Elapsed.TotalMilliseconds, latency)); }
        };
        backend.Changed += (_, _) => changes++;
        backend.Start();
        // 只看浮窗时 DPS检测页从未打开（主窗口停在首页并最小化），与用户实机一致。
        var window = LayoutProbe.Open(backend, 1280, 860, overlayOnly ? null : "combat-observation");
        var widgets = WidgetIds.Select(id => new OverlayWidgetModel(id, backend)).ToList();
        try
        {
            wait.Restart();
            while (!backend.GetGameBinding().Confirmed)
            {
                Assert.True(wait.Elapsed < TimeSpan.FromSeconds(30), "宿主未连接");
                await Task.Delay(100, token);
            }
            foreach (var widget in widgets) widget.SetShown(true);
            if (overlayOnly) window.WindowState = WindowState.Minimized;
            // DPS 浮窗上用户看到的数字：大字 DPS 与其余主数据（总伤害、DPS时长、命中次数）的文字，每次变化记下时刻。
            var dps = widgets.Single(widget => widget.WidgetId == "combat.dps");
            string? shownDps = null, shownKpis = null;
            var dpsChanges = new List<double>(); var kpiChanges = new List<double>();
            dps.PropertyChanged += (_, _) =>
            {
                var primary = dps.PrimaryDps; var others = string.Join('|', dps.OtherKpis.Select(tile => tile.Value));
                if (primary != shownDps) { shownDps = primary; dpsChanges.Add(clock.Elapsed.TotalMilliseconds); }
                if (others != shownKpis) { shownKpis = others; kpiChanges.Add(clock.Elapsed.TotalMilliseconds); }
            };
            await Task.Delay(1000, token);
            routes.Clear(); ui.Reset(); changes = 0;
            lock (samples) samples.Clear();
            await File.WriteAllTextAsync(connection + ".ready", "ready", token);
            clock.Restart(); dpsChanges.Clear(); kpiChanges.Clear();
            wait.Restart();
            // 切换范围与目标（MOONGATE_DESKTOP_LIVE_SWITCH=1）：按页面命令发出选择，记下到页面采用新选择结果（不再等待、选中与所选一致）的时间，
            // 8 秒仍未采用记为未更新。分段开始后才切换；每次切换之间隔 500 ms。
            var page = switching ? (CombatPageModel)window.GetVisualDescendants().OfType<CombatPage>().Single().DataContext! : null;
            var step = 0;
            while (File.Exists(connection))
            {
                Assert.True(wait.Elapsed < TimeSpan.FromMinutes(12), "宿主回放未结束");
                if (page is null || page.Data.RangeTargets is not { Count: > 1 } && page.Data.Scope is null) { await Task.Delay(250, token); continue; }
                var targets = (page.Data.RangeTargets ?? []).Where(chip => !chip.IsAll).Select(chip => chip.Id).ToArray();
                (string Name, string Intent, string? Argument) next = (step++ % 7) switch
                {
                    0 => ("scope-observation", "combat.scope", "observation"),
                    1 => ("target", "combat.target", targets.FirstOrDefault()),
                    2 => ("target", "combat.target", targets.Skip(1).FirstOrDefault() ?? targets.FirstOrDefault()),
                    3 => ("target-all", "combat.target", null),
                    4 => ("scope-segment", "combat.scope", "segment"),
                    5 => ("target", "combat.target", targets.FirstOrDefault()),
                    _ => ("target-all", "combat.target", null)
                };
                if (next.Name == "target" && next.Argument is null) continue;
                var scopeWanted = next.Intent == "combat.scope" ? next.Argument : page.Data.SelectedScope;
                var targetWanted = next.Intent == "combat.target" ? next.Argument : null;
                var sent = Stopwatch.StartNew();
                await backend.SendIntentAsync(next.Intent, next.Argument, token);
                var done = false;
                while (sent.Elapsed < TimeSpan.FromSeconds(8) && File.Exists(connection))
                {
                    if (!page.Data.Pending && page.Data.SelectedScope == scopeWanted && page.Data.SelectedTarget == targetWanted) { done = true; break; }
                    await Task.Delay(5, token);
                }
                if (!File.Exists(connection) && !done) break;
                switches.Add((next.Name, sent.Elapsed.TotalMilliseconds, done));
                // 选中目标时停留 3 秒（跨过分段更替），其余 1 秒；期间按 50 ms 采样页面是否有范围结果。
                var hold = Stopwatch.StartNew(); var holdMs = next.Name == "target" ? 3000 : 1000;
                while (hold.ElapsedMilliseconds < holdMs && File.Exists(connection))
                {
                    await Task.Delay(50, token);
                    // 第一次出现范围目标（这次观察已有分段）之后才计：此前整场本来没有结果。
                    fighting |= page.Data.RangeTargets is { Count: > 1 };
                    var needsRange = fighting && (page.Data.SelectedTarget is not null || page.Data.SelectedScope == "observation");
                    if (!needsRange || page.Data.Pending) { currentMissingMs = 0; continue; }
                    heldMs += 50;
                    var (hasRange, problem, _) = backend.ShownRange;
                    if (hasRange) { currentMissingMs = 0; continue; }
                    missingMs += 50; currentMissingMs += 50; longestMissingMs = Math.Max(longestMissingMs, currentMissingMs);
                    var cause = $"{page.Data.SelectedScope}|{(page.Data.SelectedTarget is null ? "all" : "target")}|{problem ?? "none"}"; missingProblems[cause] = missingProblems.GetValueOrDefault(cause) + 1;
                }
            }
            var elapsed = clock.Elapsed.TotalSeconds;
            (double At, double Latency)[] values;
            lock (samples) values = [.. samples];
            var latencies = values.Select(item => item.Latency).Order().ToArray();
            var gaps = values.Zip(values.Skip(1), (a, b) => b.At - a.At).Order().ToArray();
            double At(double[] sorted, double q) => sorted.Length == 0 ? double.NaN : Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * (sorted.Length - 1)))], 1);
            Dictionary<string, double>? Distribution(double[] sorted) => sorted.Length == 0 ? null : new()
                { ["p50"] = At(sorted, 0.5), ["p95"] = At(sorted, 0.95), ["p99"] = At(sorted, 0.99), ["max"] = Math.Round(sorted[^1], 1) };
            // 有显示样本的时段（首个到最后一个样本）内每秒显示新数据的次数；按秒的序列给出每秒的样本数与该秒延迟中位数。
            var active = values.Length < 2 ? 0 : (values[^1].At - values[0].At) / 1000;
            var perSecond = values.GroupBy(item => (int)(item.At / 1000)).OrderBy(group => group.Key).Select(group =>
            {
                var sorted = group.Select(item => item.Latency).Order().ToArray();
                return new Dictionary<string, double> { ["second"] = group.Key, ["updates"] = sorted.Length, ["p50"] = At(sorted, 0.5), ["max"] = Math.Round(sorted[^1], 1) };
            }).ToArray();
            var report = new Dictionary<string, object?>
            {
                ["label"] = label,
                ["measuredAt"] = DateTimeOffset.Now.ToString("O"),
                ["runtime"] = Environment.Version.ToString(),
                ["os"] = Environment.OSVersion.VersionString,
                ["processors"] = Environment.ProcessorCount,
                ["conditions"] = "Avalonia 无界面平台（不显示窗口），" + (overlayOnly ? "DPS检测页未打开、主窗口最小化，只显示 6 个战斗浮窗元件" : "主窗口打开在 DPS检测页，另显示 6 个战斗浮窗元件") +
                    "；后端为 BackendRuntimeProbe --live-replay --serve 起的进程内宿主（真实 SQLite、SSE），实采样本按本机单调时钟回放。" +
                    "延迟为 LiveBackend 记下的显示延迟样本（界面线程处理完通知的时刻 − 所显示数据中最新一条的抓包时点），不含 Worker 抓包与写出。",
                ["elapsedSeconds"] = Math.Round(elapsed, 2),
                ["displaySamples"] = latencies.Length,
                ["activeSeconds"] = Math.Round(active, 1),
                ["updatesPerSecond"] = active <= 0 ? null : Math.Round(values.Length / active, 1),
                ["captureToDisplayMs"] = Distribution(latencies),
                // 浮窗大字 DPS、其余主数据文字在有显示样本的时段内每秒变化的次数与相邻两次变化的间隔。
                ["overlayDpsTextChangesPerSecond"] = active <= 0 ? null : Math.Round(dpsChanges.Count(at => at >= values[0].At && at <= values[^1].At) / active, 1),
                ["overlayDpsTextIntervalMs"] = Distribution(dpsChanges.Zip(dpsChanges.Skip(1), (a, b) => b - a).Order().ToArray()),
                ["overlayKpiTextChangesPerSecond"] = active <= 0 ? null : Math.Round(kpiChanges.Count(at => at >= values[0].At && at <= values[^1].At) / active, 1),
                ["updateIntervalMs"] = Distribution(gaps),
                ["changedNotificationsPerSecond"] = Math.Round(changes / elapsed, 1),
                ["uiThreadMsPerSecond"] = Math.Round(ui.Ticks * 1000.0 / Stopwatch.Frequency / elapsed, 2),
                ["uiThreadMaxCallbackMs"] = Math.Round(ui.MaxTicks * 1000.0 / Stopwatch.Frequency, 2),
                ["requestsPerSecond"] = Math.Round(routes.Values.Sum(item => item.Count) / elapsed, 1),
                ["requestsByRoute"] = routes.OrderByDescending(item => item.Value.Count).ToDictionary(item => item.Key,
                    item => new Dictionary<string, double> { ["count"] = item.Value.Count, ["perSecond"] = Math.Round(item.Value.Count / elapsed, 1), ["averageMs"] = Math.Round(item.Value.Ms / Math.Max(1, item.Value.Count), 2) }),
                ["perSecond"] = perSecond,
                ["switches"] = switches.Count == 0 ? null : switches.GroupBy(item => item.Step).ToDictionary(group => group.Key, group =>
                {
                    var adopted = group.Where(item => item.Done).Select(item => item.Ms).Order().ToArray();
                    return (object)new Dictionary<string, object?> { ["count"] = group.Count(), ["notAdoptedWithin8s"] = group.Count(item => !item.Done),
                        ["toAdoptedMs"] = Distribution(adopted) };
                }),
                ["rangeHeldSeconds"] = Math.Round(heldMs / 1000, 1),
                ["rangeMissingSeconds"] = Math.Round(missingMs / 1000, 1),
                ["rangeLongestMissingMs"] = longestMissingMs,
                ["rangeMissingProblems"] = missingProblems,
                ["switchSequence"] = switches.Select(item => $"{item.Step}:{Math.Round(item.Ms)}{(item.Done ? "" : "!")}").ToArray()
            };
            var path = Path.Combine(RepositoryRoot(), "artifacts", "reports", $"desktop-live-host-{label}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), token);
        }
        finally
        {
            foreach (var widget in widgets) widget.Dispose();
            window.Close();
        }
    }

    /// <summary>按“方法 路由”（ID 段换成 {id}）累计请求次数与耗时（到响应头）。</summary>
    private sealed class RouteCounter(System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, double Ms)> routes) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var key = request.Method.Method + " " + string.Join('/', path.Split('/').Select(part => part.Length > 0 && (char.IsDigit(part[0]) || part.Contains('-') && part.Length > 20) ? "{id}" : part));
            var start = Stopwatch.GetTimestamp();
            var response = await base.SendAsync(request, cancellationToken);
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (!path.EndsWith("/events", StringComparison.Ordinal)) routes.AddOrUpdate(key, (1, ms), (_, item) => (item.Count + 1, item.Ms + ms));
            return response;
        }
    }

    private sealed class Pushes
    {
        public readonly Lock Gate = new();
        public readonly Queue<(ulong Revision, long At)> Pending = new();
        public readonly List<double> Latencies = [];
        public void Add(ulong revision) { lock (Gate) Pending.Enqueue((revision, Stopwatch.GetTimestamp())); }
        public void Shown(ulong? revision)
        {
            if (revision is not { } shown) return;
            var now = Stopwatch.GetTimestamp();
            lock (Gate)
                while (Pending.TryPeek(out var item) && item.Revision <= shown)
                { Pending.Dequeue(); Latencies.Add(Stopwatch.GetElapsedTime(item.At, now).TotalMilliseconds); }
        }
    }

    private static async Task Dense(CombatStubHost stub, bool live, Pushes pushes, CancellationToken token)
    {
        var clock = Stopwatch.StartNew(); var random = new Random(7); var at = 0.0;
        while (at < 20_000 && !token.IsCancellationRequested)
        {
            at += 10 + random.NextDouble() * 30;
            await Until(clock, (long)at, token);
            pushes.Add(stub.Hit(live));
        }
    }

    private static async Task Sparse(CombatStubHost stub, bool live, Pushes pushes, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        for (var i = 1; i <= 20 && !token.IsCancellationRequested; i++)
        {
            await Until(clock, i * 500, token);
            pushes.Add(stub.Hit(live));
        }
    }

    private static async Task<Dictionary<string, object?>> LatencyAsync(string name, bool live, int seconds, bool visible, bool overlay,
        Func<CombatStubHost, bool, Pushes, CancellationToken, Task> script)
    {
        var token = TestContext.Current.CancellationToken;
        var stub = new CombatStubHost();
        var ui = new UiStats();
        var pushes = new Pushes();
        var changes = 0;
        LiveBackend? owner = null;
        void Dispatch(Action action) => Dispatcher.UIThread.Post(() =>
        {
            var start = Stopwatch.GetTimestamp();
            action();
            var spent = Stopwatch.GetTimestamp() - start;
            ui.Callbacks++; ui.Ticks += spent; ui.MaxTicks = Math.Max(ui.MaxTicks, spent);
            if (owner is not null) pushes.Shown(owner.ShownWorkspaceRevision);
        });
        await using var backend = new LiveBackend(Path.Combine(Path.GetTempPath(), "moongate-missing-" + Guid.NewGuid().ToString("N"), "instance.json"), Dispatch)
        {
            Handler = () => stub,
            Bootstrap = _ => Task.FromResult(stub.Bootstrap)
        };
        owner = backend;
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
            foreach (var widget in widgets) widget.SetShown(overlay);
            if (!visible) window.WindowState = WindowState.Minimized;
            await Task.Delay(3000, token);
            stub.ResetCounters(); ui.Reset(); changes = 0;
            var clock = Stopwatch.StartNew();
            if (name == "idle") await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            else await script(stub, live, pushes, token);
            await Task.Delay(1000, token);
            var elapsed = clock.Elapsed.TotalSeconds;
            List<double> values;
            int unshown;
            lock (pushes.Gate) { values = [.. pushes.Latencies]; unshown = pushes.Pending.Count; }
            var sorted = values.Order().ToArray();
            double At(double q) => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * (sorted.Length - 1)))];
            return new()
            {
                ["name"] = name,
                ["elapsedSeconds"] = Math.Round(elapsed, 2),
                ["pushes"] = values.Count + unshown,
                ["notShownAtEnd"] = unshown,
                ["eventToDisplayMs"] = sorted.Length == 0 ? null : new Dictionary<string, double>
                    { ["p50"] = Math.Round(At(0.5), 1), ["p95"] = Math.Round(At(0.95), 1), ["p99"] = Math.Round(At(0.99), 1), ["max"] = Math.Round(sorted[^1], 1) },
                ["snapshotReads"] = stub.Count("GET /api/v1/combat/snapshot"),
                ["snapshotReadsPerSecond"] = Math.Round(stub.Count("GET /api/v1/combat/snapshot") / elapsed, 1),
                ["requestsPerSecond"] = Math.Round(stub.TotalRequests / elapsed, 1),
                ["changedNotificationsPerSecond"] = Math.Round(changes / elapsed, 1),
                ["uiThreadMsPerSecond"] = Math.Round(ui.Ticks * 1000.0 / Stopwatch.Frequency / elapsed, 2),
                ["uiThreadMaxCallbackMs"] = Math.Round(ui.MaxTicks * 1000.0 / Stopwatch.Frequency, 2)
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
