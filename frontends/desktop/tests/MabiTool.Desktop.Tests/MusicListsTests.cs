using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Pages.Music;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>乐谱库的来源合并、清单管理与乐谱导入：编辑对应的后端批量编辑、来源显示规则、管理层与导入层的行为。</summary>
public sealed class MusicListsTests
{
    private static MusicFolder Folder(string id, string? parent, string name, string key) => new(id, parent, name, key);
    private static PlaylistItem Item(string id, string score, string key, string? instrument = null) => new(id, score, instrument, key);
    private static MusicPlaylist List(string id, string? folder, string name, string key, params PlaylistItem[] items) =>
        new(id, folder, name, string.Empty, null, key, items);

    private static readonly MusicFolder[] Folders = [Folder("f1", null, "公演", "000000"), Folder("f2", "f1", "夜场", "000000")];

    private static MusicPlaylist[] Lists() =>
    [
        List("p1", null, "練習", "000000", Item("i1", "s1", "000000"), Item("i2", "s2", "000001", "piano"), Item("i3", "s3", "000002")),
        List("p2", "f2", "營火", "000004")
    ];

    [Fact]
    public void EveryListEditBecomesOneBackendBatchWithStableIdsAndSortKeys()
    {
        // 新建：放进所选文件夹，排序键取同级最大值加一；名称去掉首尾空白。
        var create = LiveBackend.BuildMusicEdit(new(MusicListEditKind.CreatePlaylist, FolderId: "f2", Text: "  新清單 "), Folders, Lists())!;
        var created = Assert.Single(create.PutPlaylists);
        Assert.Equal(("新清單", "f2", "000005"), (created.Name, created.FolderId, created.SortKey));
        Assert.Empty(created.Items);
        Assert.Matches("^[0-9a-f]{32}$", created.Id);
        var root = Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.CreatePlaylist, Text: "根"), Folders, Lists())!.PutPlaylists);
        Assert.Equal((null, "000001"), (root.FolderId, root.SortKey));
        var folder = Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.CreateFolder, FolderId: "f1", Text: "子夹"), Folders, Lists())!.PutFolders);
        Assert.Equal(("子夹", "f1", "000001"), (folder.Name, folder.ParentId, folder.SortKey));

        // 重命名、备注、默认乐器（空表示未指定）、移动（同一文件夹不变；移到文件夹排在末尾）、删除。
        Assert.Equal("改名", Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.RenamePlaylist, PlaylistId: "p1", Text: "改名"), Folders, Lists())!.PutPlaylists).Name);
        Assert.Equal("a\nb", Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.SetNotes, PlaylistId: "p1", Text: "a\r\nb"), Folders, Lists())!.PutPlaylists).Notes);
        Assert.Equal("harp", Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.SetDefaultInstrument, PlaylistId: "p1", Text: "harp"), Folders, Lists())!.PutPlaylists).DefaultInstrumentId);
        Assert.Null(Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.SetDefaultInstrument, PlaylistId: "p1", Text: ""), Folders, Lists())!.PutPlaylists).DefaultInstrumentId);
        Assert.Null(LiveBackend.BuildMusicEdit(new(MusicListEditKind.MovePlaylist, PlaylistId: "p1", FolderId: ""), Folders, Lists()));
        var moved = Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.MovePlaylist, PlaylistId: "p1", FolderId: "f2"), Folders, Lists())!.PutPlaylists);
        Assert.Equal(("f2", "000005"), (moved.FolderId, moved.SortKey));
        Assert.Equal(["p1"], LiveBackend.BuildMusicEdit(new(MusicListEditKind.DeletePlaylist, PlaylistId: "p1"), Folders, Lists())!.DeletePlaylists);
        Assert.Equal("新夹", Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.RenameFolder, FolderId: "f1", Text: "新夹"), Folders, Lists())!.PutFolders).Name);
        Assert.Equal(["f1"], LiveBackend.BuildMusicEdit(new(MusicListEditKind.DeleteFolder, FolderId: "f1"), Folders, Lists())!.DeleteFolders);

        // 加入乐谱：追加一个新条目（同一乐谱可重复加入，条目 ID 不同）；移除只去掉所选条目。
        var added = Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.AddItem, PlaylistId: "p1", ScoreId: "s1"), Folders, Lists())!.PutPlaylists);
        Assert.Equal(4, added.Items.Count);
        Assert.Equal(("s1", "000003"), (added.Items[^1].ScoreId, added.Items[^1].SortKey));
        Assert.DoesNotContain(added.Items[^1].Id, new[] { "i1", "i2", "i3" });
        var removed = Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.RemoveItem, PlaylistId: "p1", ItemId: "i2"), Folders, Lists())!.PutPlaylists);
        Assert.Equal(["i1", "i3"], removed.Items.Select(item => item.Id));

        // 排序：相邻两项交换，全部条目按位置重新编号，条目 ID 不变；已在边界时没有变化。
        var up = Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.MoveItem, PlaylistId: "p1", ItemId: "i3", Direction: -1), Folders, Lists())!.PutPlaylists);
        Assert.Equal(["i1", "i3", "i2"], up.Items.Select(item => item.Id));
        Assert.Equal(["000000", "000001", "000002"], up.Items.Select(item => item.SortKey));
        Assert.Equal("piano", up.Items.Single(item => item.Id == "i2").InstrumentId);
        var down = Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.MoveItem, PlaylistId: "p1", ItemId: "i1", Direction: 1), Folders, Lists())!.PutPlaylists);
        Assert.Equal(["i2", "i1", "i3"], down.Items.Select(item => item.Id));
        Assert.Null(LiveBackend.BuildMusicEdit(new(MusicListEditKind.MoveItem, PlaylistId: "p1", ItemId: "i1", Direction: -1), Folders, Lists()));
        Assert.Null(LiveBackend.BuildMusicEdit(new(MusicListEditKind.MoveItem, PlaylistId: "p1", ItemId: "i3", Direction: 1), Folders, Lists()));

        // 逐条乐器：只改所选条目，空表示沿用清单默认。
        var tuned = Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.SetItemInstrument, PlaylistId: "p1", ItemId: "i1", Text: "flute"), Folders, Lists())!.PutPlaylists);
        Assert.Equal(["flute", "piano", null], tuned.Items.Select(item => item.InstrumentId));
        var cleared = Assert.Single(LiveBackend.BuildMusicEdit(new(MusicListEditKind.SetItemInstrument, PlaylistId: "p1", ItemId: "i2", Text: ""), Folders, Lists())!.PutPlaylists);
        Assert.Null(cleared.Items.Single(item => item.Id == "i2").InstrumentId);
    }

    [Fact]
    public void InvalidOrOutdatedEditsAreRefusedWithAReasonBeforeAnythingIsSent()
    {
        void Refuses(MusicListEdit edit) => Assert.Throws<InvalidOperationException>(() => LiveBackend.BuildMusicEdit(edit, Folders, Lists()));
        Refuses(new(MusicListEditKind.CreatePlaylist, Text: "   "));
        Refuses(new(MusicListEditKind.CreatePlaylist, Text: new string('长', 201)));
        Refuses(new(MusicListEditKind.CreatePlaylist, Text: "坏\u0007名"));
        Refuses(new(MusicListEditKind.CreatePlaylist, FolderId: "gone", Text: "甲"));
        Refuses(new(MusicListEditKind.RenamePlaylist, PlaylistId: "gone", Text: "甲"));
        Refuses(new(MusicListEditKind.SetNotes, PlaylistId: "p1", Text: new string('备', 4001)));
        Refuses(new(MusicListEditKind.AddItem, PlaylistId: "p1"));
        Refuses(new(MusicListEditKind.RemoveItem, PlaylistId: "p1", ItemId: "gone"));
        Refuses(new(MusicListEditKind.MoveItem, PlaylistId: "p1", ItemId: "gone", Direction: 1));
        Refuses(new(MusicListEditKind.SetItemInstrument, PlaylistId: "p1", ItemId: "gone"));
        Refuses(new(MusicListEditKind.RenameFolder, FolderId: "gone", Text: "甲"));
        Refuses(new(MusicListEditKind.DeleteFolder, FolderId: "gone"));
    }

    // 断言界面文字：与切换界面语言的用例串行运行（AvaloniaFact），不读到别的用例临时切换的语言。
    [AvaloniaFact]
    public void ScoreTableShowsGameListThenTheCurrentCharactersCaptureThenImportsAndLabelsEachSource()
    {
        static MusicScoreSource Source(string key, string kind) => new(key, "TW", "r", "0", "p", kind, new(1), "complete", false);
        var library = new MusicLibraryInfo(new(1), 0, 0, 0, new([], new Dictionary<string, string>(), []),
            [Source("IMPORT", "ImportedLibrary"), Source("OTHER", "Inventory"), Source("CAPTURE", "Inventory"), Source("LIST", "GameList")]);
        Assert.Equal(["LIST", "CAPTURE", "IMPORT"], LiveBackend.DisplayedMusicSources(library, "CAPTURE").Select(source => source.ScopeKey));
        // 还没有同步成功过（没有范围键）：有多个抓包来源无法判断是哪个角色，都不显示；只有一个就显示它。
        Assert.Equal(["LIST", "IMPORT"], LiveBackend.DisplayedMusicSources(library, null).Select(source => source.ScopeKey));
        var single = library with { Sources = [.. library.Sources.Where(source => source.ScopeKey != "OTHER")] };
        Assert.Equal(["LIST", "CAPTURE", "IMPORT"], LiveBackend.DisplayedMusicSources(single, null).Select(source => source.ScopeKey));
        Assert.Empty(LiveBackend.DisplayedMusicSources(null, "CAPTURE"));

        // 名称前的来源标签：游戏列表为游戏乐谱，背包抓包（玩家自制与复制乐谱）为自制乐谱，MobiFolio 为导入；筛选按与语言无关的来源键。
        ScoreEntry Score(string provider) => new("id", "t", "t", null, [], [new(provider, "TW", "r:0", null, null, "title", false)], null, "present", new(1));
        var providers = new[] { "tw-cli", "tw-capture", "mobifolio:abc", "mobifolio" };
        Assert.Equal(["游戏乐谱", "自制乐谱", "导入", "导入"], providers.Select(provider => LiveBackend.ScoreSourceLabel(Score(provider))));
        Assert.Equal([ScoreSources.Game, ScoreSources.Composed, ScoreSources.Imported, ScoreSources.Imported],
            providers.Select(provider => LiveBackend.ScoreSourceKey(Score(provider))));
    }

    [Fact]
    public void QueueInstrumentEditReplacesOnlyThatItemAndEnqueueFromAListCarriesItsInstrument()
    {
        // 改队列一项的乐器：会话命令 edit 的完整条目里只有这一项的乐器变了，次序、条目 ID 与其余乐器不变；空表示游戏当前乐器。
        PerformanceQueueItem Queued(string id, string score, string? instrument, int index) => new(new(id, score, instrument, index.ToString("D6")), index);
        PerformanceQueueItem[] queue = [Queued("q1", "s1", null, 0), Queued("q2", "s2", "piano", 1), Queued("q3", "s3", null, 2)];
        var edited = LiveBackend.QueueItemsWithInstrument(queue, "q3", "harp");
        Assert.Equal([("q1", (string?)null), ("q2", "piano"), ("q3", "harp")], edited.Select(item => (item.Id, item.InstrumentId)));
        Assert.Equal(queue.Select(entry => entry.Item.ScoreId), edited.Select(item => item.ScoreId));
        Assert.Null(LiveBackend.QueueItemsWithInstrument(queue, "q2", "").Single(item => item.Id == "q2").InstrumentId);
        Assert.Throws<InvalidOperationException>(() => LiveBackend.QueueItemsWithInstrument(queue, "gone", "harp"));

        // 从清单加入队列：带上该乐谱在清单里条目的乐器，条目没有写时取清单默认乐器；没有选清单或乐谱不在清单里时不指定。
        var list = Lists()[0] with { DefaultInstrumentId = "flute" };
        Assert.Equal("piano", LiveBackend.EnqueueInstrument("s2", list));
        Assert.Equal("flute", LiveBackend.EnqueueInstrument("s1", list));
        Assert.Null(LiveBackend.EnqueueInstrument("s1", Lists()[0]));
        Assert.Null(LiveBackend.EnqueueInstrument("s9", list));
        Assert.Null(LiveBackend.EnqueueInstrument("s2", null));

        // 乐器选项是背包里的乐器：游戏原文名，标识为 game: 加名称，同名只列一次。
        var choices = LiveBackend.InstrumentChoices(["魯特琴", "大鼓", "魯特琴", " ", "3和弦魯特琴"]);
        Assert.Equal([("game:3和弦魯特琴", "3和弦魯特琴"), ("game:大鼓", "大鼓"), ("game:魯特琴", "魯特琴")], choices.Select(item => (item.Id, item.Name)));
        Assert.Empty(LiveBackend.InstrumentChoices(null));
    }

    [Fact]
    public void PlaylistsListRootFirstThenEachFolderDepthFirstWithItsPath()
    {
        MusicFolder[] folders = [Folder("a", null, "甲", "000001"), Folder("b", "a", "乙", "000000"), Folder("c", null, "丙", "000000")];
        MusicPlaylist[] lists = [List("x", "b", "在乙", "000000"), List("y", null, "根二", "000001"), List("z", "c", "在丙", "000000"), List("w", null, "根一", "000000")];
        Assert.Equal(["w", "y", "z", "x"], LiveBackend.OrderedPlaylists(folders, lists).Select(item => item.Id));
        var paths = LiveBackend.FolderPaths(folders);
        Assert.Equal(("甲 / 乙", "丙"), (paths["b"], paths["c"]));
        // 所在文件夹读不到的清单仍显示，排在最后。
        Assert.Equal("lost", LiveBackend.OrderedPlaylists(folders, [.. lists, List("lost", "gone", "孤", "000000")])[^1].Id);
    }

    // 断言界面文字：与切换界面语言的用例串行运行（AvaloniaFact），不读到别的用例临时切换的语言。
    [AvaloniaFact]
    public void StatusLineMentionsSkippedGameScoresOnlyWithoutAGameListAndReportsAnUnavailableCapture()
    {
        MusicOwnedSync Owned(MusicOwnedSyncStatus status, string? reason, int skipped) => new("e", status, reason, "K", 3, skipped, 0, true, null);
        var withoutList = LiveBackend.MusicSourceLine(3, null, null, null, Owned(MusicOwnedSyncStatus.Synced, null, 2));
        Assert.Equal("3 张乐谱，正在从游戏同步；背包里另有 2 张游戏提供的乐谱，经游戏连接器同步后显示", withoutList);
        // 游戏列表已同步：背包里的游戏乐谱由列表提供，不再提示。
        Assert.Equal("3 张乐谱，已同步", LiveBackend.MusicSourceLine(3, null, "complete", null, Owned(MusicOwnedSyncStatus.Synced, null, 2)));
        // 没有被跳过的游戏乐谱：状态行与不带抓包同步时相同。
        Assert.Equal(LiveBackend.MusicSourceLine(3, null, null, null), LiveBackend.MusicSourceLine(3, null, null, null, Owned(MusicOwnedSyncStatus.Synced, null, 0)));
        // 抓包同步不可用：写明原因（来源保持上次内容）。
        var unavailable = LiveBackend.MusicSourceLine(3, null, "complete", null, Owned(MusicOwnedSyncStatus.Unavailable, "inventory.bindingChanged", 0));
        Assert.Equal("3 张乐谱，已同步；背包乐谱来源暂不可用：游戏窗口已变化", unavailable);
    }

    private static (Avalonia.Controls.Window Window, MusicPage Page, MusicPageModel Model) Open(SampleBackend backend, UiLanguage language = UiLanguage.SimplifiedChinese)
    {
        var window = MabiTool.Desktop.Tests.LayoutProbe.Open(backend, 1280, 860, "music-library", language);
        var page = window.GetVisualDescendants().OfType<MusicPage>().Single();
        return (window, page, (MusicPageModel)page.DataContext!);
    }

    // 层打开时清单栏被层盖住：在层里的清单标签选择，发出的是清单栏同一个意图。
    private static void PickPlaylist(Avalonia.Controls.Window window, MusicPage page, string id) =>
        Click(window, Named<ItemsControl>(page, "PlaylistChoiceList").GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter as string == id));

    [AvaloniaFact]
    public void TheListsLayerCreatesRenamesMovesAndDeletesThroughOneBackendEditEach()
    {
        var backend = new SampleBackend();
        var (window, page, model) = Open(backend);
        try
        {
            Click(window, Named<Button>(page, "ListsButton"));
            Assert.True(model.IsListsOpen && Named<Panel>(page, "ListsLayer").IsEffectivelyVisible);
            Assert.False(model.IsGameOpen);
            Assert.True(Named<TextBlock>(page, "NoPlaylistNote").IsEffectivelyVisible);

            // 新建清单与文件夹：名称为空时停用；放进所选位置（根或文件夹）。
            var create = Named<Button>(page, "CreatePlaylistButton");
            Assert.False(create.IsEffectivelyEnabled);
            model.Lists.NewName = " 夜場 ";
            LayoutProbe.Settle();
            Click(window, create);
            Assert.Equal(new MusicListEdit(MusicListEditKind.CreatePlaylist, Text: " 夜場 "), backend.MusicEdits[^1]);
            Assert.Equal(string.Empty, model.Lists.NewName);
            Assert.Equal("已保存", model.Lists.Message);
            model.Lists.FolderId = "folder-show";
            model.Lists.NewName = "子夹";
            LayoutProbe.Settle();
            Click(window, Named<Button>(page, "CreateFolderButton"));
            Assert.Equal(new MusicListEdit(MusicListEditKind.CreateFolder, FolderId: "folder-show", Text: "子夹"), backend.MusicEdits[^1]);
            Assert.Equal("新建位置：公演", model.Lists.TargetLabel);

            // 文件夹的重命名与删除：删除要点两次。
            model.Lists.FolderName = "公演场";
            LayoutProbe.Settle();
            Click(window, Named<Button>(page, "RenameFolderButton"));
            Assert.Equal(new MusicListEdit(MusicListEditKind.RenameFolder, FolderId: "folder-show", Text: "公演场"), backend.MusicEdits[^1]);
            var before = backend.MusicEdits.Count;
            Click(window, Named<Button>(page, "DeleteFolderButton"));
            Assert.Equal(before, backend.MusicEdits.Count);
            Assert.Equal("确认删除", model.Lists.DeleteFolderLabel);
            Click(window, Named<Button>(page, "DeleteFolderButton"));
            Assert.Equal(new MusicListEdit(MusicListEditKind.DeleteFolder, FolderId: "folder-show"), backend.MusicEdits[^1]);
            Assert.Equal(string.Empty, model.Lists.FolderId);

            // 选一个清单（清单栏的选择）：层里显示它的名称、备注、条目；重命名、备注、默认乐器、移动各发送一次编辑。
            Assert.True(Named<TextBlock>(page, "NoPlaylistNote").IsEffectivelyVisible);
            PickPlaylist(window, page, "campfire");
            Assert.False(Named<TextBlock>(page, "NoPlaylistNote").IsEffectivelyVisible);
            Assert.Equal(["練習中", "公演 / 營火晚會"], model.Lists.PlaylistChoices.Select(choice => choice.Label));
            Assert.Equal("公演 / 營火晚會", model.Lists.PlaylistHeader);
            Assert.Equal(["夜風與營火", "提爾科納的雨", "牧羊少年的口哨"], model.Lists.Items.Select(item => item.Title));
            Assert.False(Named<Button>(page, "RenamePlaylistButton").IsEffectivelyEnabled);
            model.Lists.PlaylistName = "營火二場";
            model.Lists.Notes = "改備註";
            LayoutProbe.Settle();
            Click(window, Named<Button>(page, "RenamePlaylistButton"));
            Click(window, Named<Button>(page, "SaveNotesButton"));
            model.Lists.SetDefaultInstrumentCommand.Execute("piano");
            model.Lists.MovePlaylistCommand.Execute(string.Empty);
            LayoutProbe.Settle();
            Assert.Equal(
            [
                new(MusicListEditKind.RenamePlaylist, PlaylistId: "campfire", Text: "營火二場"),
                new(MusicListEditKind.SetNotes, PlaylistId: "campfire", Text: "改備註"),
                new(MusicListEditKind.SetDefaultInstrument, PlaylistId: "campfire", Text: "piano"),
                new MusicListEdit(MusicListEditKind.MovePlaylist, PlaylistId: "campfire", FolderId: string.Empty)
            ], backend.MusicEdits.TakeLast(4));

            // 条目：上移、下移、移除、逐条乐器、从乐谱表加入。
            model.Lists.MoveItemUpCommand.Execute("i2");
            model.Lists.MoveItemDownCommand.Execute("i2");
            model.Lists.RemoveItemCommand.Execute("i3");
            _ = model.Lists.SetItemInstrumentAsync("i1", "flute");
            _ = model.Lists.AddScoreAsync("campfire", "s7");
            LayoutProbe.Settle();
            Assert.Equal(
            [
                new(MusicListEditKind.MoveItem, PlaylistId: "campfire", ItemId: "i2", Direction: -1),
                new(MusicListEditKind.MoveItem, PlaylistId: "campfire", ItemId: "i2", Direction: 1),
                new(MusicListEditKind.RemoveItem, PlaylistId: "campfire", ItemId: "i3"),
                new(MusicListEditKind.SetItemInstrument, PlaylistId: "campfire", ItemId: "i1", Text: "flute"),
                new MusicListEdit(MusicListEditKind.AddItem, PlaylistId: "campfire", ScoreId: "s7")
            ], backend.MusicEdits.TakeLast(5));

            // 删除清单要点两次，切换清单后恢复为“删除清单”。
            var edits = backend.MusicEdits.Count;
            Click(window, Named<Button>(page, "DeletePlaylistButton"));
            Assert.Equal((edits, "确认删除"), (backend.MusicEdits.Count, model.Lists.DeletePlaylistLabel));
            PickPlaylist(window, page, "practice");
            Assert.Equal("删除清单", model.Lists.DeletePlaylistLabel);
            Click(window, Named<Button>(page, "DeletePlaylistButton"));
            Click(window, Named<Button>(page, "DeletePlaylistButton"));
            Assert.Equal(new MusicListEdit(MusicListEditKind.DeletePlaylist, PlaylistId: "practice"), backend.MusicEdits[^1]);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ABackendRejectionIsShownWithItsReasonAndABlockedLibraryDisablesEveryEditEntry()
    {
        var backend = new SampleBackend { MusicEditRejection = "revision.stale" };
        var (window, page, model) = Open(backend);
        try
        {
            Click(window, Named<Button>(page, "ListsButton"));
            model.Lists.NewName = "甲";
            LayoutProbe.Settle();
            Click(window, Named<Button>(page, "CreatePlaylistButton"));
            Assert.Equal("曲库已被其他前端修改，已重新读取，请再试一次", model.Lists.Message);
            Assert.True(model.Lists.MessageIsError);
            Assert.True(Named<TextBlock>(page, "ListsMessage").IsEffectivelyVisible);
            // 被拒绝后输入保留，仍可再次发送。
            Assert.Equal("甲", model.Lists.NewName);
            Assert.True(model.Lists.CanCreate);

            backend.MusicEditBlocked = "未连接后端";
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal("未连接后端", Named<TextBlock>(page, "ListsNotice").Text);
            Assert.False(Named<Button>(page, "CreatePlaylistButton").IsEffectivelyEnabled);
            Assert.False(Named<TextBox>(page, "NewNameBox").IsEffectivelyEnabled);
            var edits = backend.MusicEdits.Count;
            Click(window, Named<Button>(page, "CreatePlaylistButton"));
            Assert.Equal(edits, backend.MusicEdits.Count);
            // 乐谱表每行的“加入清单”同样停用并带原因提示。
            var add = page.GetVisualDescendants().OfType<Button>().First(button => button.Tag as string == "add-to-list");
            Assert.False(add.IsEffectivelyEnabled);
            Assert.Equal("未连接后端", ToolTip.GetTip(add));
        }
        finally { window.Close(); }
    }
}
