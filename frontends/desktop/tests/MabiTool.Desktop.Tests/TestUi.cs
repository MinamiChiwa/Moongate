using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Shell;
using MabiTool.Desktop.Theme;
using Xunit;

namespace MabiTool.Desktop.Tests;

/// <summary>各测试共用的查找、点按与窗口操作。</summary>
internal static class TestUi
{
    public static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    public static T Page<T>(Visual root) where T : Control => root.GetVisualDescendants().OfType<T>().Single();

    /// <summary>看得见的文字：文字块，加上表格行自己画出的各列文字。</summary>
    public static List<string> VisibleTexts(Visual root) => root.GetVisualDescendants().OfType<TextBlock>()
        .Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? string.Empty)
        .Concat(root.GetVisualDescendants().OfType<MabiTool.Desktop.Controls.Tables.TableRowPresenter>()
            .Where(row => row.IsEffectivelyVisible).SelectMany(row => row.CellTexts.Select(cell => cell.Text)))
        .ToList();

    public static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>滚入视野、画一帧后按中心真实点按；停用的控件同样点，用来确认不会发出意图。</summary>
    public static void Click(Window window, Control target)
    {
        target.BringIntoView();
        LayoutProbe.Render(window);
        var center = Center(target, window);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        LayoutProbe.Settle();
    }

    /// <summary>
    /// 不滚动，直接点按钮当前所在的位置；点到的必须就是这个按钮（没有被活动胶囊或其他元素盖住，也不在滚动区外）。
    /// </summary>
    public static void ClickInPlace(Window window, Button button)
    {
        LayoutProbe.Render(window);
        Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
        var center = Center(button, window);
        var hit = window.InputHitTest(center) as Visual;
        Assert.True(hit is not null && (ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button)),
            $"按钮 {button.Name} 的中心 {center} 处是 {hit?.GetType().Name}");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        LayoutProbe.Settle();
    }

    public static void PressAt(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        LayoutProbe.Settle();
    }

    public static void PressKey(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None,
        string? text = null)
    {
        window.KeyPress(key, modifiers, physical, text);
        window.KeyRelease(key, modifiers, physical, text);
        LayoutProbe.Settle();
    }

    /// <summary>侧栏里某个模块的入口。</summary>
    public static Button Nav(Window window, string moduleId) => window.GetVisualDescendants().OfType<Button>()
        .Single(button => button.DataContext is NavigationItem item && item.Module.Id == moduleId && button.Classes.Contains("nav"));

    /// <summary>推进真实时间与渲染帧，让动画与页面切换走完。</summary>
    public static void Pump(Window window, int milliseconds = 450)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            Thread.Sleep(16);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            LayoutProbe.Settle();
        }
        LayoutProbe.Render(window);
    }

    /// <summary>单独打开迷你窗（不经主窗口），语言、主题与动效在建窗前设好。</summary>
    public static MiniWindow OpenMini(IDesktopBackend backend, double width, double height,
        UiThemeChoice theme = UiThemeChoice.Light, UiLanguage language = UiLanguage.SimplifiedChinese)
    {
        UiText.Apply(Application.Current!, language);
        UiTheme.Apply(Application.Current!, theme);
        UiMotion.Enabled = false;
        var window = new MiniWindow { DataContext = new MiniWindowModel(backend), Width = width, Height = height };
        window.Show();
        LayoutProbe.Settle();
        return window;
    }

    public static GamePickerModel Picker(Window window) => ((ShellViewModel)window.DataContext!).GamePicker;

    /// <summary>点侧栏绑定卡打开选择游戏窗口弹出层。</summary>
    public static void OpenPicker(Window window) => Click(window, Named<Button>(window, "BindingCard"));

    /// <summary>测试结束时回到默认的简体中文与浅色。</summary>
    public static void ResetAppearance()
    {
        UiTheme.Apply(Application.Current!, UiThemeChoice.Light);
        UiText.Apply(Application.Current!, UiLanguage.SimplifiedChinese);
    }

    public static double Contrast(Color a, Color b)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) =>
            0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

        var (lighter, darker) = (Luminance(a), Luminance(b));
        if (lighter < darker) (lighter, darker) = (darker, lighter);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static double MeasureWidth(this TextBlock text)
    {
        text.Measure(Size.Infinity);
        return text.DesiredSize.Width;
    }
}
