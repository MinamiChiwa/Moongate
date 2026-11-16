using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Controls.Tables;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Theme;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>通用表格：排序、筛选、检索的匹配规则，列顺序合并，复制与 CSV 的文字格式，列设置的保存与恢复，快捷键。</summary>
public sealed class TableTests
{
    private sealed record Row(string Name, string Amount, string Note = "");

    private static TableColumn NameColumn => new() { Id = "name", Header = "名稱", Path = nameof(Row.Name) };
    private static TableColumn AmountColumn => new() { Id = "amount", Header = "數量", Path = nameof(Row.Amount), Kind = ColumnKind.Number };
    private static TableColumn NoteColumn => new() { Id = "note", Header = "備註", Path = nameof(Row.Note) };

    private static readonly Row[] Rows =
    [
        new("Drum", "1,200"), new("豎琴", "未读取"), new("魯特琴", "35"), new("Lute", "不适用"), new("Potion", "1,200", "x")
    ];

    [Fact]
    public void NumbersSortByValueAndNonNumbersStayLast()
    {
        Assert.Equal(1200, TableQuery.Number("1,200"));
        Assert.Equal(12.5, TableQuery.Number("12.5%"));
        Assert.Equal(185, TableQuery.Number("3:05"));
        Assert.Null(TableQuery.Number("未读取"));
        Assert.Null(TableQuery.Number("不适用"));

        var columns = new[] { NameColumn, AmountColumn };
        var ascending = TableQuery.Apply(Rows, columns, new Dictionary<string, ColumnFilter>(), [new("amount", false)]).Cast<Row>();
        Assert.Equal(["魯特琴", "Drum", "Potion", "豎琴", "Lute"], ascending.Select(row => row.Name));
        var descending = TableQuery.Apply(Rows, columns, new Dictionary<string, ColumnFilter>(), [new("amount", true)]).Cast<Row>();
        // 降序时非数值仍在最后；数值相同的两行按第二个排序键（名称降序）。
        var twoKeys = TableQuery.Apply(Rows, columns, new Dictionary<string, ColumnFilter>(), [new("amount", true), new("name", true)]).Cast<Row>();
        Assert.Equal(["Drum", "Potion", "魯特琴", "豎琴", "Lute"], descending.Select(row => row.Name));
        Assert.Equal(["Potion", "Drum", "魯特琴"], twoKeys.Take(3).Select(row => row.Name));
        Assert.Equal(["Lute", "豎琴"], twoKeys.Skip(3).Select(row => row.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FiltersAndSearchFollowTheirMatchRules()
    {
        var columns = new[] { NameColumn, AmountColumn, NoteColumn };
        var none = new Dictionary<string, ColumnFilter>();
        // 文字“包含”不区分大小写；数字范围只接受有数值的行。
        Assert.Equal(["Lute"], TableQuery.Apply(Rows, columns, new Dictionary<string, ColumnFilter> { ["name"] = new("lu") }, []).Cast<Row>().Select(row => row.Name));
        Assert.Equal(["Drum", "Potion"], TableQuery.Apply(Rows, columns, new Dictionary<string, ColumnFilter> { ["amount"] = new(null, 100, null) }, [])
            .Cast<Row>().Select(row => row.Name));
        Assert.Equal(["魯特琴"], TableQuery.Apply(Rows, columns, new Dictionary<string, ColumnFilter> { ["amount"] = new(null, null, 100) }, [])
            .Cast<Row>().Select(row => row.Name));
        Assert.Equal(5, TableQuery.Apply(Rows, columns, none, []).Count);

        // 检索在所有可见列里查找；空检索词没有匹配。
        Assert.True(TableQuery.Matches(Rows[4], columns, " X "));
        Assert.False(TableQuery.Matches(Rows[4], [NameColumn, AmountColumn], "x"));
        Assert.False(TableQuery.Matches(Rows[0], columns, "  "));
        Assert.True(TableQuery.Matches(Rows[1], columns, "未读"));
    }

    [Fact]
    public void SavedOrderKeepsUserChoiceAndPlacesNewColumnsByDefaultNeighbour()
    {
        Assert.Equal(["a", "b", "c"], TableQuery.ResolveOrder(["a", "b", "c"], null));
        Assert.Equal(["c", "a", "b"], TableQuery.ResolveOrder(["a", "b", "c"], ["c", "a", "b"]));
        // 新角色列 x 默认在 b 之后：跟在用户顺序里的 b 后面；已不存在的列 gone 被丢弃。
        Assert.Equal(["c", "b", "x", "a"], TableQuery.ResolveOrder(["a", "b", "x", "c"], ["c", "gone", "b", "a"]));
        Assert.Equal(["first", "c", "a"], TableQuery.ResolveOrder(["first", "a", "c"], ["c", "a"]));
    }

    [Fact]
    public void CopyTextIsTabSeparatedAndCsvIsRfc4180WithBom()
    {
        var columns = new[] { NameColumn, AmountColumn, NoteColumn };
        var rows = new[] { new Row("A\tB", "1,200", "第一行\n第二行"), new Row("C", "2", "说 \"好\"") };
        Assert.Equal("名稱\t數量\t備註\r\nA B\t1,200\t第一行 第二行\r\nC\t2\t说 \"好\"",
            TableQuery.Tsv(rows, columns, column => column.Header!));

        var csv = TableQuery.CsvBytes(rows, columns, column => column.Header!);
        Assert.Equal(Encoding.UTF8.GetPreamble(), csv[..3]);
        Assert.Equal("名稱,數量,備註\r\nA\tB,\"1,200\",\"第一行\n第二行\"\r\nC,2,\"说 \"\"好\"\"\"\r\n",
            Encoding.UTF8.GetString(csv[3..]));
        Assert.Equal("\" 前後空白 \"", TableQuery.Csv([new Row(" 前後空白 ", "")], [NameColumn], _ => "n").Split("\r\n")[1]);
        Assert.Equal("背包-a_b-20261113", TableQuery.SafeFileName("背包-a/b-20261113"));
    }

    [Fact]
    public void TableSettingsSurviveThePreferenceFile()
    {
        var directory = Directory.CreateTempSubdirectory("moongate-tables-");
        try
        {
            var store = new DesktopPreferenceStore(Path.Combine(directory.FullName, "preferences.json"));
            var settings = new TableSettings(["amount", "name"], new Dictionary<string, double> { ["name"] = 260 }, ["note"],
                [new("amount", true), new("name", false)], new Dictionary<string, string> { ["note"] = "交易備忘" }, ["source"]);
            store.Save(DesktopPreferences.Default with
            {
                Tables = new Dictionary<string, TableSettings> { ["inventory.items"] = settings },
                InventoryAccount = "acct-2", InventoryHiddenGroups = ["backpack"],
                InventoryWindow = new(120, 80, 1000, 640, true)
            });
            var loaded = store.Load();
            var table = loaded.Tables!["inventory.items"];
            Assert.Equal(settings.Order, table.Order);
            Assert.Equal(260, table.Widths!["name"]);
            Assert.Equal(["note"], table.Hidden);
            Assert.Equal(settings.Sort, table.Sort);
            Assert.Equal("交易備忘", table.Titles!["note"]);
            Assert.Equal(["source"], table.Shown);
            Assert.Equal("acct-2", loaded.InventoryAccount);
            Assert.Equal(["backpack"], loaded.InventoryHiddenGroups);
            Assert.Equal(new(120, 80, 1000, 640, true), loaded.InventoryWindow);

            // 旧文件没有这些字段时为默认值。
            File.WriteAllText(Path.Combine(directory.FullName, "preferences.json"), "{\"Language\":\"TraditionalChinese\"}");
            Assert.Null(store.Load().Tables);
            Assert.Null(store.Load().InventoryWindow);
        }
        finally { directory.Delete(true); }
    }

    private static (Window Window, DataTable Table) Host(string id, IReadOnlyList<Row> rows, Action<DataTable>? setup = null)
    {
        UiText.Apply(Avalonia.Application.Current!, UiLanguage.SimplifiedChinese);
        UiMotion.Enabled = false;
        var table = new DataTable { TableId = id, BodyMaxHeight = 300 };
        table.Columns.Add(new TableColumn { Id = "name", Header = "名稱", Path = nameof(Row.Name), Fill = true });
        table.Columns.Add(new TableColumn { Id = "amount", Header = "數量", Path = nameof(Row.Amount), Kind = ColumnKind.Number });
        table.Columns.Add(new TableColumn
        {
            Id = "note", Header = "備註", Path = nameof(Row.Note), Editable = true, Renamable = true
        });
        setup?.Invoke(table);
        table.ItemsSource = rows;
        var window = new Window { Width = 700, Height = 500, Content = table };
        window.Show();
        LayoutProbe.Settle();
        LayoutProbe.Render(window);
        return (window, table);
    }

    [AvaloniaFact]
    public void ColumnSettingsAreSharedByTablesWithTheSameId()
    {
        var id = "test." + Guid.NewGuid().ToString("N");
        var (first, a) = Host(id, Rows);
        var (second, b) = Host(id, Rows);
        try
        {
            a.SetWidth("amount", 180, true);
            a.MoveColumn("note", "name");
            a.ToggleSort(a.VisibleColumns.Single(column => column.Id == "amount"), false);
            a.ToggleSort(a.VisibleColumns.Single(column => column.Id == "name"), true);
            a.SetColumnShown(a.VisibleColumns.Single(column => column.Id == "name"), false);
            a.Rename(a.Columns.Single(column => column.Id == "note"), "交易備忘");
            LayoutProbe.Settle();

            Assert.Equal(["note", "amount"], b.VisibleColumns.Select(column => column.Id));
            Assert.Equal(180, b.Slots.Single(slot => slot.Column.Id == "amount").Width);
            Assert.Equal([new TableSort("amount", false), new TableSort("name", false)], b.Sort);
            Assert.Equal("交易備忘", b.HeaderText(b.Columns.Single(column => column.Id == "note")));
            Assert.Equal(["魯特琴", "Drum", "Potion"], b.View.Cast<Row>().Take(3).Select(row => row.Name));

            // 恢复默认列：顺序、显隐与列宽回到默认，排序与列名保留。
            b.ResetColumns();
            LayoutProbe.Settle();
            Assert.Equal(["name", "amount", "note"], a.VisibleColumns.Select(column => column.Id));
            Assert.Equal(120, a.Slots.Single(slot => slot.Column.Id == "amount").Width);
            Assert.Equal(2, a.Sort.Count);
        }
        finally
        {
            first.Close();
            second.Close();
            TableSettingsStore.Set(id, TableSettings.Empty);
        }
    }

    [AvaloniaFact]
    public void KeyboardSelectsCopiesAndSearches()
    {
        var (window, table) = Host("test." + Guid.NewGuid().ToString("N"), Rows);
        try
        {
            var copied = new List<string>();
            table.Copied += (_, text) => copied.Add(text);
            Click(window, table.List.ContainerFromIndex(0)!);
            PressKey(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
            PressKey(window, Key.C, PhysicalKey.C, RawInputModifiers.Control);
            Assert.Equal("名稱\t數量\t備註\r\nDrum\t1,200\t\r\n豎琴\t未读取\t", copied[^1]);

            PressKey(window, Key.End, PhysicalKey.End);
            PressKey(window, Key.C, PhysicalKey.C, RawInputModifiers.Control);
            Assert.Equal("名稱\t數量\t備註\r\nPotion\t1,200\tx", copied[^1]);

            PressKey(window, Key.A, PhysicalKey.A, RawInputModifiers.Control);
            Assert.Equal(5, table.SelectedRows.Count);

            // Ctrl+F 打开检索框；匹配行高亮，Enter／F3 跳到下一个，Shift+F3 回到上一个，Esc 关闭。
            PressKey(window, Key.F, PhysicalKey.F, RawInputModifiers.Control);
            Assert.True(table.SearchOpen);
            table.SearchText = "1,200";
            LayoutProbe.Settle();
            Assert.Equal([0, 4], table.MatchIndices);
            Assert.Equal(0, table.CurrentMatch);
            PressKey(window, Key.F3, PhysicalKey.F3);
            Assert.Equal(4, table.CurrentMatch);
            Assert.Equal([Rows[4]], table.SelectedRows);
            PressKey(window, Key.F3, PhysicalKey.F3, RawInputModifiers.Shift);
            Assert.Equal(0, table.CurrentMatch);
            Assert.Contains("currentmatch", table.List.ContainerFromIndex(0)!.Classes);

            // 检索与筛选同时生效。
            table.SetFilter("name", new ColumnFilter("pot"));
            LayoutProbe.Settle();
            Assert.Equal([0], table.MatchIndices);
            PressKey(window, Key.Escape, PhysicalKey.Escape);
            Assert.False(table.SearchOpen);
            Assert.Empty(table.MatchIndices);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RefreshUpdatesRowsInPlaceAndKeepsTheRealizedRowContainers()
    {
        var (window, table) = Host("test." + Guid.NewGuid().ToString("N"), Rows);
        try
        {
            var source = table.List.ItemsSource;
            var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            ((System.Collections.Specialized.INotifyCollectionChanged)source!).CollectionChanged += (_, e) => actions.Add(e.Action);
            var items = table.List.Items.Cast<object>().ToList();
            var containers = Enumerable.Range(0, Rows.Length).Select(i => table.List.ContainerFromIndex(i)).ToList();
            Click(window, table.List.ContainerFromIndex(2)!);
            // 指针停在第 2 行上（索引 1）。
            window.MouseMove(Center(containers[1]!, window));
            LayoutProbe.Render(window);
            Assert.True(containers[1]!.IsPointerOver);
            // 每秒刷新给出新的行对象：行数不变时表体集合不发任何事件，各位置的项与行容器都是原来的实例，
            // 变了的两行只换容器里的行对象；悬停仍在指针所在的那一行，选择按行标识保留。
            table.RowKey = row => ((Row)row).Name;
            var next = Rows.Select((row, i) => i is 0 or 2 ? row with { Amount = "9,999" } : row).ToList();
            table.ItemsSource = next;
            LayoutProbe.Render(window);
            Assert.Same(source, table.List.ItemsSource);
            Assert.Empty(actions);
            Assert.Equal(items, table.List.Items.Cast<object>());
            Assert.Equal(["9,999", "未读取", "9,999"], table.View.Take(3).Cast<Row>().Select(row => row.Amount));
            for (var i = 0; i < Rows.Length; i++)
            {
                Assert.Same(containers[i], table.List.ContainerFromIndex(i));
                Assert.Same(next[i], containers[i]!.DataContext);
                Assert.Equal(i == 1, containers[i]!.IsPointerOver);
            }
            Assert.Equal(["Drum", "9,999", ""], RowPresenter(containers[0]!).CellTexts.Select(cell => cell.Text));
            Assert.Equal(["魯特琴"], table.SelectedRows.Cast<Row>().Select(row => row.Name));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void StableItemsFollowsAFixedObservableCollectionInPlace()
    {
        // 页面模型持有固定的 StableList、只就地改内容：绑在 StableItems.Source 上的列表随之更新，未变的项容器保留。
        var rows = new StableList<string>();
        rows.Sync(["甲", "乙", "丙"]);
        var list = new ItemsControl();
        StableItems.SetSource(list, rows);
        var window = new Window { Width = 300, Height = 300, Content = list };
        window.Show();
        LayoutProbe.Settle();
        try
        {
            var first = list.ContainerFromIndex(0);
            var third = list.ContainerFromIndex(2);
            rows.Sync(["甲", "乙乙", "丙", "丁"]);
            LayoutProbe.Settle();
            Assert.Equal(["甲", "乙乙", "丙", "丁"], list.Items.Cast<string>());
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "丁");
            Assert.Same(first, list.ContainerFromIndex(0));
            Assert.Same(third, list.ContainerFromIndex(2));

            // 每次给出新列表的用法照旧按位同步到同一个内部集合。
            var other = new ItemsControl();
            StableItems.SetSource(other, new[] { "一", "二" });
            var source = other.ItemsSource;
            StableItems.SetSource(other, new[] { "一", "三" });
            Assert.Same(source, other.ItemsSource);
            Assert.Equal(["一", "三"], other.Items.Cast<string>());
        }
        finally { window.Close(); }
    }

    private static TableRowPresenter RowPresenter(Control container) => container.GetVisualDescendants().OfType<TableRowPresenter>().First();

    [AvaloniaFact]
    public void DrawnRowsFollowColumnsAndExposeColumnText()
    {
        // 文字列由行自己画：“按技能／按目标”一类切换（表标识、列与行同一轮变化）只排一次，行容器与行控件不重建，各列文字随之更新。
        var (window, table) = Host("test." + Guid.NewGuid().ToString("N"), Rows);
        try
        {
            var container = table.List.ContainerFromIndex(0)!;
            var row = RowPresenter(container);
            Assert.Empty(container.GetVisualDescendants().OfType<TextBlock>());
            var layouts = 0;
            table.LayoutChanged += (_, _) => layouts++;
            table.TableId = "test." + Guid.NewGuid().ToString("N");
            table.ColumnsSource =
            [
                new TableColumn { Id = "target", Header = "目標", Path = nameof(Row.Name), Fill = true },
                new TableColumn { Id = "amount", Header = "數量", Path = nameof(Row.Amount), Kind = ColumnKind.Number },
                new TableColumn { Id = "note", Header = "備註", Path = nameof(Row.Note) }
            ];
            table.ItemsSource = Rows.Reverse().ToList();
            LayoutProbe.Render(window);
            Assert.Equal(1, layouts);
            Assert.Same(container, table.List.ContainerFromIndex(0));
            Assert.Same(row, RowPresenter(container));
            Assert.Equal(["Potion", "1,200", "x"], row.CellTexts.Select(cell => cell.Text));

            // 列宽与隐藏照常生效。
            table.SetWidth("amount", 200, false);
            LayoutProbe.Render(window);
            Assert.Equal(200, row.CellBounds("amount")!.Value.Width);
            table.SetColumnShown(table.VisibleColumns.Single(column => column.Id == "note"), false);
            LayoutProbe.Render(window);
            Assert.Equal(["Potion", "1,200"], row.CellTexts.Select(cell => cell.Text));

            // 读屏按列给出“列名：文字”；复制与检索仍按列文字。
            var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(row);
            Assert.Equal("目標：Potion，數量：1,200", peer.GetName());
            Click(window, container);
            Assert.Equal("目標\t數量\r\nPotion\t1,200", table.SelectionText());
            table.OpenSearch();
            table.SearchText = "1,200";
            LayoutProbe.Settle();
            Assert.Equal([0, 4], table.MatchIndices);
        }
        finally { window.Close(); }
    }
}