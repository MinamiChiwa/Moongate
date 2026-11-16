using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace MabiTool.Desktop.Overlay;

/// <summary>一块显示器：整块范围、工作区（物理像素）、缩放比例与是否为主显示器。</summary>
public sealed record ScreenInfo(PixelRect Bounds, PixelRect WorkingArea, double Scaling, bool Primary)
{
    /// <summary>记忆位置用的显示器标识：整块范围加缩放比例，例如 “0,0,1920,1080@1.5”。</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture,
        $"{Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height}@{Math.Round(Scaling, 2)}");
}

/// <summary>
/// 画布上的一块显示器：<see cref="Area"/> 是它的工作区（画布参照尺寸的逻辑像素，与 <see cref="OverlayCanvas.ReferenceSize"/> 同一单位），
/// <see cref="Dpi"/> 是它的缩放比例除以参照缩放比例（元件在这块显示器上按这个倍数放大，物理大小与在主显示器上一致）。
/// </summary>
public sealed record OverlayMonitor(string Key, Rect Area, double Dpi, bool Primary);

/// <summary>
/// 一块逻辑画面的范围（物理像素）与它的缩放比例：浮窗每块显示器一个窗口（<see cref="ForScreen"/>），版面编辑画布是摆有元件的显示器（<see cref="ForEditor"/>），
/// 与游戏窗口无关。<see cref="Monitors"/> 把全部显示器的工作区换成以 <see cref="Pixels"/> 为原点、按 <see cref="Scaling"/> 换算的逻辑矩形；
/// 元件的位置记在所在显示器的工作区里。
/// </summary>
public sealed record OverlayArea(PixelRect Pixels, double Scaling, IReadOnlyList<ScreenInfo> Screens)
{
    /// <summary>读不到任何显示器信息时，编辑画布按这个尺寸示意。</summary>
    public static readonly Size FallbackLogical = new(1920, 1080);

    public Size Logical => new(Pixels.Width / Scaling, Pixels.Height / Scaling);

    /// <summary>虚拟桌面范围；<paramref name="scaling"/> 为空时取主显示器的缩放比例（编辑画布的参照）。没有显示器时为空。</summary>
    public static OverlayArea? Resolve(IReadOnlyList<ScreenInfo> screens, double? scaling = null)
    {
        var usable = screens.Where(screen => screen.Bounds is { Width: > 0, Height: > 0 }).ToList();
        if (usable.Count == 0) return null;
        var union = usable.Skip(1).Aggregate(usable[0].Bounds, (all, screen) => all.Union(screen.Bounds));
        var primary = usable.FirstOrDefault(screen => screen.Primary) ?? usable[0];
        return new(union, scaling is > 0 and var value ? value : primary.Scaling, usable);
    }

    /// <summary>
    /// 版面编辑画布的范围：摆有元件的显示器（按 <see cref="Locate"/> 定位，都没有时为主显示器）拼成的矩形，以主显示器的缩放比例为参照。
    /// </summary>
    public static OverlayArea? ForEditor(IReadOnlyList<ScreenInfo> screens, IEnumerable<string?> placements)
    {
        if (Resolve(screens) is not { } all) return null;
        var monitors = all.Monitors;
        var used = placements.Select(key => Locate(monitors, key)!.Key).ToHashSet(StringComparer.Ordinal);
        var primary = all.Screens.FirstOrDefault(screen => screen.Primary) ?? all.Screens[0];
        if (used.Count == 0) used.Add(primary.Key);
        return Resolve(all.Screens.Where(screen => used.Contains(screen.Key)).ToList(), primary.Scaling);
    }

    /// <summary>
    /// 编辑画布中央的说明：画的是哪几块显示器（主显示器，或相对主显示器的方位）、分辨率与缩放；读不到显示器时说明按 1920×1080 示意。
    /// </summary>
    public static string Caption(IReadOnlyList<ScreenInfo>? screens)
    {
        if (screens is not { Count: > 0 })
            return Localization.UiText.Pick("屏幕（未读取到显示器信息，按 1920×1080 示意）", "螢幕（未讀取到顯示器資訊，按 1920×1080 示意）");
        var primary = screens.FirstOrDefault(screen => screen.Primary);
        string Name(ScreenInfo screen)
        {
            if (screen.Primary) return Localization.UiText.Pick("主显示器", "主顯示器");
            if (primary is null) return Localization.UiText.Pick("显示器", "顯示器");
            var (dx, dy) = (screen.Bounds.Center.X - primary.Bounds.Center.X, screen.Bounds.Center.Y - primary.Bounds.Center.Y);
            return Math.Abs(dx) >= Math.Abs(dy)
                ? dx < 0 ? Localization.UiText.Pick("左侧显示器", "左側顯示器") : Localization.UiText.Pick("右侧显示器", "右側顯示器")
                : dy < 0 ? Localization.UiText.Pick("上方显示器", "上方顯示器") : Localization.UiText.Pick("下方显示器", "下方顯示器");
        }
        return string.Join(Localization.UiText.Pick("、", "、"), screens.OrderBy(screen => screen.Bounds.X).Select(screen =>
            $"{Name(screen)}（{screen.Bounds.Width}×{screen.Bounds.Height} · {Math.Round(screen.Scaling * 100)}%）"));
    }

    /// <summary>
    /// 一块显示器上的浮窗窗口：范围是这块显示器，比例取窗口实际的缩放比例（窗口完全落在这块显示器里，就是它的比例）；
    /// <see cref="Monitors"/> 仍列出全部显示器（以这块显示器为原点），供元件按记住的显示器定位。
    /// </summary>
    public static OverlayArea ForScreen(IReadOnlyList<ScreenInfo> screens, ScreenInfo screen, double scaling) =>
        new(screen.Bounds, scaling > 0 ? scaling : screen.Scaling, screens.Where(item => item.Bounds is { Width: > 0, Height: > 0 }).ToList());

    /// <summary>每块显示器的工作区换成窗口（或画布参照）内的逻辑矩形，缩放倍数相对 <see cref="Scaling"/>。</summary>
    public IReadOnlyList<OverlayMonitor> Monitors => Screens.Select(screen => new OverlayMonitor(screen.Key,
        new Rect((screen.WorkingArea.X - Pixels.X) / Scaling, (screen.WorkingArea.Y - Pixels.Y) / Scaling,
            screen.WorkingArea.Width / Scaling, screen.WorkingArea.Height / Scaling),
        screen.Scaling / Scaling, screen.Primary)).ToList();

    public static IReadOnlyList<ScreenInfo> Describe(Screens? screens) =>
        screens?.All.Select(screen => new ScreenInfo(screen.Bounds, screen.WorkingArea, screen.Scaling > 0 ? screen.Scaling : 1, screen.IsPrimary)).ToList()
        ?? [];

    /// <summary>
    /// 元件记住的显示器：标识完全相同的一块；缩放比例改了但范围相同时取范围相同的一块；那块显示器已不在（拔掉、换了排列）时回到主显示器，
    /// 元件在工作区里的比例位置不变，所以总落在可见区域内。
    /// </summary>
    public static OverlayMonitor? Locate(IReadOnlyList<OverlayMonitor> monitors, string? key)
    {
        if (monitors.Count == 0) return null;
        if (key is { Length: > 0 })
        {
            if (monitors.FirstOrDefault(monitor => monitor.Key == key) is { } exact) return exact;
            var bounds = key.Split('@')[0];
            if (monitors.FirstOrDefault(monitor => monitor.Key.Split('@')[0] == bounds) is { } same) return same;
        }
        return monitors.FirstOrDefault(monitor => monitor.Primary) ?? monitors[0];
    }
}
