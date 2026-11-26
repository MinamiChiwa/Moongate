using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls.Tables;
using MabiTool.Desktop.Pages.Inventory;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>背包页：账号切换、按道具汇总的透视表（0 与未读取的区分）、按角色查询、备注列的保存与检索、列组开关。</summary>
public sealed class InventoryTests
{
    private const string Account = "acct-1";

    private static InventoryIdentity Identity(string id, string name) => new(Account, id, name, name);

    private static InventoryItemView Item(string category, InventoryContainer container, ulong type, ulong? quantity, string? name = null,
        string? character = null) =>
        new(new InventoryItem(category, container, $"{category}-{type}-{container}", new(type), null,
                quantity is { } q ? new UInt64String(q) : null, JsonDocument.Parse("{}").RootElement),
            new InventoryNameAssociation(new(category, new(type)), name, name, name is null ? InventoryNameState.Unknown : InventoryNameState.ClientCatalog, null),
            true, DateTimeOffset.UnixEpoch, ObservationQuality.Confirmed, CharacterId: character);

    private static InventoryCoverage Cover(string category, InventoryContainer container, bool complete = true) => new(category, container, complete);

    private static InventoryAccountView AccountView(IReadOnlyList<InventoryCoverage>? shared = null) => new(Account, "主帳號", new(7),
        [
            new(Identity("c1", "琉特"), DateTimeOffset.UnixEpoch,
                [Cover("consumable", InventoryContainer.Backpack), Cover("consumable", InventoryContainer.CharacterStorage)], true),
            new(Identity("c2", "艾薇"), DateTimeOffset.UnixEpoch,
                [Cover("consumable", InventoryContainer.Backpack), Cover("consumable", InventoryContainer.CharacterStorage, false)], false)
        ],
        [new("c1", "琉特", "琉特"), new("c2", "艾薇", "艾薇"), new("c3", "諾娃", "諾娃")], true, [], shared ?? []);

    [AvaloniaFact]
    public void PivotWritesZeroOnlyWhenTheContainerWasReadCompletely()
    {
        InventoryAccountItems Items(IReadOnlyList<InventoryCoverage>? shared) => new(AccountView(shared),
            new Dictionary<string, IReadOnlyList<InventoryItemView>>
            {
                ["c1"] = [Item("consumable", InventoryContainer.Backpack, 5101, 30, "生命藥水"), Item("consumable", InventoryContainer.Backpack, 5101, 7, "生命藥水")],
                ["c2"] = [Item("consumable", InventoryContainer.Backpack, 5102, null, "染色安瓶")]
            },
            [Item("consumable", InventoryContainer.AccountStorage, 5101, 2, "生命藥水")]);

        // 公共保管箱的 0 与“未读取”按账号视图的 sharedCoverage 判定，任何账号都适用。
        var (characters, rows) = InventoryPivotBuilder.Build(Items([Cover("consumable", InventoryContainer.AccountStorage)]));

        // 已读取的角色在前，角色名单里尚未读取的角色排在后面。
        Assert.Equal(["琉特", "艾薇", "諾娃"], characters.Select(c => c.Name));
        var potion = rows.Single(row => row.TypeKey == "consumable:5101");
        Assert.Equal(["37", "0", "未读取"], potion.Backpack.Select(cell => cell.Text));
        // 艾薇的角色保管箱读取不完整：没有这件道具时写“未读取”，不写 0。
        Assert.Equal(["0", "未读取", "未读取"], potion.Storage.Select(cell => cell.Text));
        Assert.Equal("2", potion.Shared.Text);
        Assert.Equal("39", potion.Total.Text);
        Assert.True(potion.Total.Dim);

        var ampoule = rows.Single(row => row.TypeKey == "consumable:5102");
        Assert.Equal(InventoryCountState.Unknown, ampoule.Backpack[1].State);
        Assert.Equal("0", ampoule.Shared.Text);

        // sharedCoverage 为空表示从未读取：没有的道具记为未读取；对应类别不完整时同样是未读取。
        var (_, unknownShared) = InventoryPivotBuilder.Build(Items([]));
        Assert.Equal("未读取", unknownShared.Single(row => row.TypeKey == "consumable:5102").Shared.Text);
        Assert.Equal("2", unknownShared.Single(row => row.TypeKey == "consumable:5101").Shared.Text);
        var (_, partialShared) = InventoryPivotBuilder.Build(Items([Cover("consumable", InventoryContainer.AccountStorage, false)]));
        Assert.Equal("未读取", partialShared.Single(row => row.TypeKey == "consumable:5102").Shared.Text);
    }

    [AvaloniaFact]
    public void EachScoreIsOneRowNamedByItsTitleWithComposerInTheTip()
    {
        // 合成标题与作曲者。两张自制谱共用表编号 0，各有实例 ID。
        InventoryItemView Score(ulong instance, string? title, string composer, string character) =>
            new(new InventoryItem("MusicNoteItem", InventoryContainer.Backpack, "score-" + instance, new(0), new(instance), new(1), JsonDocument.Parse("{}").RootElement),
                new InventoryNameAssociation(new("MusicNoteItem", new(0)), title, title, title is null ? InventoryNameState.Unknown : InventoryNameState.ItemInstance, null),
                true, DateTimeOffset.UnixEpoch, ObservationQuality.Confirmed, CharacterId: character,
                Score: new(new(0), title, composer, 1, null, null, true, false, null, null));
        var account = AccountView() with { Characters = [.. AccountView().Characters.Select(c => c with { Coverage = [Cover("MusicNoteItem", InventoryContainer.Backpack)] })] };
        var (_, rows) = InventoryPivotBuilder.Build(new(account,
            new Dictionary<string, IReadOnlyList<InventoryItemView>> { ["c1"] = [Score(21, "合成乙谱", "合成作曲者", "c1"), Score(23, null, "合成作曲者", "c1")], ["c2"] = [Score(22, "合成甲谱", "另一作曲者", "c2")] }, []));

        // 每张一行，按显示名排序；标题为空的写明“标题未知”。
        Assert.Equal(["合成乙谱", "合成甲谱", "自制乐谱 · 标题未知"], rows.Select(row => row.Name));
        var first = rows.Single(row => row.Name == "合成乙谱");
        Assert.Equal(["1", "0", "未读取"], first.Backpack.Select(cell => cell.Text));
        Assert.Equal(["0", "1", "未读取"], rows.Single(row => row.Name == "合成甲谱").Backpack.Select(cell => cell.Text));
        Assert.Contains("乐谱实例标题", first.Tip);
        Assert.Contains("作曲：合成作曲者", first.Tip);
        Assert.Contains("上锁：未知", first.Tip);
        Assert.Contains("该乐谱实例没有标题", rows.Single(row => row.Name == "自制乐谱 · 标题未知").Tip);
        Assert.True(rows.Single(row => row.Name == "合成甲谱").Matches("另一作曲"));
        Assert.False(first.Matches("另一作曲"));
    }

    [Fact]
    public async Task ReadingAnAccountQueriesOnceAndSplitsItemsByCharacter()
    {
        var queries = new List<InventoryItemQuery>();
        Task<InventoryItemPage> Query(InventoryItemQuery query, CancellationToken _)
        {
            queries.Add(query);
            IReadOnlyList<InventoryItemView> page = query.Cursor is null
                ? [Item("consumable", InventoryContainer.Backpack, 1, 1, character: "c1"), Item("consumable", InventoryContainer.AccountStorage, 9, 3)]
                : [Item("consumable", InventoryContainer.CharacterStorage, 2, 4, character: "c1"), Item("consumable", InventoryContainer.Backpack, 3, 5, character: "c2")];
            return Task.FromResult(new InventoryItemPage(page, query.Cursor is null ? "next-1" : null, new(7)));
        }

        var items = await InventoryPivotBuilder.ReadAsync(AccountView(), Query, CancellationToken.None);

        // 不带角色编号按账号查询，按游标读完；条目按 characterId 分列，公共保管箱条目没有角色编号。
        Assert.Equal([(Account, (string?)null, (string?)null), (Account, null, "next-1")],
            queries.Select(query => (query.AccountId, query.CharacterId, query.Cursor)));
        Assert.All(queries, query => Assert.Equal(InventoryPivotBuilder.PageSize, query.PageSize));
        Assert.Equal(2, items.ByCharacter["c1"].Count);
        Assert.Single(items.ByCharacter["c2"]);
        Assert.Single(items.Shared);
    }

    [AvaloniaFact]
    public void SwitchingAccountSendsTheQueryAndRemembersTheChoice()
    {
        InventoryPreference.Restore(null, null, null);
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "inventory");
        try
        {
            var model = (InventoryPageModel)Page<InventoryPage>(window).DataContext!;
            Assert.Equal("account-1", model.SelectedAccount?.Id);
            Assert.Equal(2, Named<ComboBox>(window, "AccountBox").ItemCount);
            Assert.Contains(model.Columns, column => column is { Id: "backpack:c2", Header: "艾薇" });

            model.SelectedAccount = model.Data.Accounts.Single(account => account.Id == "account-2");
            LayoutProbe.Settle();

            Assert.Equal(("inventory.account", "account-2"), backend.UiIntents[^1]);
            Assert.Equal("account-2", InventoryPreference.Account);
            Assert.Equal(["storage:c3", "backpack:c3"], model.Columns.Where(column => column.Id.Contains(':')).Select(column => column.Id));

            // 再打开的页面按记下的账号请后端读取；后端已在读这个账号时不再发送。
            var other = new SampleBackend();
            using (new InventoryPageModel(other)) { }
            Assert.Equal(("inventory.account", "account-2"), other.UiIntents[^1]);
            var count = backend.UiIntents.Count;
            using (new InventoryPageModel(backend)) { }
            Assert.Equal(count, backend.UiIntents.Count);
        }
        finally
        {
            window.Close();
            InventoryPreference.Restore(null, null, null);
        }
    }

    [AvaloniaFact]
    public void CategoriesShowTheirDisplayNamesForSearchAndExport()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "inventory");
        try
        {
            Assert.Equal("宝石·符文", InventoryCategoryNames.Display("Socketable"));
            Assert.Equal("观赏鱼", InventoryCategoryNames.Display("AquariumFish"));
            // 没有登记的代码原样显示。
            Assert.Equal("JamItem", InventoryCategoryNames.Display("JamItem"));

            var model = (InventoryPageModel)Page<InventoryPage>(window).DataContext!;
            var category = model.Columns.Single(column => column.Id == "category");
            Assert.All(model.Rows.Where(row => row.Category == "instrument"), row => Assert.Equal("乐器", category.TextOf(row)));
            // 搜索按显示名查找。
            model.Search = "乐器";
            Assert.NotEmpty(model.Rows);
            Assert.All(model.Rows, row => Assert.Equal("instrument", row.Category));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void GroupTogglesHideColumnGroups()
    {
        InventoryPreference.Restore(null, null, null);
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "inventory");
        try
        {
            var model = (InventoryPageModel)Page<InventoryPage>(window).DataContext!;
            model.ToggleGroupCommand.Execute(InventoryPreference.BackpackGroup);
            LayoutProbe.Settle();
            Assert.DoesNotContain(model.Columns, column => column.Id.StartsWith("backpack:", StringComparison.Ordinal));
            Assert.Contains(model.Columns, column => column.Id == "storage:c1");
            Assert.False(model.Groups.Single(group => group.Id == InventoryPreference.BackpackGroup).Shown);
            var table = Named<DataTable>(window, "ItemsTable");
            Assert.DoesNotContain(table.VisibleColumns, column => column.Id.StartsWith("backpack:", StringComparison.Ordinal));
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally
        {
            window.Close();
            InventoryPreference.Restore(null, null, null);
        }
    }

    [Fact]
    public void NotesAreSavedPerAccountAndSurviveRestart()
    {
        var directory = Directory.CreateTempSubdirectory("moongate-notes-");
        try
        {
            var path = Path.Combine(directory.FullName, "inventory-notes.json");
            var store = new InventoryNoteStore(path);
            store.Set("a1", "consumable:5101", "留給艾薇");
            store.Set("a2", "consumable:5101", "另一個帳號");
            store.Set("a1", "material:1", "  ");

            var reopened = new InventoryNoteStore(path);
            Assert.Equal("留給艾薇", reopened.Get("a1", "consumable:5101"));
            Assert.Equal("另一個帳號", reopened.Get("a2", "consumable:5101"));
            Assert.Equal(string.Empty, reopened.Get("a1", "material:1"));
            Assert.False(File.Exists(path + ".tmp"));

            // 清空即删除；文件损坏时为空，不影响使用。
            reopened.Set("a1", "consumable:5101", string.Empty);
            Assert.Equal(string.Empty, new InventoryNoteStore(path).Get("a1", "consumable:5101"));
            File.WriteAllText(path, "{ not json");
            var broken = new InventoryNoteStore(path);
            Assert.Equal(string.Empty, broken.Get("a2", "consumable:5101"));
            broken.Set("a2", "x:1", "重新寫入");
            Assert.Equal("重新寫入", new InventoryNoteStore(path).Get("a2", "x:1"));
        }
        finally { directory.Delete(true); }
    }

    [AvaloniaFact]
    public void EditingTheNoteColumnSavesItAndSearchFindsIt()
    {
        InventoryPreference.Restore(null, null, null);
        var saved = InventoryNoteStore.Shared;
        InventoryNoteStore.Shared = new InventoryNoteStore();
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "inventory");
        try
        {
            var model = (InventoryPageModel)Page<InventoryPage>(window).DataContext!;
            var table = Named<DataTable>(window, "ItemsTable");
            var first = (InventoryPivotRow)table.View[0];
            Click(window, table.List.ContainerFromIndex(0)!);
            PressKey(window, Key.F2, PhysicalKey.F2);
            var editor = table.GetVisualDescendants().OfType<TextBox>().Single(box => box.Classes.Contains("tableedit"));
            editor.Text = "交易用";
            PressKey(window, Key.Enter, PhysicalKey.Enter);

            Assert.Equal("交易用", model.Note(first));
            Assert.Equal("交易用", InventoryNoteStore.Shared.Get("account-1", first.TypeKey));
            Assert.DoesNotContain(table.GetVisualDescendants().OfType<TextBox>(), box => box.Classes.Contains("tableedit"));

            // 备注参与搜索、复制与导出。
            model.Search = "交易";
            LayoutProbe.Settle();
            Assert.Equal([first.TypeKey], model.Rows.Select(row => row.TypeKey));
            var csv = System.Text.Encoding.UTF8.GetString(table.ExportBytes());
            Assert.Contains("交易用", csv);

            // Esc 取消编辑，不保存。
            model.Search = string.Empty;
            LayoutProbe.Settle();
            Click(window, table.List.ContainerFromIndex(0)!);
            PressKey(window, Key.F2, PhysicalKey.F2);
            table.GetVisualDescendants().OfType<TextBox>().Single(box => box.Classes.Contains("tableedit")).Text = "不要";
            PressKey(window, Key.Escape, PhysicalKey.Escape);
            Assert.DoesNotContain(model.Data.Rows, row => model.Note(row) == "不要");
        }
        finally
        {
            window.Close();
            InventoryNoteStore.Shared = saved;
            InventoryPreference.Restore(null, null, null);
        }
    }

    /// <summary>
    /// 补充倒计时按“上次补充时间 + n × 间隔”逐秒递减；到零后接着倒数下一次并标“待进图”（游戏进图时才结算），已到的次数随结果给出。
    /// </summary>
    [AvaloniaFact]
    public void RefillCountdownTicksEverySecondAndKeepsCountingAfterZero()
    {
        var last = new DateTimeOffset(2026, 11, 15, 8, 12, 49, TimeSpan.Zero);
        var silver = new InventoryRefill(last, 1800);
        Assert.Equal(("29:59", 0), InventoryRefillText.Countdown(silver, last.AddSeconds(1)));
        Assert.Equal(("29:58", 0), InventoryRefillText.Countdown(silver, last.AddSeconds(2)));
        Assert.Equal(("00:01", 0), InventoryRefillText.Countdown(silver, last.AddSeconds(1799)));
        // 到零之后：进图结算前数值不变，倒计时转到下一次。
        Assert.Equal(("待进图 15:00", 1), InventoryRefillText.Countdown(silver, last.AddSeconds(2700)));
        Assert.Equal(("待进图 29:59", 2), InventoryRefillText.Countdown(silver, last.AddSeconds(3601)));
        // 十二小时的魔族貢品按 时:分:秒。
        Assert.Equal(("11:59:59", 0), InventoryRefillText.Countdown(new InventoryRefill(last, 43200), last.AddSeconds(1)));
        Assert.Equal((string.Empty, 0), InventoryRefillText.Countdown(null, last));
    }

    /// <summary>资源卡的主货币（金币、银币、魔族貢品）与角色资源读同一份资源视图、跟随同一个角色选择：一次刷新两处同时变化。</summary>
    [AvaloniaFact]
    public void SummaryAndResourceRowsUpdateTogetherFromOneResourceView()
    {
        InventoryPreference.Restore(null, null, null);
        var last = DateTimeOffset.Now.AddMinutes(-10);
        InventoryResourceView View(string key, string name, ulong value, string character, DateTimeOffset? refilled = null, double? interval = null) =>
            new(key, key, "observed", null, name, null, new(value), null, null, DateTimeOffset.Now, refilled, interval, null, null, true, true,
                null, InventoryResourceScope.Character, character);
        InventoryResourcesView Resources(ulong silver, ulong heart) => new("account-1", "c1", "琉特", true, new(1),
        [
            View("goldFree", "金幣", 1000, "c1"), View("goldPaid", "金幣", 20, "c1"),
            View("silverCoin", "銀幣", silver, "c1", last, 1800), View("passDeep", "魔族貢品", 1, "c1", last, 43200),
            View("heartToken", "愛心幣", heart, "c1"),
            View("goldFree", "金幣", 7, "c2"), View("goldPaid", "金幣", 0, "c2"),
        ]);
        var backend = new SampleBackend { ResourcesOverride = Resources(63, 526) };
        using var model = new InventoryPageModel(backend);

        Assert.Equal(["1,020", "63", "1"], model.MainCurrencies.Select(row => row.Value));
        Assert.Equal(new InventoryRefill(last, 1800), model.MainCurrencies[1].Refill);
        Assert.Equal("526", model.CharacterResources.Single(row => row.Key == "heartToken").Value);
        // 金币、银币、贡品只在主货币，角色资源组不重复列出。
        Assert.DoesNotContain(model.CharacterResources, row => row.Key is "goldTotal" or "silverCoin" or "passDeep");

        backend.ResourcesOverride = Resources(64, 528);
        backend.RaiseChanged();
        Assert.Equal("64", model.MainCurrencies[1].Value);
        Assert.Equal("528", model.CharacterResources.Single(row => row.Key == "heartToken").Value);

        // 切换资源卡的角色：主货币三项随之切换，该角色没有的项为未读取（不是 0）。
        model.SelectResourceCharacterCommand.Execute("c2");
        Assert.Equal(["7", "未读取", "未读取"], model.MainCurrencies.Select(row => row.Value));
        Assert.StartsWith("艾薇", model.ResourceCharacterLabel);
        InventoryPreference.Restore(null, null, null);
    }

    /// <summary>
    /// 命令行货币只对当前登录角色按游戏原文名称并入：一行对应时取命令行、免费与付费两行合成一行、命令行独有的追加；其他角色与读不到命令行时只取抓包。
    /// 补充倒计时沿用抓包，悬停写明来源。
    /// </summary>
    [AvaloniaFact]
    public void CliCurrenciesTakePriorityForTheLoggedInCharacterAndCaptureFillsTheRest()
    {
        InventoryPreference.Restore(null, null, null);
        var last = DateTimeOffset.Now.AddMinutes(-10);
        InventoryResourceView View(string key, string name, ulong value, string? character, InventoryResourceScope scope = InventoryResourceScope.Character,
            DateTimeOffset? refilled = null, double? interval = null) =>
            new(key, key, "observed", null, name, null, new(value), null, null, DateTimeOffset.Now, refilled, interval, null, null, true, true,
                null, scope, character);
        var resources = new InventoryResourcesView("account-1", "c1", "琉特", true, new(1),
        [
            View("goldFree", "金幣", 1000, "c1"), View("goldPaid", "金幣", 20, "c1"),
            View("silverCoin", "銀幣", 63, "c1", refilled: last, interval: 1800),
            View("wecaFree", "韋卡", 120, null, InventoryResourceScope.Account), View("wecaPaid", "韋卡", 30, null, InventoryResourceScope.Account),
            View("silverCoin", "銀幣", 40, "c2")
        ]);
        GameCurrenciesData Cli(GameDataSource source) => new(new(source, null, DateTimeOffset.Now, false, []),
            [new("銀幣", 64), new("韋卡", 150), new("喵幣", 9)]);
        var backend = new SampleBackend { ResourcesOverride = resources, CurrenciesOverride = Cli(GameDataSource.Connector) };
        using var model = new InventoryPageModel(backend);

        var silver = model.MainCurrencies[1];
        Assert.Equal(("64", GameDataSource.Connector), (silver.Value, silver.Source));
        Assert.Equal(new InventoryRefill(last, 1800), silver.Refill);
        Assert.Contains("来源：游戏连接器", silver.Tip);
        Assert.Contains("抓包：63", silver.Tip);
        // 金币命令行没给：照抓包合计，并写明来源。
        Assert.Equal(("1,020", GameDataSource.Capture), (model.MainCurrencies[0].Value, model.MainCurrencies[0].Source));
        Assert.EndsWith("来源：抓包", model.MainCurrencies[0].Tip);
        // 免费与付费合成一行，分项写进悬停提示。
        var weca = Assert.Single(model.AccountResources);
        Assert.Equal(("韦卡", "150"), (weca.Name, weca.Value));
        Assert.Contains("韦卡（免费）：120", weca.Tip);
        Assert.Contains("韦卡（付费）：30", weca.Tip);
        // 命令行独有的追加为当前角色的一项。
        Assert.Equal("9", model.CharacterResources.Single(row => row.RawName == "喵幣").Value);

        // 其他角色只取抓包。
        model.SelectResourceCharacterCommand.Execute("c2");
        Assert.Equal(("40", GameDataSource.Capture), (model.MainCurrencies[1].Value, model.MainCurrencies[1].Source));
        Assert.DoesNotContain(model.CharacterResources, row => row.RawName == "喵幣");

        // 命令行没读到（回落抓包的结果同样不算命令行）：全部取抓包。
        backend.CurrenciesOverride = Cli(GameDataSource.Capture);
        backend.RaiseChanged();
        model.SelectResourceCharacterCommand.Execute("c1");
        Assert.Equal(("63", GameDataSource.Capture), (model.MainCurrencies[1].Value, model.MainCurrencies[1].Source));
        Assert.Equal(2, model.AccountResources.Count);
        InventoryPreference.Restore(null, null, null);
    }
}
