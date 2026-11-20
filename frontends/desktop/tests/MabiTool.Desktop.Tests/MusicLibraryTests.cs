using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Pages;
using MabiTool.Desktop.Pages.Combat;
using MabiTool.Desktop.Pages.Diagnostics;
using MabiTool.Desktop.Pages.Dyeing;
using MabiTool.Desktop.Pages.Inventory;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Pages.Music;
using MabiTool.Desktop.Pages.Overlay;
using MabiTool.Desktop.Pages.Rooms;
using MabiTool.Desktop.Pages.Settings;
using MabiTool.Desktop.Shell;
using MabiTool.Desktop.Theme;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>乐谱库：曲库与队列、“游戏内演奏”层与能力停用、迷你窗、乐谱表分页与时长查询。</summary>
public sealed class MusicLibraryTests
{
    [AvaloniaFact]
    public void MusicPageEnqueuesSearchesAndBlocksUnselectableScores()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "music-library");
        var page = window.GetVisualDescendants().OfType<MusicPage>().Single();
        var model = (MusicPageModel)page.DataContext!;
        Assert.Equal(4, model.Data.Queue.Count);

        Button Enqueue(string id) => page.GetVisualDescendants().OfType<Button>()
            .Single(button => button.CommandParameter as string == "music.enqueue:" + id);
        Click(window, Enqueue("s7"));
        Assert.Equal(5, model.Data.Queue.Count);
        Assert.Equal("艾菈的搖籃曲", model.Data.Queue[^1].Title);

        // 名称结尾有空白的乐谱游戏无法选取，不能加入队列。
        Assert.False(Enqueue("s8").IsEnabled);

        model.Search = "杜巴頓";
        LayoutProbe.Settle();
        Assert.Equal(["s5", "s6"], model.Data.Scores.Select(score => score.Id));
        Assert.Equal(["同名第 1 张", "同名第 2 张"], model.Data.Scores.Select(score => score.Tag));
        Assert.Contains(("music.search", "杜巴頓"), backend.UiIntents);

        model.Search = string.Empty;
        LayoutProbe.Settle();
        var campfire = page.GetVisualDescendants().OfType<Button>()
            .Single(button => button.CommandParameter as string == "music.playlist:campfire");
        Click(window, campfire);
        Assert.Equal(["s1", "s2", "s3"], model.Data.Scores.Select(score => score.Id));
        window.Close();
    }

    private static string PlayNotice(UiLanguage language) =>
        language == UiLanguage.TraditionalChinese ? "台服暫不支援遊戲內選曲演奏" : "台服暂不支持游戏内选曲演奏";

    private static string? Unavailable(string id) => id == CapabilityIds.PerformancePlay
        ? CapabilityNotice.Describe(new(id, "performance", CapabilityStatus.Unavailable, null, null, null, null,
            "module.notImplemented", DateTimeOffset.UnixEpoch, []))
        : null;

    private static int Intents(SampleBackend backend, string name) => backend.UiIntents.Count(intent => intent.Item1 == name);

    private static void PressKey(Window window, Key key)
    {
        var physical = key == Key.Escape ? PhysicalKey.Escape : PhysicalKey.Tab;
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        LayoutProbe.Settle();
    }

    private static void ClickOutside(Window window)
    {
        // 页面左下方：在层的面板之外，也不在任何按钮上。
        window.MouseDown(new Point(600, 400), MouseButton.Left);
        window.MouseUp(new Point(600, 400), MouseButton.Left);
        LayoutProbe.Settle();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MusicMainPageCarriesNoGameOperations(bool unavailable)
    {
        var backend = unavailable ? new SampleBackend { CapabilityNotices = Unavailable } : new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "music-library");
        try
        {
            var page = window.GetVisualDescendants().OfType<MusicPage>().Single();
            // 开始演奏、下一首、停止与能力说明都不在主页面上；层关闭时它们不可见。
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<Button>(),
                button => button.IsEffectivelyVisible && button.CommandParameter as string is "music.start" or "music.next" or "music.stop");
            Assert.False(Named<Panel>(page, "GameLayer").IsEffectivelyVisible);
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible
                && ((text.Text ?? string.Empty).Contains(PlayNotice(UiLanguage.SimplifiedChinese)) || text.Name == "CapabilityNotice"));
            // 本地曲库与队列的操作仍在主页面：搜索、加入、移除。
            Assert.True(Named<TextBox>(page, "SearchBox").IsEffectivelyVisible);
            Assert.Contains(page.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled
                && (button.CommandParameter as string)?.StartsWith("music.enqueue:") == true);
            Assert.Contains(page.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled
                && (button.CommandParameter as string)?.StartsWith("music.dequeue:") == true);
            Assert.True(Named<Button>(page, "GameLayerButton").IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void QueuePanelKeepsOnlyQueueContentAndSettingsLiveInTheLayer()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "music-library");
        try
        {
            var page = window.GetVisualDescendants().OfType<MusicPage>().Single();
            var model = (MusicPageModel)page.DataContext!;
            // 队列行只有乐器名或“未指定”，没有游戏内换装之类的说明。
            Assert.All(model.Data.Queue, row => Assert.DoesNotContain("换装", row.Note));
            Assert.All(model.Data.Queue, row => Assert.DoesNotContain("沿用", row.Note));
            // 主页面的队列面板没有播放顺序、曲间等待等设置。
            Assert.NotEmpty(model.Data.QueueSettings);
            var queue = Named<Card>(page, "QueuePanel");
            Assert.DoesNotContain(queue.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text is "播放顺序" or "曲间等待" or "检测到合奏时");
            // 打开层后设置显示在层里。
            Click(window, Named<Button>(page, "GameLayerButton"));
            var box = Named<Border>(page, "QueueSettingsBox");
            Assert.True(box.IsEffectivelyVisible);
            var texts = box.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
            Assert.Contains("播放顺序", texts);
            Assert.Contains("曲间等待", texts);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese)]
    [InlineData(UiLanguage.TraditionalChinese)]
    public async Task GameLayerExplainsAndDisablesOperationsUntilTheCapabilityReturns(UiLanguage language)
    {
        var backend = new SampleBackend { CapabilityNotices = Unavailable };
        var window = LayoutProbe.Open(backend, 1280, 860, "music-library", language);
        try
        {
            var page = window.GetVisualDescendants().OfType<MusicPage>().Single();
            var model = (MusicPageModel)page.DataContext!;
            Click(window, Named<Button>(page, "GameLayerButton"));
            Assert.True(model.IsGameOpen);
            Assert.True(Named<Panel>(page, "GameLayer").IsEffectivelyVisible);
            var notice = Named<TextBlock>(page, "CapabilityNotice");
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Equal(PlayNotice(language), notice.Text);
            var start = Named<Button>(page, "StartButton");
            var next = Named<Button>(page, "NextButton");
            var stop = Named<Button>(page, "StopButton");

            // 演奏中：下一首与停止由页面数据允许，能力不可用时仍停用，点按不发出意图。
            Assert.True(model.Data.Actions.CanNext && model.Data.Actions.CanStop);
            Assert.All(new[] { start, next, stop }, button => Assert.False(button.IsEffectivelyEnabled));
            Click(window, next);
            Click(window, stop);
            Assert.Equal(0, Intents(backend, "music.next") + Intents(backend, "music.stop"));
            Assert.Empty(LayoutProbe.FindTextOverflow(window));

            // 停止后开始演奏由页面数据允许，同样停用。
            await backend.SendIntentAsync("music.stop", null, TestContext.Current.CancellationToken);
            LayoutProbe.Settle();
            Assert.True(model.Data.Actions.CanStart);
            Assert.False(start.IsEffectivelyEnabled);
            Click(window, start);
            Assert.Equal(0, Intents(backend, "music.start"));

            // 四项功能的状态行跟随 performance.play：不可用。
            var rows = Named<ItemsControl>(page, "GameFeatureList").GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToList();
            Assert.Equal(4, rows.Count(text => text == "不可用"));
            Assert.Equal(language == UiLanguage.TraditionalChinese
                ? new[] { "遊戲內選曲演奏", "自動連續播放", "演奏前自動換樂器", "從遊戲讀取樂譜內容" }
                : ["游戏内选曲演奏", "自动连续播放", "演奏前自动换乐器", "从游戏读取乐谱内容"],
                rows.Where(text => text != "不可用").ToArray());

            // 能力恢复：说明消失，按钮按页面数据放开，点按发出意图。
            backend.CapabilityNotices = null;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.False(notice.IsEffectivelyVisible);
            Assert.True(start.IsEffectivelyEnabled);
            Click(window, start);
            Assert.Equal(1, Intents(backend, "music.start"));
            Assert.Equal(model.Data.Actions.CanNext, next.IsEffectivelyEnabled);
            Assert.True(stop.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void GameLayerStatesFollowDegradedUnknownAndAvailableCapabilities()
    {
        foreach (var (level, text) in new[] { (CapabilityLevel.Degraded, "降级可用"), (CapabilityLevel.Unknown, "未知"), (CapabilityLevel.Available, "可用") })
        {
            var backend = new SampleBackend { CapabilityLevels = _ => level };
            var window = LayoutProbe.Open(backend, 1280, 860, "music-library");
            try
            {
                var page = window.GetVisualDescendants().OfType<MusicPage>().Single();
                var model = (MusicPageModel)page.DataContext!;
                Click(window, Named<Button>(page, "GameLayerButton"));
                // 没有说明，按钮完全取自页面数据。
                Assert.False(model.HasCapabilityNotice);
                Assert.False(Named<TextBlock>(page, "CapabilityNotice").IsEffectivelyVisible);
                Assert.Equal(model.Data.Actions.CanStart, Named<Button>(page, "StartButton").IsEffectivelyEnabled);
                Assert.Equal(model.Data.Actions.CanNext, Named<Button>(page, "NextButton").IsEffectivelyEnabled);
                Assert.Equal(model.Data.Actions.CanStop, Named<Button>(page, "StopButton").IsEffectivelyEnabled);
                var statuses = Named<ItemsControl>(page, "GameFeatureList").GetVisualDescendants().OfType<TextBlock>()
                    .Where(row => row.IsEffectivelyVisible && row.FontWeight == Avalonia.Media.FontWeight.Bold).Select(row => row.Text).ToList();
                Assert.Equal(Enumerable.Repeat(text, 4), statuses);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public void GameLayerKeepsPageDataWhenOffline()
    {
        var backend = new SampleBackend { Offline = true };
        var window = LayoutProbe.Open(backend, 1280, 860, "music-library");
        try
        {
            var model = (MusicPageModel)window.GetVisualDescendants().OfType<MusicPage>().Single().DataContext!;
            Assert.False(model.HasCapabilityNotice);
            Assert.Equal(model.Data.Actions.CanStart, model.CanStart);
            Assert.Equal(model.Data.Actions.CanNext, model.CanNext);
            Assert.Equal(model.Data.Actions.CanStop, model.CanStop);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void GameLayerClosesWithEscapeOrOutsideClickAndFocusReturnsToTheEntry()
    {
        var backend = new SampleBackend { CapabilityNotices = Unavailable };
        var window = LayoutProbe.Open(backend, 1280, 860, "music-library");
        try
        {
            var page = window.GetVisualDescendants().OfType<MusicPage>().Single();
            var model = (MusicPageModel)page.DataContext!;
            var entry = Named<Button>(page, "GameLayerButton");

            // 键盘打开：焦点落在层内的关闭按钮，Esc 关闭后回到入口。
            entry.Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            LayoutProbe.Render(window);
            Assert.True(model.IsGameOpen);
            Assert.Same(Named<Button>(page, "GameLayerClose"), window.FocusManager?.GetFocusedElement());
            PressKey(window, Key.Escape);
            Assert.False(model.IsGameOpen);
            Assert.Same(entry, window.FocusManager?.GetFocusedElement());

            // 点层外关闭，不发出任何意图，焦点同样回到入口。
            Click(window, entry);
            Assert.True(model.IsGameOpen);
            ClickOutside(window);
            Assert.False(model.IsGameOpen);
            Assert.Same(entry, window.FocusManager?.GetFocusedElement());
            Assert.Empty(backend.UiIntents);

            // Tab 只在层内循环；关闭按钮同样关闭。
            Click(window, entry);
            var panel = Named<Border>(page, "GamePanel");
            for (var index = 0; index < 12; index++)
            {
                PressKey(window, Key.Tab);
                var focused = window.FocusManager?.GetFocusedElement() as Visual;
                Assert.True(focused is not null && focused.GetVisualAncestors().Contains(panel),
                    $"第 {index + 1} 次 Tab 后焦点离开了层：{focused?.GetType().Name}");
            }
            Click(window, Named<Button>(page, "GameLayerClose"));
            Assert.False(model.IsGameOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(UiLanguage.SimplifiedChinese)]
    [InlineData(UiLanguage.TraditionalChinese)]
    public void MiniDisablesGameActionsWithNoticeAndRecovers(UiLanguage language)
    {
        var backend = new SampleBackend { CapabilityNotices = Unavailable };
        var window = OpenMini(backend, 440, 920, UiThemeChoice.Light, language);
        try
        {
            var model = (MiniWindowModel)window.DataContext!;
            // 没有选乐谱时说明也在：下一首与停止停用的原因就在按钮下方。
            var notice = Named<TextBlock>(window, "CapabilityNotice");
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Equal(PlayNotice(language), notice.Text);
            Assert.True(model.Data.Actions.CanNext && model.Data.Actions.CanStop);
            Assert.False(Named<Button>(window, "NextButton").IsEffectivelyEnabled);
            Assert.False(Named<Button>(window, "StopButton").IsEffectivelyEnabled);

            // 选一张乐谱：加入队列可用，开始演奏停用。
            model.SelectCommand.Execute("s2");
            LayoutProbe.Settle();
            Assert.True(Named<Button>(window, "EnqueueButton").IsEffectivelyEnabled);
            Assert.False(Named<Button>(window, "StartButton").IsEffectivelyEnabled);

            backend.CapabilityNotices = null;
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.False(notice.IsEffectivelyVisible);
            Assert.True(Named<Button>(window, "NextButton").IsEffectivelyEnabled);
            Assert.True(Named<Button>(window, "StopButton").IsEffectivelyEnabled);
            Assert.Equal(model.Data.Actions.CanStart, Named<Button>(window, "StartButton").IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
            ResetAppearance();
        }
    }

    [AvaloniaFact]
    public void MiniSharesStateWithMainWindowAndSendsLibraryIntents()
    {
        var backend = new SampleBackend();
        var main = LayoutProbe.Open(backend, 1280, 860, "music-library");
        var shell = (ShellViewModel)main.DataContext!;
        var controller = shell.Catalog.Mini!;
        Assert.Same(main, controller.Main);

        // 入口在乐谱库页标题区。
        ClickInPlace(main, Named<Button>(main, "OpenMiniButton"));
        var mini = Assert.IsType<MiniWindow>(controller.Window);
        var model = (MiniWindowModel)mini.DataContext!;
        var page = (MusicPageModel)main.GetVisualDescendants().OfType<MusicPage>().Single().DataContext!;
        Assert.Equal("全部乐谱", model.ListName);
        Assert.Equal(page.Data.Queue.Count, model.Data.Queue.Count);

        // 选清单：同一个后端状态，主窗口的乐谱库页随之切换。
        Click(mini, mini.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter as string == "music.playlist:campfire"));
        Assert.Equal(("music.playlist", "campfire"), backend.UiIntents[^1]);
        Assert.Equal("營火晚會", model.ListName);
        Assert.Equal(["s1", "s2", "s3"], model.Tiles.Select(tile => tile.Id));
        Assert.Equal(["s1", "s2", "s3"], page.VisibleScores.Select(row => row.Id));

        // 演奏中的磁贴高亮。
        Assert.Contains("playing", Tile(mini, "s1").Classes);

        // 选乐谱后操作条出现，加入队列交出 music.enqueue。
        Assert.False(Named<Border>(mini, "SelectionBar").IsVisible);
        Click(mini, Tile(mini, "s2"));
        Assert.True(Named<Border>(mini, "SelectionBar").IsVisible);
        Assert.Contains("active", Tile(mini, "s2").Classes);
        var queued = page.Data.Queue.Count;
        ClickInPlace(mini, Named<Button>(mini, "EnqueueButton"));
        Assert.Equal(("music.enqueue", "s2"), backend.UiIntents[^1]);
        Assert.Equal(queued + 1, page.Data.Queue.Count);

        // 游戏无法选取的乐谱：磁贴停用并显示原因，命令也选不中它。
        Click(mini, mini.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter as string == "music.playlist:all"));
        var blocked = Tile(mini, "s8");
        Assert.False(blocked.IsEnabled);
        Assert.Contains(blocked.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "名称结尾有空白，游戏无法选取");
        model.SelectCommand.Execute("s8");
        Assert.DoesNotContain(model.Tiles, tile => tile.Selected && tile.Id == "s8");

        // 底部活动区：停止、开始、下一首都是乐谱库页已有的意图。
        Assert.Equal("夜風與營火", model.CurrentTitle);
        Assert.Equal("下一首：提爾科納的雨", model.NextLine);
        ClickInPlace(mini, Named<Button>(mini, "StopButton"));
        Assert.Equal(("music.stop", (string?)null), backend.UiIntents[^1]);
        Click(mini, Tile(mini, "s3"));
        ClickInPlace(mini, Named<Button>(mini, "StartButton"));
        Assert.Equal(("music.start", (string?)null), backend.UiIntents[^1]);
        ClickInPlace(mini, Named<Button>(mini, "NextButton"));
        Assert.Equal(("music.next", (string?)null), backend.UiIntents[^1]);

        // 紧急停止在迷你窗里直接可达。
        ClickInPlace(mini, Named<Button>(mini, "EmergencyStopButton"));
        Assert.Equal(1, backend.EmergencyStopRequests);

        // 再按一次入口不会开第二个迷你窗；关闭主窗口时迷你窗一并关闭。
        model.ShowMainCommand.Execute(null);
        ClickInPlace(main, Named<Button>(main, "OpenMiniButton"));
        Assert.Same(mini, controller.Window);
        main.Close();
        LayoutProbe.Settle();
        Assert.Null(controller.Window);
        Assert.False(mini.IsVisible);
    }

    [AvaloniaFact]
    public void MiniFollowsLanguageWithoutLosingSelection()
    {
        var window = OpenMini(new SampleBackend(), 440, 920, UiThemeChoice.Light, UiLanguage.SimplifiedChinese);
        try
        {
            var model = (MiniWindowModel)window.DataContext!;
            model.SelectCommand.Execute("s5");
            UiText.Apply(Application.Current!, UiLanguage.TraditionalChinese);
            LayoutProbe.Settle();
            Assert.Equal("已選：杜巴頓廣場午後", model.SelectedLine);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "目前曲目");
            // 同名乐谱逐张列出并各自标明。
            Assert.Equal(["同名第 1 張", "同名第 2 張"], model.Tiles.Where(tile => tile.Title == "杜巴頓廣場午後").Select(tile => tile.Tag));
        }
        finally
        {
            window.Close();
            UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        }
    }

    private static Button Tile(Window window, string id) =>
        Named<ItemsControl>(window, "TileList").GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter as string == id);

    [AvaloniaFact]
    public void ScoreTableShowsInstrumentAndLengthAndRequestsOnlyTheVisiblePage()
    {
        var backend = new ManyScoresBackend(75);
        var window = LayoutProbe.Open(backend, 1280, 860, "music-library");
        try
        {
            var model = (MusicPageModel)window.GetVisualDescendants().OfType<MusicPage>().Single().DataContext!;
            Assert.Equal(30, model.VisibleScores.Count);
            Assert.Equal("第 1／3 页，共 75 张", model.PageLabel);
            // 登记给后端查询的只有这一页。
            Assert.Equal(Enumerable.Range(0, 30).Select(Id), backend.Requests[^1]);
            Assert.Equal("魯特琴", model.VisibleScores[0].Instrument);
            Assert.Equal("未指定", model.VisibleScores[1].Instrument);
            Assert.Equal("未知", model.VisibleScores[1].Length);
            Assert.Equal("2:00", model.VisibleScores[0].Length);

            ClickInPlace(window, window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "NextPageButton"));
            Assert.Equal(Enumerable.Range(30, 30).Select(Id), backend.Requests[^1]);
            Assert.Equal("score-30", model.VisibleScores[0].Id);

            // 后端刷新（例如查到时长）不会让同一页重复登记。
            var count = backend.Requests.Count;
            backend.Raise();
            LayoutProbe.Settle();
            Assert.Equal(count, backend.Requests.Count);
            Assert.All(backend.Requests, request => Assert.True(request.Count <= MusicPageModel.PageSize));
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
            LayoutProbe.SaveScreenshot(window, "music-library-paged-1280x860");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void DurationCacheQueriesInSmallBatchesAndOnlyOnce()
    {
        var now = DateTimeOffset.UnixEpoch;
        var cache = new ScoreDurationCache(8, TimeSpan.FromSeconds(1), () => now);
        var ids = Enumerable.Range(0, 200).Select(Id).ToList();

        // 一次登记两百张，也只保留最近的一部分待查，每轮最多八张。
        Assert.True(cache.Want(ids));
        var first = cache.NextBatch();
        Assert.Equal(8, first.Count);
        Assert.Empty(cache.NextBatch());
        now += TimeSpan.FromSeconds(1);
        var second = cache.NextBatch();
        Assert.Equal(8, second.Count);
        Assert.Empty(first.Intersect(second));

        foreach (var id in first) cache.Record(id, id == "score-0" ? 125_000 : null);
        Assert.True(cache.TryGet("score-0", out var known) && known == 125_000);
        Assert.True(cache.TryGet("score-1", out var unknown) && unknown is null);

        // 已查过的（包括查不到的）不再查询。
        Assert.False(cache.Want(first));
        var batches = 2;
        for (var guard = 0; guard < 20; guard++)
        {
            now += TimeSpan.FromSeconds(1);
            var batch = cache.NextBatch();
            if (batch.Count == 0) break;
            batches++;
            foreach (var id in batch) cache.Record(id, null);
        }
        Assert.Equal(ScoreDurationCache.Capacity / 8, batches);
    }

    private static string Id(int index) => $"score-{index}";

    /// <summary>有很多乐谱的样本：前一半在清单里指定了魯特琴、时长已知，其余未指定、未知。</summary>
    private sealed class ManyScoresBackend(int count) : IDesktopBackend
    {
        public event EventHandler<string>? ShortcutTriggered { add { } remove { } }
        private readonly SampleBackend sample = new();
        private EventHandler? changed;

        public List<IReadOnlyList<string>> Requests { get; } = [];

        public event EventHandler? Changed
        {
            add { changed += value; sample.Changed += value; }
            remove { changed -= value; sample.Changed -= value; }
        }

        public void Raise() => changed?.Invoke(this, EventArgs.Empty);

        public MusicPageData GetMusic() => sample.GetMusic() with
        {
            Scores = Enumerable.Range(0, count).Select(index => new ScoreRow(Id(index), $"第 {index + 1} 首練習曲", string.Empty, "芙蕾雅",
                index % 2 == 0 ? "魯特琴" : "未指定", index % 2 == 0 ? "2:00" : "未知", false, true, index % 2 == 0 ? "lute" : null)).ToList()
        };

        public void RequestScoreDetails(IReadOnlyList<string> scoreIds) => Requests.Add(scoreIds);

        public ActivitySnapshot GetActivity() => sample.GetActivity();
        public BindingView GetGameBinding() => sample.GetGameBinding();
        public CombatPageData GetCombat() => sample.GetCombat();
        public RoomsPageData GetRooms() => sample.GetRooms();
        public ColorPageData GetColor() => sample.GetColor();
        public InventoryPageData GetInventory() => sample.GetInventory();
        public ModulesPageData GetModules() => sample.GetModules();
        public SettingsPageData GetSettings() => sample.GetSettings();
        public PixelRect? GetGameWindowBounds() => null;
        public string? GetCapabilityNotice(string capabilityId) => null;
        public TopicNotice? GetPresentationNotice(string topic) => null;
        public CapabilityLevel GetCapabilityLevel(string capabilityId) => CapabilityLevel.Unknown;
        public Task RequestEmergencyStopAsync(CancellationToken cancellationToken) => sample.RequestEmergencyStopAsync(cancellationToken);
        public Task SendCombatIntentAsync(CombatIntent intent, string? targetId, CancellationToken cancellationToken) =>
            sample.SendCombatIntentAsync(intent, targetId, cancellationToken);
        public Task SendIntentAsync(string intent, string? argument, CancellationToken cancellationToken) =>
            sample.SendIntentAsync(intent, argument, cancellationToken);
        public Task<BackendReply> SaveColorDraftAsync(ColorDraft draft, CancellationToken cancellationToken) =>
            sample.SaveColorDraftAsync(draft, cancellationToken);
        public Task<BackendReply> SaveColorProfileAsync(string name, ColorDraft draft, CancellationToken cancellationToken) =>
            sample.SaveColorProfileAsync(name, draft, cancellationToken);
        public Task<BackendReply> LoadColorProfileAsync(string profileId, CancellationToken cancellationToken) =>
            sample.LoadColorProfileAsync(profileId, cancellationToken);
        public Task<BackendReply> DeleteColorProfileAsync(string profileId, CancellationToken cancellationToken) =>
            sample.DeleteColorProfileAsync(profileId, cancellationToken);
        public Task<ColorAtlasReply> GetColorAtlasAsync(string targetHex, string matchMode, double tolerance, bool full, CancellationToken cancellationToken) =>
            sample.GetColorAtlasAsync(targetHex, matchMode, tolerance, full, cancellationToken);
        public Task<GameWindowActivation> ActivateGameWindowAsync(CancellationToken cancellationToken) => sample.ActivateGameWindowAsync(cancellationToken);
        public Task<CaptureReply> CaptureScreenAsync(CancellationToken cancellationToken) => sample.CaptureScreenAsync(cancellationToken);
        public Task ReleaseCaptureAsync(string captureId, CancellationToken cancellationToken) => sample.ReleaseCaptureAsync(captureId, cancellationToken);
        public Task<BackendReply> ChangeShortcutAsync(string actionId, KeyChord chord, CancellationToken cancellationToken) =>
            sample.ChangeShortcutAsync(actionId, chord, cancellationToken);
        public Task<GameWindowListReply> ListGameWindowsAsync(CancellationToken cancellationToken) =>
            sample.ListGameWindowsAsync(cancellationToken);
        public Task<BackendReply> SelectGameWindowAsync(string candidateId, CancellationToken cancellationToken) =>
            sample.SelectGameWindowAsync(candidateId, cancellationToken);
        public Task<BackendReply> SaveUpdatePolicyAsync(UpdatePolicyView policy, CancellationToken cancellationToken) =>
            sample.SaveUpdatePolicyAsync(policy, cancellationToken);
        public Task<RoomPreferencesReply> LoadRoomPreferencesAsync(CancellationToken cancellationToken) =>
            sample.LoadRoomPreferencesAsync(cancellationToken);
        public Task<BackendReply> SaveRoomPreferencesAsync(MabiTool.Contracts.RoomPreferences preferences, string etag,
            CancellationToken cancellationToken) => sample.SaveRoomPreferencesAsync(preferences, etag, cancellationToken);
        public Task<ColorHistoryReply> QueryColorHistoryAsync(string? beforeOperationId, CancellationToken cancellationToken) =>
            sample.QueryColorHistoryAsync(beforeOperationId, cancellationToken);
        public DiagnosticAccess GetDiagnosticAccess() => sample.GetDiagnosticAccess();
        public event EventHandler<DiagnosticPushedRecord>? DiagnosticRecordReceived { add { } remove { } }
        public event EventHandler? DiagnosticStreamRestored { add { } remove { } }
        public Task<DiagnosticRuntimeReply> ReadDiagnosticRuntimeAsync(CancellationToken cancellationToken) => sample.ReadDiagnosticRuntimeAsync(cancellationToken);
        public Task<DiagnosticRecordsReply> QueryDiagnosticRecordsAsync(DiagnosticRecordFilter filter, CancellationToken cancellationToken) =>
            sample.QueryDiagnosticRecordsAsync(filter, cancellationToken);
        public Task<DiagnosticCaptureReply> GetDiagnosticCaptureAsync(CancellationToken cancellationToken) => sample.GetDiagnosticCaptureAsync(cancellationToken);
        public Task<DiagnosticCaptureReply> StartDiagnosticCaptureAsync(DebugCaptureSettings settings, CancellationToken cancellationToken) =>
            sample.StartDiagnosticCaptureAsync(settings, cancellationToken);
        public Task<DiagnosticCaptureReply> StopDiagnosticCaptureAsync(string captureId, CancellationToken cancellationToken) =>
            sample.StopDiagnosticCaptureAsync(captureId, cancellationToken);
        public Task<DiagnosticExportReply> ExportDiagnosticsAsync(DiagnosticExportScope scope, string? captureSessionId,
            IProgress<double>? progress, CancellationToken cancellationToken) =>
            sample.ExportDiagnosticsAsync(scope, captureSessionId, progress, cancellationToken);
    }
}
