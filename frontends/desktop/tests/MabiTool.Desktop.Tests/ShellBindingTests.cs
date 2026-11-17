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

/// <summary>侧栏绑定卡：绑定名称的映射与显示，以及打开的选择游戏窗口弹出层。</summary>
public sealed class ShellBindingTests
{
    private static List<Button> Rows(Window window) =>
        Named<ItemsControl>(window, "GameWindowList").GetVisualDescendants().OfType<Button>().ToList();

    private static int ListRequests(SampleBackend backend) => backend.UiIntents.Count(intent => intent.Intent == "game.windows");

    [AvaloniaFact]
    public void OpeningReadsCandidatesAgainEveryTimeAndMarksTheCurrentWindow()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860);
        try
        {
            OpenPicker(window);
            Assert.True(Picker(window).IsOpen);
            Assert.True(Named<Border>(window, "GamePickerPanel").IsEffectivelyVisible);
            Assert.Equal(1, ListRequests(backend));
            Assert.Equal(3, Rows(window).Count);
            var current = Assert.Single(Picker(window).Rows, row => row.Current);
            Assert.Equal("sample-window-1", current.Id);
            Assert.Equal("Mabinogi Mobile", current.Name);
            Assert.Contains("12840", current.Detail);
            Assert.Contains("洛奇 Mobile 台服", current.Detail);

            Click(window, Named<Button>(window, "GamePickerRefresh"));
            Assert.Equal(2, ListRequests(backend));
            Click(window, Named<Button>(window, "GamePickerClose"));
            Assert.False(Picker(window).IsOpen);
            Assert.Empty(Picker(window).Rows);

            OpenPicker(window);
            Assert.Equal(3, ListRequests(backend));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ShowsLoadingUntilTheBackendAnswers()
    {
        var gate = new TaskCompletionSource();
        var backend = new SampleBackend { WindowListGate = gate };
        var window = LayoutProbe.Open(backend, 1280, 860);
        try
        {
            OpenPicker(window);
            Assert.True(Named<TextBlock>(window, "GamePickerLoading").IsEffectivelyVisible);
            Assert.False(Named<TextBlock>(window, "GamePickerEmpty").IsEffectivelyVisible);
            Assert.False(Named<Button>(window, "GamePickerRefresh").IsEffectivelyEnabled);
            Assert.Empty(Rows(window));

            gate.SetResult();
            LayoutProbe.Settle();
            Assert.False(Named<TextBlock>(window, "GamePickerLoading").IsEffectivelyVisible);
            Assert.Equal(3, Rows(window).Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LateAnswerAfterClosingIsDiscarded()
    {
        var gate = new TaskCompletionSource();
        var backend = new SampleBackend { WindowListGate = gate };
        var window = LayoutProbe.Open(backend, 1280, 860);
        try
        {
            OpenPicker(window);
            Picker(window).Close();
            gate.SetResult();
            LayoutProbe.Settle();
            Assert.False(Picker(window).IsOpen);
            Assert.Empty(Picker(window).Rows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ChoosingAWindowSendsOneBindingAndClosesThePicker()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860);
        try
        {
            OpenPicker(window);
            var second = Rows(window).Single(button => button.DataContext is GameWindowRow { Id: "sample-window-2" });
            Click(window, second);

            Assert.Equal([("game.select", (string?)"sample-window-2")], backend.UiIntents.Where(intent => intent.Intent == "game.select"));
            Assert.False(Picker(window).IsOpen);
            Assert.False(Named<Panel>(window, "GamePickerLayer").IsVisible);
            // 绑定随后端状态刷新；角色名只在抓包状态里显示，绑定卡标题是窗口本身，不重复角色名。
            Assert.Equal("芙蕾雅", ((ShellViewModel)window.DataContext!).Binding.CharacterName);
            var card = Named<Button>(window, "BindingCard");
            Assert.Equal("游戏窗口", Named<TextBlock>(card, "BindingTitle").Text);
            Assert.DoesNotContain(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "芙蕾雅");

            OpenPicker(window);
            Assert.Equal("sample-window-2", Assert.Single(Picker(window).Rows, row => row.Current).Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RejectedBindingKeepsThePickerOpenAndReportsInTheCapsule()
    {
        var backend = new SampleBackend { RejectCode = "game.selectionExpired" };
        var window = LayoutProbe.Open(backend, 1280, 860);
        try
        {
            OpenPicker(window);
            Click(window, Rows(window)[1]);

            Assert.True(Picker(window).IsOpen);
            Assert.Equal(3, Rows(window).Count);
            Assert.True(Rows(window).All(row => row.IsEffectivelyEnabled));
            Assert.Equal("请求未完成：窗口选择已过期", ((ShellViewModel)window.DataContext!).Activity.Detail);
            Assert.Equal("琉特", ((ShellViewModel)window.DataContext!).Binding.CharacterName);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EmptyListAsksToStartTheGame()
    {
        var backend = new SampleBackend { NoGameWindows = true };
        var window = LayoutProbe.Open(backend, 1280, 860);
        try
        {
            OpenPicker(window);
            var empty = Named<TextBlock>(window, "GamePickerEmpty");
            Assert.True(empty.IsEffectivelyVisible);
            Assert.Equal("没有找到游戏窗口，请先启动游戏，再按刷新。", empty.Text);
            Assert.Empty(Rows(window));
            Assert.True(Named<Button>(window, "GamePickerRefresh").IsEffectivelyEnabled);
            Assert.Empty(LayoutProbe.FindTextOverflow(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DisconnectedBackendDisablesTheEntry()
    {
        var backend = new SampleBackend { Offline = true };
        var window = LayoutProbe.Open(backend, 1280, 860);
        try
        {
            var card = Named<Button>(window, "BindingCard");
            Assert.False(card.IsEffectivelyEnabled);
            OpenPicker(window);
            Assert.False(Picker(window).IsOpen);
            Assert.DoesNotContain(backend.UiIntents, intent => intent.Intent == "game.windows");
            // 停用的绑定卡文字保持原样，不随停用变淡。
            Assert.Equal(1, card.Opacity);
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "未连接后端");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void KeyboardSelectsWithEnterAndClosesWithEscape()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860);
        try
        {
            var card = Named<Button>(window, "BindingCard");
            Assert.Equal("选择游戏窗口", AutomationProperties.GetName(card));

            card.Focus(NavigationMethod.Tab);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            LayoutProbe.Render(window);
            Assert.True(Picker(window).IsOpen);

            // 读取完成后焦点落在第一行；Tab 移到下一行，Enter 选择。
            var rows = Rows(window);
            Assert.Same(rows[0], window.FocusManager?.GetFocusedElement());
            Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(row))));
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            LayoutProbe.Settle();
            Assert.Same(rows[1], window.FocusManager?.GetFocusedElement());
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            LayoutProbe.Settle();
            Assert.Equal(("game.select", (string?)"sample-window-2"), backend.UiIntents[^1]);
            Assert.False(Picker(window).IsOpen);

            // Esc 关闭且不发送绑定，焦点回到绑定卡。
            OpenPicker(window);
            var sent = backend.UiIntents.Count(intent => intent.Intent == "game.select");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            LayoutProbe.Settle();
            Assert.False(Picker(window).IsOpen);
            Assert.Equal(sent, backend.UiIntents.Count(intent => intent.Intent == "game.select"));
            Assert.Same(card, window.FocusManager?.GetFocusedElement());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClickingOutsideClosesWithoutBinding()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860);
        try
        {
            OpenPicker(window);
            window.MouseDown(new Point(1100, 500), MouseButton.Left);
            window.MouseUp(new Point(1100, 500), MouseButton.Left);
            LayoutProbe.Settle();
            Assert.False(Picker(window).IsOpen);
            Assert.DoesNotContain(backend.UiIntents, intent => intent.Intent == "game.select");
        }
        finally { window.Close(); }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static GameBindingSnapshot Binding(string state = "bound", bool simulation = false) => new(state,
        new(new("session", "TW", 1, Now, new(1), new(1), null), new(0, 0, 1280, 720), 1, 1, false, true), simulation, "epoch", new(1));

    [AvaloniaFact]
    public void BindingNameUsesObservedNicknameVerbatim()
    {
        var view = LiveBackend.MapBinding(true, Binding(), false, "琉特・Ruth");
        Assert.Equal("琉特・Ruth", view.CharacterName);
        Assert.True(view.Confirmed);
        Assert.Equal("已绑定游戏窗口", view.Status);
    }

    [AvaloniaTheory]
    [InlineData(null, false, "游戏窗口")]
    [InlineData("", false, "游戏窗口")]
    [InlineData(null, true, "模拟游戏窗口")]
    public void BindingNameKeepsWindowTextWithoutNickname(string? nickname, bool simulation, string expected) =>
        Assert.Equal(expected, LiveBackend.MapBinding(true, Binding(simulation: simulation), simulation, nickname).CharacterName);

    [AvaloniaFact]
    public void NicknameDoesNotReplaceUnboundOrDisconnectedText()
    {
        Assert.Equal("未绑定", LiveBackend.MapBinding(true, new("unbound", null, false, "epoch", new(1)), false, "琉特").CharacterName);
        Assert.Equal("未连接后端", LiveBackend.MapBinding(false, null, false, "琉特").CharacterName);
    }

    [AvaloniaFact]
    public void SidebarAndMiniWindowShowTheBindingName()
    {
        var window = LayoutProbe.Open(new LongTextBackend(), 1280, 860, "music-library");
        try
        {
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == "名字非常非常長的角色名稱測試用琉特");
            var mini = OpenMini(new LongTextBackend(), 440, 920, UiThemeChoice.Light, UiLanguage.SimplifiedChinese);
            try
            {
                Assert.Contains("名字非常非常長的角色名稱測試用琉特", ((Shell.MiniWindowModel)mini.DataContext!).BindingLine);
            }
            finally { mini.Close(); }
        }
        finally { window.Close(); }
    }
}
