using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Infrastructure;
using Xunit;
using static MabiTool.Desktop.Tests.TestUi;

namespace MabiTool.Desktop.Tests;

/// <summary>卡片网格：尺寸档与列数的排版规则、布局按列数保存与旧拖高迁移、编辑模式内换位与改档并保存、取消与恢复默认。</summary>
public sealed class CardGridTests
{
    private const CardSize Full = CardSize.FullWidth;

    [Fact]
    public void CardsArePlacedBySizeAndColumnCount()
    {
        // 每列至少 280、间距 14：868 起三列，574 起两列。
        Assert.Equal([3, 2, 2, 1], new[] { 868d, 867, 574, 573 }.Select(width => CardGrid.ColumnsFor(width)));
        // 行数以半行为最小单位。
        Assert.Equal([73d, 160, 247, 334, 508], new[] { 0.5, 1, 1.5, 2, 3 }.Select(rows => CardGrid.HeightFor(rows)));

        // DPS 页缺省布局：范围、伤害、治疗全宽，两张效果卡片半宽×2 并排，其后三张全宽。横向以半列为单位，固定档纵向以半行为一条行轨。
        CardSize[] combat = [Full, Full, Full, CardSize.HalfByTwo, CardSize.HalfByTwo, Full, Full, Full];
        Assert.Equal<CardCell>(
            [new(0, 6, 0, 0), new(0, 6, 1, 0), new(0, 6, 2, 0), new(0, 3, 3, 4), new(3, 3, 3, 4), new(0, 6, 7, 0), new(0, 6, 8, 0), new(0, 6, 9, 0)],
            CardGrid.Place(combat, 3));
        // 两列：半宽占一列，全宽占满一行。
        Assert.Equal<CardCell>([new(0, 4, 2, 0), new(0, 2, 3, 4), new(2, 2, 3, 4), new(0, 4, 7, 0)], CardGrid.Place(combat, 2).Skip(2).Take(4));
        // 一列：全部占满一行，行数不变。
        Assert.Equal<CardCell>([new(0, 1, 2, 0), new(0, 1, 3, 4), new(0, 1, 7, 4), new(0, 1, 11, 0)], CardGrid.Place(combat, 1).Skip(2).Take(4));

        // 按顺序排入最先放得下的位置：2×2 占两行，其后的 1×1 排到它左侧的空位，再放不下的换到下一行。
        Assert.Equal<CardCell>([new(0, 2, 0, 2), new(2, 4, 0, 4), new(0, 2, 2, 2), new(0, 2, 4, 2)],
            CardGrid.Place([CardSize.OneByOne, CardSize.TwoByTwo, CardSize.OneByOne, CardSize.OneByOne], 3));

        // 随内容档：相邻的几张在一行里放得下时共用一条行轨，放不下的另起一条；其后的固定档从下面接着排。
        Assert.Equal<CardCell>([new(0, 4, 0, 0), new(4, 2, 0, 0), new(0, 2, 1, 0), new(0, 2, 2, 3)],
            CardGrid.Place([CardSize.TwoByAuto, CardSize.OneByAuto, CardSize.OneByAuto, CardSize.OneByOneAndHalf], 3));
        // 背包页缺省布局：物品 2×4，右侧一列叠放 1×1.5 与 1×2.5（合起来与物品等高），其下两张随内容的并排。
        Assert.Equal<CardCell>([new(0, 4, 0, 8), new(4, 2, 0, 3), new(4, 2, 3, 5), new(0, 2, 8, 0), new(2, 2, 8, 0)],
            CardGrid.Place([CardSize.TwoByFour, CardSize.OneByOneAndHalf, CardSize.OneByTwoAndHalf, CardSize.OneByAuto, CardSize.OneByAuto], 3));
    }

    [AvaloniaFact]
    public void LayoutIsSavedPerColumnCountAndLegacyDraggedHeightsMigrate()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"moongate-cards-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preferences.json");
        var legacy = Path.Combine(directory, "combat-cards.json");
        try
        {
            // 旧版：两张效果卡片列表区的拖动高度。换算成最接近的行数，写成各列数相同的半宽档。
            File.WriteAllText(path, """{ "Cards": { "combat/self-effects": { "Expanded": false } } }""");
            File.WriteAllText(legacy, """{ "combat.self-effects": 390, "combat.target-effects": 234 }""");
            var cards = new DesktopPreferenceStore(path).Load().Cards!;
            Assert.False(cards["combat/self-effects"].Expanded);
            Assert.Equal([1, 2, 3], cards["combat/self-effects"].Layout!.Keys.Order());
            Assert.All(cards["combat/self-effects"].Layout!.Values, slot => Assert.Equal(new CardSlot(CardSize.HalfByThree), slot));
            Assert.Equal(new CardSlot(CardSize.HalfByTwo), cards["combat/target-effects"].Layout![3]);

            // 三列下保存一次布局：只写三列的顺序与尺寸档；偏好保存后旧文件删除，再读取结果相同。
            CardStateStore.Restore(cards);
            CardStateStore.SetLayout("combat", 3, [("healing", CardSize.TwoByOne), ("self-effects", CardSize.HalfByFour)]);
            new DesktopPreferenceStore(path).Save(DesktopPreferences.Default with { Cards = CardStateStore.All });
            Assert.False(File.Exists(legacy));
            CardStateStore.Restore(new DesktopPreferenceStore(path).Load().Cards);
            Assert.Equal(new CardSlot(CardSize.TwoByOne, 0), CardStateStore.Slot("combat", "healing", 3));
            Assert.Null(CardStateStore.Slot("combat", "healing", 2));
            Assert.Equal(new CardSlot(CardSize.HalfByFour, 1), CardStateStore.Slot("combat", "self-effects", 3));
            Assert.Equal(new CardSlot(CardSize.HalfByThree), CardStateStore.Slot("combat", "self-effects", 2));

            // 恢复缺省：布局去掉，折叠状态保留；只有布局的卡片不再留项。
            CardStateStore.ClearLayout("combat");
            Assert.Equal(new CardState(false), Assert.Single(CardStateStore.All).Value);
        }
        finally
        {
            CardStateStore.Restore(null);
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void EditModeReordersAndResizesThroughTheMenuSavesPerColumnCountAndCancelDiscards()
    {
        CardStateStore.Restore(null);
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "combat-observation");
        try
        {
            var grid = Named<CardGrid>(window, "CardsGrid");
            Card Panel(string name) => Named<Card>(window, name);
            // 滚入视野后按原位点按：点到的必须就是这个按钮本身（没有被编辑层或别的按钮盖住）。
            void ClickInPlaceAfterScroll(Button button)
            {
                button.BringIntoView();
                ClickInPlace(window, button);
            }
            double Top(string name) => Panel(name).Bounds.Top;
            LayoutProbe.Render(window);

            // 缺省布局（三列）：治疗全宽、高度随内容（不撑到一行高），两张效果卡片两行高、等宽并排。
            Assert.Equal(3, grid.Columns);
            Assert.Equal(grid.Bounds.Width, Panel("HealingPanel").Bounds.Width, 0.5);
            Assert.True(Panel("HealingPanel").Bounds.Height < CardGrid.RowUnit);
            Assert.Equal(CardGrid.HeightFor(2), Panel("SelfEffectsPanel").Bounds.Height);
            Assert.Equal(Top("SelfEffectsPanel"), Top("TargetEffectsPanel"));
            Assert.Equal(Panel("SelfEffectsPanel").Bounds.Width, Panel("TargetEffectsPanel").Bounds.Width, 0.5);
            Assert.True(Top("DamagePanel") < Top("HealingPanel"));

            // 平时不能调整。
            Assert.False(grid.Move(Panel("HealingPanel"), -1));
            Assert.False(grid.Resize(Panel("HealingPanel"), CardSize.TwoByAuto));

            // 编辑模式：标题栏变成取消、恢复默认、完成；卡片上盖编辑层，点卡片标题行不折叠；卡片自己的操作区让给布局菜单。
            Click(window, Named<Button>(window, "EditLayoutButton"));
            Assert.True(grid.IsEditing);
            Assert.All(new[] { "CancelLayoutButton", "ResetLayoutButton", "DoneLayoutButton" },
                name => Assert.True(Named<Button>(window, name).IsEffectivelyVisible));
            Assert.False(Named<Button>(window, "SelfFavoritesButton").IsEffectivelyVisible);
            Click(window, Panel("ScopePanel").Toggle);
            Assert.True(Panel("ScopePanel").IsExpanded);

            // 聚焦“治疗”的编辑层后按左方向键：前移到“伤害”之前；只接受适用的尺寸档。
            Control Layer(string name) => window.GetVisualDescendants().OfType<Control>()
                .Single(control => control.Name == "CardEditLayer" && control.Bounds == Panel(name).Bounds);
            var layer = Layer("HealingPanel");
            Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(layer)));
            Assert.True(layer.Focus());
            PressKey(window, Key.Left, PhysicalKey.ArrowLeft);
            LayoutProbe.Render(window);
            Assert.True(Top("HealingPanel") < Top("DamagePanel"));
            Assert.False(grid.Resize(Panel("HealingPanel"), CardSize.HalfByFour));

            // 按住“统计范围”拖到“治疗”的下半部：落在“治疗”之后、“伤害”之前。
            Layer("ScopePanel").BringIntoView();
            LayoutProbe.Render(window);
            var from = Center(Layer("ScopePanel"), window);
            Assert.True(window.InputHitTest(from) is Control { Name: "CardEditLayer" });
            var healing = Panel("HealingPanel");
            var to = healing.TranslatePoint(new Point(healing.Bounds.Width / 2, healing.Bounds.Height - 20), window)!.Value;
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(new Point(from.X, from.Y + 20));
            window.MouseMove(to);
            window.MouseUp(to, MouseButton.Left);
            LayoutProbe.Render(window);
            Assert.True(Top("HealingPanel") < Top("ScopePanel") && Top("ScopePanel") < Top("DamagePanel"));

            // 布局菜单：真实点击打开，列出前移、后移与该卡片适用的尺寸档（当前档带选中标记）；点“半宽×3”改档。
            Button Menu(string name) => Layer(name).GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CardLayoutMenu");
            List<MenuItem> Items() => window.GetVisualDescendants().OfType<MenuItem>().Where(item => item.IsEffectivelyVisible).ToList();
            var menu = Menu("SelfEffectsPanel");
            Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(menu)));
            ClickInPlaceAfterScroll(menu);
            LayoutProbe.Render(window);
            Assert.Equal(["前移", "后移", "半宽×2", "半宽×3", "半宽×4", "2×2", "3×2"], Items().Select(item => item.Header as string));
            Assert.Equal("半宽×2", Assert.Single(Items(), item => item.IsChecked).Header);
            PressAt(window, Center(Items().Single(item => "半宽×3".Equals(item.Header)), window));
            LayoutProbe.Render(window);
            Assert.Empty(Items());
            Assert.Equal(CardSize.HalfByThree, grid.SizeOf(Panel("SelfEffectsPanel")));
            Assert.Equal(CardGrid.HeightFor(3), Panel("SelfEffectsPanel").Bounds.Height);
            // 键盘：聚焦菜单按钮后按回车同样打开，Esc 关闭。
            menu = Menu("TargetEffectsPanel");
            Assert.True(menu.Focus());
            PressKey(window, Key.Enter, PhysicalKey.Enter);
            LayoutProbe.Render(window);
            Assert.Equal(7, Items().Count);
            PressKey(window, Key.Escape, PhysicalKey.Escape);
            LayoutProbe.Render(window);
            Assert.Empty(Items());

            // 完成：保存三列的顺序与尺寸档，编辑层撤掉，标题行恢复可点。
            Click(window, Named<Button>(window, "DoneLayoutButton"));
            Assert.False(grid.IsEditing);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), control => control.Name == "CardEditLayer");
            Assert.Equal(new CardSlot(CardSize.FullWidth, 0), CardStateStore.Slot("combat", "healing", 3));
            Assert.Equal(new CardSlot(CardSize.FullWidth, 1), CardStateStore.Slot("combat", "scope", 3));
            Assert.Equal(new CardSlot(CardSize.FullWidth, 2), CardStateStore.Slot("combat", "damage", 3));
            Assert.Equal(CardSize.HalfByThree, CardStateStore.Slot("combat", "self-effects", 3)!.Size);
            Assert.Null(CardStateStore.Slot("combat", "healing", 2));
            LayoutProbe.Render(window);
            Assert.Equal(CardGrid.HeightFor(3), Panel("SelfEffectsPanel").Bounds.Height);

            // 窗口变窄降为两列：两列没有保存过，取缺省布局，效果卡片仍并排；再窄为一列时上下排。
            window.Width = 1000;
            LayoutProbe.Render(window);
            Assert.Equal(2, grid.Columns);
            Assert.True(Top("DamagePanel") < Top("HealingPanel"));
            Assert.Equal(Top("SelfEffectsPanel"), Top("TargetEffectsPanel"));
            Assert.Equal(CardGrid.HeightFor(2), Panel("SelfEffectsPanel").Bounds.Height);
            window.Width = 760;
            LayoutProbe.Render(window);
            Assert.Equal(1, grid.Columns);
            Assert.True(Top("SelfEffectsPanel") < Top("TargetEffectsPanel"));

            // 回到三列宽度：显示三列下保存的布局。收起“统计范围”，折叠状态存入同一处。
            window.Width = 1280;
            LayoutProbe.Render(window);
            Assert.Equal(3, grid.Columns);
            Assert.True(Top("HealingPanel") < Top("DamagePanel"));
            Click(window, Panel("ScopePanel").Toggle);
            Assert.False(Panel("ScopePanel").IsExpanded);
            var saved = CardStateStore.All.ToDictionary();

            // 取消：编辑中的换位、改档与恢复默认全部丢弃，布局、折叠状态与保存项都和进入编辑时一样。
            Click(window, Named<Button>(window, "EditLayoutButton"));
            Layer("BreakPanel").Focus();
            PressKey(window, Key.Up, PhysicalKey.ArrowUp);
            Click(window, Named<Button>(window, "ResetLayoutButton"));
            LayoutProbe.Render(window);
            Assert.True(grid.IsEditing);
            Assert.True(Panel("ScopePanel").IsExpanded);
            Assert.True(Top("ScopePanel") < Top("DamagePanel") && Top("DamagePanel") < Top("HealingPanel"));
            Assert.Equal(saved, CardStateStore.All);
            Click(window, Named<Button>(window, "CancelLayoutButton"));
            LayoutProbe.Render(window);
            Assert.False(grid.IsEditing);
            Assert.False(Panel("ScopePanel").IsExpanded);
            Assert.True(Top("HealingPanel") < Top("ScopePanel") && Top("ScopePanel") < Top("DamagePanel"));
            Assert.Equal(CardGrid.HeightFor(3), Panel("SelfEffectsPanel").Bounds.Height);
            Assert.Equal(saved, CardStateStore.All);

            // 恢复默认并完成：各列数的布局与折叠状态都回到缺省，保存项去掉。
            Click(window, Named<Button>(window, "EditLayoutButton"));
            Click(window, Named<Button>(window, "ResetLayoutButton"));
            Click(window, Named<Button>(window, "DoneLayoutButton"));
            LayoutProbe.Render(window);
            Assert.True(Panel("ScopePanel").IsExpanded);
            Assert.True(Top("ScopePanel") < Top("DamagePanel") && Top("DamagePanel") < Top("HealingPanel"));
            Assert.Equal(CardGrid.HeightFor(2), Panel("SelfEffectsPanel").Bounds.Height);
            Assert.Empty(CardStateStore.All);
        }
        finally
        {
            window.Close();
            CardStateStore.Restore(null);
        }
    }
    [AvaloniaFact]
    public void ScrollAreasKeepAFixedScrollBarSlot()
    {
        // 卡片外壳的滚动区：内容放得下与放不下时内容宽度相同，滚动条在内容右侧的槽位里，与内容隔开固定间距。
        Border Content(double height) => new() { Height = height };
        ScrollViewer Scroller(Control content) => new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = content
        };
        var shortContent = Content(50);
        var tallContent = Content(1000);
        var shortScroller = Scroller(shortContent);
        var tallScroller = Scroller(tallContent);
        var outer = Scroller(new StackPanel { Children = { new Border { Height = 200, Child = shortScroller }, new Border { Height = 200, Child = tallScroller }, Content(1000) } });
        var window = new Window { Width = 300, Height = 420, Content = outer };
        window.Show();
        try
        {
            LayoutProbe.Render(window);
            var bar = tallScroller.GetVisualDescendants().OfType<ScrollBar>().Single(item => item.Name == "PART_VerticalScrollBar");
            Assert.True(bar.IsVisible);
            Assert.Equal(shortContent.Bounds.Width, tallContent.Bounds.Width);
            var barLeft = bar.TranslatePoint(default, tallScroller)!.Value.X;
            var contentRight = tallContent.TranslatePoint(new Point(tallContent.Bounds.Width, 0), tallScroller)!.Value.X;
            Assert.Equal(ScrollGutter.Gap, barLeft - contentRight, 1);

            // 滚轮落在槽位（滚动条）上：滚动所在的滚动区，外层不动。
            window.MouseWheel(bar.TranslatePoint(new Point(bar.Bounds.Width / 2, 40), window)!.Value, new Vector(0, -1));
            LayoutProbe.Settle();
            Assert.True(tallScroller.Offset.Y > 0);
            Assert.Equal(0, outer.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }
}
