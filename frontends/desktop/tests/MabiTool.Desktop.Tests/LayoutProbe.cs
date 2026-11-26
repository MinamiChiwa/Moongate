using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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

    /// <summary>
    /// 返回悬停时被裁切的按钮与图标。按钮：给窗口加上 motion 类、把按钮置为悬停，读外观层（PART_Face）样式给出的真实变换，
    /// 取放大后的外观轮廓；图标（<see cref="Controls.IconTile"/>）：取各动效预设里色块整体外扩的最大轮廓
    /// （素材包图片按整块动）。轮廓依次对照每个裁切祖先（ClipToBounds、HoverRoom 设的裁切、圆角容器、窗口边界、
    /// 滚动区的内容范围）。静止时已经被某个祖先裁切的控件（滚出视口、窄窗口放不下）不算悬停造成的裁切，跳过该祖先。
    /// </summary>
    public static List<string> FindHoverClipping(Window window)
    {
        var problems = new List<string>();
        var faces = new List<(Button Button, Border Face)>();
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
            if (button.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Face") is { IsEffectivelyVisible: true } face
                && face.Bounds.Width > 0 && face.Bounds.Height > 0)
                faces.Add((button, face));

        // 外观层的过渡先关掉：状态一变变换立即到位，读到的就是样式给出的终值。
        foreach (var (_, face) in faces) face.Transitions = null;
        var hadMotion = window.Classes.Contains("motion");
        window.Classes.Set("motion", true);
        try
        {
            foreach (var (button, face) in faces)
            {
                ((IPseudoClasses)button.Classes).Set(":pointerover", true);
                var hover = HoverMatrix(face);
                ((IPseudoClasses)button.Classes).Set(":pointerover", false);
                if (hover.IsIdentity) continue;
                foreach (var problem in ClipProblems(face, face.Bounds.Size, Math.Max(face.CornerRadius.TopLeft, face.CornerRadius.TopRight), [hover]))
                    problems.Add($"按钮 {Describe(button)}「{ButtonLabel(button)}」悬停放大后{problem}");
            }
            foreach (var tile in window.GetVisualDescendants().OfType<Controls.IconTile>().Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 0))
            {
                var size = Math.Min(tile.Bounds.Width, tile.Bounds.Height);
                foreach (var problem in ClipProblems(tile, tile.Bounds.Size, size * 0.3, IconMotionEnvelope(size)))
                    problems.Add($"图标 {tile.Slot} 动效外扩后{problem}");
            }
        }
        finally
        {
            window.Classes.Set("motion", hadMotion);
            foreach (var (_, face) in faces) face.ClearValue(Animatable.TransitionsProperty);
        }
        return problems;
    }

    /// <summary>图标各动效预设在各进度的整块变换（以色块中心为原点）。</summary>
    private static List<Matrix> IconMotionEnvelope(double size)
    {
        var center = new Point(size / 2, size / 2);
        var matrices = new List<Matrix>();
        foreach (var preset in IconMotion.Presets.Where(preset => preset != IconMotion.None))
            for (var step = 1; step < 40; step++)
                matrices.Add(Matrix.CreateTranslation(-center.X, -center.Y) * IconMotion.Transform(preset, step / 40.0, size)
                             * Matrix.CreateTranslation(center.X, center.Y));
        return matrices;
    }

    private static string ButtonLabel(Button button) =>
        button.Content as string ?? button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? button.Name ?? string.Empty;

    private static Matrix HoverMatrix(Border face)
    {
        if (face.RenderTransform is not { } transform) return Matrix.Identity;
        var origin = face.RenderTransformOrigin.ToPixels(face.Bounds.Size);
        return Matrix.CreateTranslation(-origin.X, -origin.Y) * transform.Value * Matrix.CreateTranslation(origin.X, origin.Y);
    }

    /// <summary>圆角矩形轮廓按变换后的位置取样的点：四条边的端点与每个圆角上的几个点。</summary>
    private static List<Point> OutlinePoints(Size size, double cornerRadius, Matrix transform)
    {
        var radius = Math.Min(cornerRadius, Math.Min(size.Width, size.Height) / 2);
        var points = new List<Point>();
        var centers = new[]
        {
            (new Point(radius, radius), Math.PI), (new Point(size.Width - radius, radius), -Math.PI / 2),
            (new Point(size.Width - radius, size.Height - radius), 0d), (new Point(radius, size.Height - radius), Math.PI / 2)
        };
        foreach (var (center, start) in centers)
            for (var step = 0; step <= 4; step++)
            {
                var angle = start + Math.PI / 2 * step / 4;
                points.Add(transform.Transform(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle))));
            }
        return points;
    }

    private static IEnumerable<string> ClipProblems(Visual face, Size size, double cornerRadius, IReadOnlyList<Matrix> transforms)
    {
        var rest = OutlinePoints(size, cornerRadius, Matrix.Identity);
        var grown = transforms.SelectMany(transform => OutlinePoints(size, cornerRadius, transform)).ToList();
        // 滚动区在可滚动的方向上，内容两端都会停在视口边缘：该方向按内容范围（Extent）而不是当前视口判断，
        // 并且视口之外的祖先在该方向上不再重复判断（视口已经是更近的裁切边界）。
        var (xDone, yDone) = (false, false);
        foreach (var clipper in face.GetVisualAncestors())
        {
            var toClip = face.TransformToVisual(clipper) ?? Matrix.Identity;
            foreach (var clip in ClipsOf(clipper))
            {
                var checkX = !xDone || clip.ScrollX;
                var checkY = !yDone || clip.ScrollY;
                if (!checkX && !checkY) continue;
                if (rest.Any(point => !InsideClip(toClip.Transform(point), clip.Rect, clip.Radius, checkX, checkY))) continue;
                var outside = grown.Select(point => toClip.Transform(point))
                    .Where(point => !InsideClip(point, clip.Rect, clip.Radius, checkX, checkY)).ToList();
                if (outside.Count > 0)
                    yield return $"超出 {Describe((Control)clipper)} 的{(clip.ScrollX || clip.ScrollY ? "内容" : "裁切")}范围 {clip.Rect}（{outside[0]}）";
            }
            if (clipper is ScrollContentPresenter presenter)
            {
                xDone |= presenter.Extent.Width > presenter.Viewport.Width + 0.5;
                yDone |= presenter.Extent.Height > presenter.Viewport.Height + 0.5;
            }
        }
    }

    private readonly record struct Clip(Rect Rect, double Radius, bool ScrollX = false, bool ScrollY = false);

    private static IEnumerable<Clip> ClipsOf(Visual visual)
    {
        // 滚动区的外壳与它里面的内容呈现器裁切同一片区域，只取内容呈现器。
        if (visual is ScrollViewer) yield break;
        if (visual.Clip is { } geometry) yield return new Clip(geometry.Bounds, 0);
        if (visual is ScrollContentPresenter presenter)
        {
            var (scrollX, scrollY) = (presenter.Extent.Width > presenter.Viewport.Width + 0.5, presenter.Extent.Height > presenter.Viewport.Height + 0.5);
            var rect = new Rect(scrollX ? -presenter.Offset.X : 0, scrollY ? -presenter.Offset.Y : 0,
                scrollX ? presenter.Extent.Width : presenter.Bounds.Width, scrollY ? presenter.Extent.Height : presenter.Bounds.Height);
            yield return new Clip(rect, 0, scrollX, scrollY);
        }
        else if (visual.ClipToBounds || visual is TopLevel)
            yield return new Clip(new Rect(visual.Bounds.Size),
                visual is Border border ? Math.Max(border.CornerRadius.TopLeft, border.CornerRadius.TopRight) : 0);
    }

    private static bool InsideClip(Point point, Rect rect, double radius, bool checkX = true, bool checkY = true)
    {
        const double slack = 0.5;
        if (checkX && (point.X < rect.Left - slack || point.X > rect.Right + slack)
            || checkY && (point.Y < rect.Top - slack || point.Y > rect.Bottom + slack))
            return false;
        if (radius <= 0 || !checkX || !checkY) return true;
        var r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        var cx = Math.Clamp(point.X, rect.Left + r, rect.Right - r);
        var cy = Math.Clamp(point.Y, rect.Top + r, rect.Bottom - r);
        return Math.Sqrt((point.X - cx) * (point.X - cx) + (point.Y - cy) * (point.Y - cy)) <= r + slack;
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
