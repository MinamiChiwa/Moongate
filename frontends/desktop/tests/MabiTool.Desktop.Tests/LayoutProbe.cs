using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Shell;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Tests;

internal static class LayoutProbe
{
    private const double Tolerance = 1;

    public static Window Open(IDesktopBackend backend, double width, double height, string? moduleId = null,
        UiLanguage language = UiLanguage.SimplifiedChinese, UiThemeChoice theme = UiThemeChoice.Light)
    {
        UiText.Apply(Application.Current!, language);
        // 默认固定为浅色，排版与截图不受运行机器的系统主题影响。
        UiTheme.Apply(Application.Current!, theme);
        // 排版检查在静止状态下进行；动效另有专门的测试。
        MabiTool.Desktop.Theme.UiMotion.Enabled = false;
        var window = App.CreateMainWindow(backend);
        window.Width = width;
        window.Height = height;
        window.Show();
        if (moduleId is not null) Select(window, moduleId);
        Settle();
        return window;
    }

    public static void Select(Window window, string moduleId)
    {
        var shell = (ShellViewModel)window.DataContext!;
        shell.Select(shell.BuiltIn.Concat(shell.Extensions).Concat(shell.ShellEntries).Concat(shell.PendingModules)
            .Single(item => item.Module.Id == moduleId));
        Settle();
    }

    public static void Settle() => Dispatcher.UIThread.RunJobs();

    /// <summary>排版后再画一帧：无界面模式的命中测试用的是最近画出的画面，滚动之后要先画一帧才准。</summary>
    public static void Render(Window window)
    {
        Settle();
        using var frame = window.CaptureRenderedFrame();
        Settle();
    }

    /// <summary>
    /// 返回所有出框或被裁切的文字。出框：文字块超出最近的面板、内嵌块或按钮；
    /// 裁切：没有省略号也不换行的文字，实际排版宽度大于分配到的宽度。
    /// 横向滚动区内的内容只检查裁切，滚动区本身必须留在面板内。
    /// </summary>
    public static List<string> FindTextOverflow(Window window)
    {
        var problems = new List<string>();
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
        {
            if (ScrolledOutOfCappedList(text)) continue;
            var label = $"「{text.Text}」";
            var layout = text.TextLayout;
            var content = new Size(text.Bounds.Width - text.Padding.Left - text.Padding.Right,
                text.Bounds.Height - text.Padding.Top - text.Padding.Bottom);
            if (text.TextTrimming == TextTrimming.None && text.TextWrapping == TextWrapping.NoWrap
                && layout.WidthIncludingTrailingWhitespace > content.Width + Tolerance)
                problems.Add($"{label} 被裁切：需要 {layout.WidthIncludingTrailingWhitespace:F1}，只有 {content.Width:F1}");
            if (layout.Height > content.Height + Tolerance)
                problems.Add($"{label} 高度不足：需要 {layout.Height:F1}，只有 {content.Height:F1}");

            var container = text.GetVisualAncestors().OfType<Control>().FirstOrDefault(IsContainer);
            if (container is null or ScrollViewer) continue;
            if (Escapes(text, container, out var detail)) problems.Add($"{label} 超出 {Describe(container)}：{detail}");
        }

        foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>()
                     .Where(s => s.IsEffectivelyVisible && s.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled))
        {
            var panel = scroller.GetVisualAncestors().OfType<Control>().FirstOrDefault(IsContainer);
            if (panel is not null and not ScrollViewer && Escapes(scroller, panel, out var detail))
                problems.Add($"横向滚动区超出 {Describe(panel)}：{detail}");
        }

        foreach (var panel in window.GetVisualDescendants().OfType<Border>()
                     .Where(b => b.IsEffectivelyVisible && b.Classes.Contains("panel")))
        {
            var origin = panel.TranslatePoint(default, window) ?? default;
            if (origin.X < -Tolerance || origin.X + panel.Bounds.Width > window.Bounds.Width + Tolerance)
                problems.Add($"面板 {panel.Name} 横向超出窗口：{origin.X:F1} + {panel.Bounds.Width:F1} > {window.Bounds.Width:F1}");
        }
        return problems;
    }

    // 限高滚动列表（设置了 MaxHeight 的滚动区，例如诊断页的实时记录）里没有完整落在可视范围内的文字不检查：
    // 它们被滚动区裁切，不属于出框。页面本身的滚动区不在此列。
    private static bool ScrolledOutOfCappedList(TextBlock text)
    {
        foreach (var scroller in text.GetVisualAncestors().OfType<ScrollViewer>().Where(s => !double.IsInfinity(s.MaxHeight)))
        {
            if (scroller.Extent.Height <= scroller.Viewport.Height + Tolerance) continue;
            var rect = new Rect(text.Bounds.Size).TransformToAABB(text.TransformToVisual(scroller) ?? Matrix.Identity);
            if (rect.Y < -Tolerance || rect.Bottom > scroller.Viewport.Height + Tolerance) return true;
        }
        return false;
    }

    public static string SaveScreenshot(Window window, string name)
    {
        var directory = Path.Combine(RepositoryRoot(), "artifacts", "test-results", "desktop-frontend");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".png");
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("没有取得渲染画面");
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return path;
    }

    private static bool IsContainer(Control control) => control switch
    {
        Button => true,
        // 面板内可纵向滚动的区域（例如合奏页底部状态卡的详情）裁掉滚出视口的内容是预期行为，也算容器。
        ScrollViewer scroller => scroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
                                 || scroller.Name != "PageScroller" && scroller.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled
                                 && scroller.GetVisualAncestors().OfType<Border>().Any(border => border.Classes.Contains("panel")),
        Border border => border.Classes.Contains("panel") || border.Classes.Contains("inset")
            || border.Classes.Contains("glyph") || border.Classes.Contains("effect"),
        _ => false
    };

    private static bool Escapes(Control inner, Control outer, out string detail)
    {
        // 经过缩放容器的元素按变换后的外接矩形判断。
        var transform = inner.TransformToVisual(outer) ?? Matrix.Identity;
        var rect = new Rect(inner.Bounds.Size).TransformToAABB(transform);
        var limit = new Rect(outer.Bounds.Size).Inflate(Tolerance);
        detail = $"{rect} 不在 {outer.Bounds.Size} 内";
        return !limit.Contains(rect);
    }

    private static string Describe(Control control) =>
        string.IsNullOrEmpty(control.Name) ? $"{control.GetType().Name}[{string.Join('.', control.Classes)}]" : control.Name;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MabiTool.slnx"))) return directory.FullName;
        throw new InvalidOperationException("找不到仓库根目录");
    }
}
