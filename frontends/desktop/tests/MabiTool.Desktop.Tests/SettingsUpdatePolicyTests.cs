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

/// <summary>设置页“更新”区：自动检查、检查间隔、下载限速的单位与提交、不限速、点空白处提交。</summary>
public sealed class SettingsUpdatePolicyTests
{
    private const int Megabyte = 1_048_576;

    private static SettingsPageModel Model(Window window) =>
        (SettingsPageModel)window.GetVisualDescendants().OfType<SettingsPage>().Single().DataContext!;

    private static Button Choice(Window window, string list, string label) =>
        Named<ItemsControl>(window, list).GetVisualDescendants().OfType<Button>()
            .Single(button => button.DataContext is PolicyChoice choice && choice.Label == label);

    private static string Active(Window window, string list) =>
        Named<ItemsControl>(window, list).GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("active")).Content as string ?? string.Empty;

    private static TextBox LimitBox(Window window) => Named<TextBox>(window, "LimitBox");

    private static TextBlock RangeNote(Window window) => Named<TextBlock>(window, "LimitRangeNote");

    private static string ActiveUnit(Window window) =>
        Named<Button>(window, "LimitKilobytesButton").Classes.Contains("active") ? "KB/s"
        : Named<Button>(window, "LimitMegabytesButton").Classes.Contains("active") ? "MB/s" : string.Empty;

    // 在输入框里输入后按回车。
    internal static void TypeAndEnter(Window window, string text)
    {
        var box = LimitBox(window);
        box.BringIntoView();
        LayoutProbe.Render(window);
        box.Focus();
        box.Text = text;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        LayoutProbe.Settle();
    }

    [AvaloniaFact]
    public void ShowsTheBackendPolicy()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "shell.settings");
        try
        {
            Assert.Contains("active", Named<Button>(window, "AutoCheckOnButton").Classes);
            Assert.DoesNotContain("active", Named<Button>(window, "AutoCheckOffButton").Classes);
            Assert.Equal("6", Active(window, "IntervalChoices"));
            Assert.Equal(["1", "6", "12", "24"], Model(window).IntervalChoices.Select(choice => choice.Label));
            // 后端值 4,194,304 字节每秒能整除 1 MB，显示为 4 MB/s。
            Assert.Equal("4", LimitBox(window).Text);
            Assert.Equal("MB/s", ActiveUnit(window));
            Assert.False(RangeNote(window).IsVisible);
            Assert.False(Named<TextBlock>(window, "PolicyUnavailableNote").IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BackendBytesAreShownInTheFittingUnit()
    {
        Assert.Equal(("4", LimitUnit.MB), SettingsPageModel.Describe(4 * Megabyte));
        Assert.Equal(("64", LimitUnit.MB), SettingsPageModel.Describe(67_108_864));
        Assert.Equal(("64", LimitUnit.KB), SettingsPageModel.Describe(65_536));
        Assert.Equal(("1536", LimitUnit.KB), SettingsPageModel.Describe(1_572_864));
        // 不能整除 1 KB 时小数最多两位。
        Assert.Equal(("97.66", LimitUnit.KB), SettingsPageModel.Describe(100_000));

        Assert.Equal(1_572_864, SettingsPageModel.ParseLimit("1.5", LimitUnit.MB));
        Assert.Equal(65_536, SettingsPageModel.ParseLimit("64", LimitUnit.KB));
        Assert.Equal(67_108_864, SettingsPageModel.ParseLimit("65,536", LimitUnit.KB));
        Assert.Equal(67_108_864, SettingsPageModel.ParseLimit(" 64 ", LimitUnit.MB));
        Assert.Null(SettingsPageModel.ParseLimit("63", LimitUnit.KB));
        Assert.Null(SettingsPageModel.ParseLimit("65", LimitUnit.MB));
        Assert.Null(SettingsPageModel.ParseLimit("0.06", LimitUnit.MB));
        Assert.Null(SettingsPageModel.ParseLimit("abc", LimitUnit.MB));
        Assert.Null(SettingsPageModel.ParseLimit("", LimitUnit.MB));
        Assert.Null(SettingsPageModel.ParseLimit("-8", LimitUnit.MB));
    }

    [AvaloniaFact]
    public async Task BackendValueThatIsNotWholeMegabytesIsShownInKilobytes()
    {
        var backend = new SampleBackend();
        await backend.SaveUpdatePolicyAsync(new(true, 168, 2, 65_536), TestContext.Current.CancellationToken);
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            Assert.Equal("168", Active(window, "IntervalChoices"));
            Assert.Equal("64", LimitBox(window).Text);
            Assert.Equal("KB/s", ActiveUnit(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EnterAndLeavingTheBoxSubmitTheWholePolicy()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            // 回车提交：完整策略，下载并发数按原值带回。
            TypeAndEnter(window, "8");
            Assert.Equal([new UpdatePolicyView(true, 6, 2, 8 * Megabyte)], backend.SavedPolicies);
            Assert.Equal("8", LimitBox(window).Text);

            // 离开输入框时提交。
            var box = LimitBox(window);
            box.Focus();
            box.Text = "1.5";
            Named<Button>(window, "AutoCheckOnButton").Focus();
            LayoutProbe.Settle();
            Assert.Equal(new UpdatePolicyView(true, 6, 2, 1_572_864), backend.SavedPolicies[^1]);
            // 1.5 MB 不能整除 1 MB，重读后以 KB/s 显示。
            Assert.Equal("1536", LimitBox(window).Text);
            Assert.Equal("KB/s", ActiveUnit(window));

            // 与后端当前值相同时不发送。
            TypeAndEnter(window, "1536");
            Assert.Equal(2, backend.SavedPolicies.Count);

            // 检查间隔与自动检查照旧逐项保存。
            Click(window, Choice(window, "IntervalChoices", "24"));
            Assert.Equal(new UpdatePolicyView(true, 24, 2, 1_572_864), backend.SavedPolicies[^1]);
            Click(window, Named<Button>(window, "AutoCheckOffButton"));
            Assert.Equal(new UpdatePolicyView(false, 24, 2, 1_572_864), backend.SavedPolicies[^1]);
            Assert.False(Choice(window, "IntervalChoices", "6").IsEffectivelyEnabled);
            Assert.True(LimitBox(window).IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SwitchingUnitKeepsTheNumberAndSubmitsItAgain()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            // 4 MB/s 改成 KB/s：数字保留，按 4 KB/s 换算，低于下限不发送并写出 KB/s 的范围。
            Click(window, Named<Button>(window, "LimitKilobytesButton"));
            Assert.Empty(backend.SavedPolicies);
            Assert.Equal("4", LimitBox(window).Text);
            Assert.Equal("KB/s", ActiveUnit(window));
            Assert.True(RangeNote(window).IsEffectivelyVisible);
            Assert.Equal("请输入 64–65,536 KB/s 之间的数字", RangeNote(window).Text);

            // 改成 3000 后切到 KB/s 已是当前单位，回车提交 3000 KB/s。
            TypeAndEnter(window, "3000");
            Assert.Equal(new UpdatePolicyView(true, 6, 2, 3000 * 1024), backend.SavedPolicies[^1]);
            Assert.False(RangeNote(window).IsVisible);
            Assert.Equal("3000", LimitBox(window).Text);

            // 输入 2 后切到 MB/s：按 2 MB/s 重新提交。
            var box = LimitBox(window);
            box.Text = "2";
            Click(window, Named<Button>(window, "LimitMegabytesButton"));
            Assert.Equal(new UpdatePolicyView(true, 6, 2, 2 * Megabyte), backend.SavedPolicies[^1]);
            Assert.Equal("2", LimitBox(window).Text);
            Assert.Equal("MB/s", ActiveUnit(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InvalidInputIsKeptNotSentAndShowsTheRange()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            foreach (var text in new[] { "abc", "", "0.05", "65" })
            {
                TypeAndEnter(window, text);
                Assert.Empty(backend.SavedPolicies);
                Assert.Equal(text, LimitBox(window).Text);
                Assert.True(RangeNote(window).IsEffectivelyVisible, $"输入「{text}」应显示范围");
                Assert.Equal("请输入 0.07–64 MB/s 之间的数字", RangeNote(window).Text);
            }

            // 后端状态刷新时保留不合法的输入。
            backend.RaiseChanged();
            LayoutProbe.Settle();
            Assert.Equal("65", LimitBox(window).Text);
            Assert.True(RangeNote(window).IsEffectivelyVisible);

            // 改成合法的值后范围说明消失。
            TypeAndEnter(window, "16");
            Assert.Equal(16 * Megabyte, Assert.Single(backend.SavedPolicies).MaximumBytesPerSecond);
            Assert.False(RangeNote(window).IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RangeNoteFollowsTheInterfaceLanguage()
    {
        var window = LayoutProbe.Open(new SampleBackend(), 1280, 860, "shell.settings", UiLanguage.TraditionalChinese);
        try
        {
            TypeAndEnter(window, "100");
            Assert.Equal("請輸入 0.07–64 MB/s 之間的數字", RangeNote(window).Text);
            Click(window, Named<Button>(window, "LimitKilobytesButton"));
            Assert.Equal("請輸入 64–65,536 KB/s 之間的數字", RangeNote(window).Text);
        }
        finally
        {
            window.Close();
            UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
        }
    }

    [AvaloniaFact]
    public void RejectedSaveReturnsToTheBackendValue()
    {
        var backend = new SampleBackend { RejectCode = "revision.stale" };
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            Click(window, Choice(window, "IntervalChoices", "12"));
            Assert.Equal(12, Assert.Single(backend.SavedPolicies).CheckIntervalHours);
            Assert.Equal("6", Active(window, "IntervalChoices"));
            Assert.Equal(6, Model(window).Policy!.CheckIntervalHours);

            TypeAndEnter(window, "512");
            Click(window, Named<Button>(window, "LimitKilobytesButton"));
            Assert.Equal(512 * 1024, backend.SavedPolicies[^1].MaximumBytesPerSecond);
            // 失败后回到后端当前值 4 MB/s，错误码显示在活动胶囊。
            Assert.Equal("4", LimitBox(window).Text);
            Assert.Equal("MB/s", ActiveUnit(window));
            Assert.False(RangeNote(window).IsVisible);
            Assert.Equal("请求未完成：revision.stale", backend.GetActivity().Detail);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == "请求未完成：revision.stale");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DisconnectedBackendDisablesThePolicy()
    {
        var backend = new SampleBackend { Offline = true };
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            Assert.False(Named<StackPanel>(window, "UpdatePolicyRows").IsEffectivelyEnabled);
            Assert.False(LimitBox(window).IsEffectivelyEnabled);
            Assert.False(Named<Button>(window, "LimitKilobytesButton").IsEffectivelyEnabled);
            Assert.True(Named<TextBlock>(window, "PolicyUnavailableNote").IsEffectivelyVisible);
            Click(window, Named<Button>(window, "AutoCheckOffButton"));
            Model(window).LimitText = "8";
            Model(window).CommitLimit();
            Assert.Empty(backend.SavedPolicies);
            Assert.False(RangeNote(window).IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void UnlimitedSavesAnEmptyLimitAndCustomReusesTheBoxValue()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            // 选“不限速”：保存为空的带宽上限，输入框隐藏。
            Click(window, Named<Button>(window, "LimitUnlimitedButton"));
            Assert.Equal(new UpdatePolicyView(true, 6, 2, null), backend.SavedPolicies[^1]);
            Assert.Contains("active", Named<Button>(window, "LimitUnlimitedButton").Classes);
            Assert.False(LimitBox(window).IsEffectivelyVisible);

            // 改回“自定义”：沿用输入框里的 4 MB/s 并保存。
            Click(window, Named<Button>(window, "LimitCustomButton"));
            Assert.Equal(new UpdatePolicyView(true, 6, 2, 4 * Megabyte), backend.SavedPolicies[^1]);
            Assert.Contains("active", Named<Button>(window, "LimitCustomButton").Classes);
            Assert.Equal("4", LimitBox(window).Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task BackendWithoutALimitShowsUnlimitedAndCustomWaitsForAValidNumber()
    {
        var backend = new SampleBackend();
        await backend.SaveUpdatePolicyAsync(new(true, 6, 2, null), TestContext.Current.CancellationToken);
        backend.SavedPolicies.Clear();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            Assert.Contains("active", Named<Button>(window, "LimitUnlimitedButton").Classes);
            Assert.False(LimitBox(window).IsEffectivelyVisible);

            // 输入框为空时改选“自定义”只显示输入框，不发送；输入合法数值后回车保存。
            Click(window, Named<Button>(window, "LimitCustomButton"));
            Assert.Empty(backend.SavedPolicies);
            Assert.True(LimitBox(window).IsEffectivelyVisible);
            Assert.Contains("active", Named<Button>(window, "LimitCustomButton").Classes);
            TypeAndEnter(window, "abc");
            Assert.Empty(backend.SavedPolicies);
            Assert.True(RangeNote(window).IsEffectivelyVisible);
            TypeAndEnter(window, "8");
            Assert.Equal(new UpdatePolicyView(true, 6, 2, 8 * Megabyte), Assert.Single(backend.SavedPolicies));
            Assert.Contains("active", Named<Button>(window, "LimitCustomButton").Classes);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClickingBlankSpaceCommitsTheFocusedBox()
    {
        var backend = new SampleBackend();
        var window = LayoutProbe.Open(backend, 1280, 860, "shell.settings");
        try
        {
            var box = Named<TextBox>(window, "LimitBox");
            box.BringIntoView();
            LayoutProbe.Render(window);
            box.Focus();
            box.Text = "8";
            LayoutProbe.Settle();
            Assert.Same(box, window.FocusManager!.GetFocusedElement());

            // 在输入框里按下（选择文字）不提交。
            var inside = box.TranslatePoint(new Point(10, box.Bounds.Height / 2), window)!.Value;
            window.MouseDown(inside, MouseButton.Left);
            window.MouseUp(inside, MouseButton.Left);
            LayoutProbe.Settle();
            Assert.Same(box, window.FocusManager.GetFocusedElement());
            Assert.Empty(backend.SavedPolicies);

            // 点面板的空白处：输入框失去焦点并提交。
            var panel = Named<Card>(window, "UpdatePanel");
            var blank = panel.TranslatePoint(new Point(panel.Bounds.Width - 6, 6), window)!.Value;
            window.MouseDown(blank, MouseButton.Left);
            window.MouseUp(blank, MouseButton.Left);
            LayoutProbe.Settle();
            Assert.NotSame(box, window.FocusManager.GetFocusedElement());
            Assert.Equal(8 * 1_048_576, backend.SavedPolicies[^1].MaximumBytesPerSecond);

            // 点按钮照常是点击。
            box.Focus();
            Click(window, Named<Button>(window, "AutoCheckOffButton"));
            Assert.False(backend.SavedPolicies[^1].AutomaticChecks);
        }
        finally { window.Close(); }
    }
}
