using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Dyeing;

/// <summary>后端给出的 RGB24 画面转成界面位图（不透明 BGRA）。</summary>
public static class ColorBitmaps
{
    public static WriteableBitmap Create(ColorBitmap source) => Create(source.Width, source.Height, source.Rgb);

    public static WriteableBitmap Create(int width, int height, byte[] rgb)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        var row = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            var source = y * width * 3;
            for (var x = 0; x < width; x++)
            {
                row[x * 4] = rgb[source + x * 3 + 2];
                row[x * 4 + 1] = rgb[source + x * 3 + 1];
                row[x * 4 + 2] = rgb[source + x * 3];
                row[x * 4 + 3] = 255;
            }
            Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }
        return bitmap;
    }
}

/// <summary>
/// 吸管的坐标换算、画面核对与取像素（原工具 screen_mapping.frozen_image_point 与 eyedropper.pixel_hex）。
/// 全程使用物理像素：物理光标坐标减去冻结画面左上角（可为负）即画面像素，不经过任何界面缩放。
/// 画面按显示器逐块冻结，每块显示器一张画面。
/// </summary>
public static class ScreenPickMath
{
    /// <summary>后端单张截屏的像素上限（ColorCaptureService.MaximumPixels，4096×4096），按单块显示器计算。</summary>
    public const long MaximumPixels = 16_777_216;

    /// <summary>物理光标坐标对应的冻结画面像素；在画面外时为空（不取边缘颜色）。</summary>
    public static PixelPoint? ImagePoint(PixelPoint cursor, PixelRect capture)
    {
        var x = (long)cursor.X - capture.X;
        var y = (long)cursor.Y - capture.Y;
        return x >= 0 && y >= 0 && x < capture.Width && y < capture.Height ? new PixelPoint((int)x, (int)y) : null;
    }

    /// <summary>冻结画面上一个像素的 #RRGGBB。</summary>
    public static string PixelHex(ScreenCapture capture, PixelPoint pixel)
    {
        var offset = (pixel.Y * capture.Width + pixel.X) * 3;
        return "#" + Convert.ToHexString(capture.Rgb, offset, 3);
    }

    /// <summary>物理光标处的颜色；光标在冻结画面外时为空。</summary>
    public static string? HexAt(ScreenCapture capture, PixelPoint cursor) =>
        ImagePoint(cursor, Bounds(capture)) is { } pixel ? PixelHex(capture, pixel) : null;

    public static PixelRect Bounds(ScreenCapture capture) => new(capture.X, capture.Y, capture.Width, capture.Height);

    /// <summary>物理像素尺寸在界面缩放 <paramref name="scaling"/> 下的逻辑尺寸（物理 ÷ 缩放）。</summary>
    public static Size LogicalSize(PixelSize size, double scaling) => new(size.Width / scaling, size.Height / scaling);

    /// <summary>冻结画面在取色层上的逻辑矩形：从客户区左上角起，按当前界面缩放换算，使每个画面像素正好占一个物理像素。</summary>
    public static Rect FrozenRect(ScreenCapture capture, double scaling) => new(LogicalSize(new PixelSize(capture.Width, capture.Height), scaling));

    /// <summary>物理光标所在的那个物理像素在取色层上的逻辑矩形（边长 1 物理像素）；光标不在这块显示器上时为空。</summary>
    public static Rect? CursorCell(PixelPoint cursor, PixelRect monitor, double scaling) =>
        ImagePoint(cursor, monitor) is { } pixel ? new Rect(pixel.X / scaling, pixel.Y / scaling, 1 / scaling, 1 / scaling) : null;

    /// <summary>界面缩放是否等于窗口 DPI（96 DPI 对应 1.0）。</summary>
    public static bool SameScaling(double scaling, uint dpi) => dpi > 0 && Math.Abs(scaling - dpi / 96.0) < 1e-9;

    public static bool ExceedsLimit(PixelRect monitor) => (long)monitor.Width * monitor.Height > MaximumPixels;

    /// <summary>物理光标所在的显示器序号；不在任何显示器上时为 -1。</summary>
    public static int MonitorAt(IReadOnlyList<PixelRect> monitors, PixelPoint cursor)
    {
        for (var index = 0; index < monitors.Count; index++)
            if (ImagePoint(cursor, monitors[index]) is not null) return index;
        return -1;
    }

    /// <summary>两次读到的显示器布局（物理像素矩形，顺序固定）是否相同；读不到时视为不同。</summary>
    public static bool SameLayout(IReadOnlyList<PixelRect> before, IReadOnlyList<PixelRect>? after) =>
        after is not null && before.SequenceEqual(after);

    public static string LayoutChanged => UiText.Pick("取色期间显示布局变化，请重新取色。", "取色期間顯示配置變化，請重新取色。");

    /// <summary>
    /// 核对一块显示器的冻结画面：取色开始时的显示器布局 <paramref name="before"/> 与截屏后重新读到的 <paramref name="after"/> 相同，
    /// 且后端画面的原点与宽高等于请求的显示器区域 <paramref name="requested"/>。一致时为空，否则给出原因（带两边的实际边界）。
    /// </summary>
    public static string? CheckCapture(IReadOnlyList<PixelRect> before, IReadOnlyList<PixelRect>? after, PixelRect requested, PixelRect capture)
    {
        if (!SameLayout(before, after)) return LayoutChanged;
        if (capture == requested) return null;
        return UiText.Pick($"后端画面 {Describe(capture)} 与请求的屏幕区域 {Describe(requested)} 不一致，请重新取色。",
            $"後端畫面 {Describe(capture)} 與請求的螢幕區域 {Describe(requested)} 不一致，請重新取色。");
    }

    /// <summary>显示器超过截屏上限时在这块显示器上显示的说明。</summary>
    public static string TooLarge(PixelRect monitor) =>
        UiText.Pick($"这块屏幕 {Describe(monitor)} 超过截屏上限 16,777,216 像素，请移到其他屏幕取色，或按右键、Esc 取消。",
            $"這塊螢幕 {Describe(monitor)} 超過截圖上限 16,777,216 像素，請移到其他螢幕取色，或按右鍵、Esc 取消。");

    /// <summary>边界的文字：原点与宽×高。</summary>
    public static string Describe(PixelRect rect) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({rect.X}, {rect.Y}) {rect.Width}×{rect.Height}");
}

/// <summary>取色层上发生的事：光标进入某块显示器、左键确认（物理光标位置）、取消或出错。</summary>
public abstract record PickEvent
{
    private PickEvent() { }

    /// <summary>物理光标移到序号为 <paramref name="Monitor"/> 的显示器上（取色开始时报告光标所在的显示器）。</summary>
    public sealed record Entered(int Monitor) : PickEvent;

    public sealed record Confirmed(PixelPoint Cursor) : PickEvent;

    public sealed record Cancelled : PickEvent
    {
        public static Cancelled Instance { get; } = new();
    }

    public sealed record Failed(string Reason) : PickEvent;
}

/// <summary>
/// 每块显示器一层的取色层。显示后各层都接收点击（不落到下面的程序），还没冻结的显示器上的层不进入截图；
/// 某层报告 <see cref="PickEvent.Entered"/> 时它已经避开截图，可以截取这块显示器。释放时关闭全部层。
/// </summary>
public interface IPickLayers : IDisposable
{
    /// <summary>等待下一件事。</summary>
    ValueTask<PickEvent> NextAsync(CancellationToken cancellationToken);

    /// <summary>在这块显示器上显示“正在截取这块屏幕”（提示不进入截图）。</summary>
    void ShowCapturing(int monitor);

    /// <summary>在这块显示器上按物理像素 1:1 显示冻结画面，画十字线、采样框与跟随标签。</summary>
    void ShowFrozen(int monitor, ScreenCapture capture);

    /// <summary>在这块显示器上显示说明（如超过截屏上限）；这层继续接收点击。</summary>
    void ShowNotice(int monitor, string message);
}

/// <summary>一次吸管的结果：取到的颜色、取消（两者都为空）或失败原因；<see cref="Note"/> 是没能切到游戏窗口等不影响取色的说明。</summary>
public sealed record ScreenPickResult(string? Hex, string? Error, string? Note = null);

/// <summary>
/// 吸管需要的桌面能力：隐藏与恢复 Moongate 的窗口、按物理像素列出显示器、在每块显示器上显示取色层。
/// 真实实现见 <see cref="DesktopScreenPickHost"/>；测试替换成记录调用的实现。
/// </summary>
public interface IScreenPickHost
{
    /// <summary>隐藏当前可见的全部 Moongate 窗口；释放返回值时恢复它们。</summary>
    IDisposable HideWindows();

    /// <summary>等隐藏、游戏窗口前置与取色层避开截图生效再截屏（原工具 150 ms），避免把自己的窗口冻结进画面。</summary>
    Task SettleAsync(CancellationToken cancellationToken);

    /// <summary>各显示器的物理像素矩形（含负原点，顺序固定）；读不到或进不了物理像素上下文时为空。</summary>
    IReadOnlyList<PixelRect>? Monitors();

    /// <summary>在每块显示器上显示取色层。</summary>
    IPickLayers ShowLayers(IReadOnlyList<PixelRect> monitors);
}

/// <summary>
/// 吸管流程（原工具 qt_ui/eyedropper.py，改为按显示器逐块冻结）：有绑定时先激活游戏窗口 → 隐藏 Moongate 全部窗口 →
/// 按物理像素列出显示器 → 每块显示器铺一层取色层 → 等待生效 → 光标所在的显示器报告进入时只冻结这一块，移到还没冻结的显示器时再冻结那一块，
/// 冻结过的显示器保留画面。每块画面读回后立即释放后端画面（画面已在本地）；超过单张上限的显示器只在这块显示器上说明。
/// 左键确认、右键或 Esc 取消结束整个取色。激活失败不阻止取色，结果里注明没能切到游戏窗口。
/// 任何路径（确认、取消、出错、超时）都关闭全部取色层、释放画面并恢复隐藏的窗口。
/// </summary>
public static class EyedropperSession
{
    /// <summary>每块显示器截屏请求的超时。</summary>
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(15);

    public static async Task<ScreenPickResult> RunAsync(IDesktopBackend backend, IScreenPickHost host, CancellationToken cancellationToken)
    {
        // Moongate 仍在前台时请求激活，系统才允许把前台交给游戏。
        var activation = await backend.ActivateGameWindowAsync(cancellationToken);
        var note = activation.Missed
            ? UiText.Pick("未能切到游戏窗口", "未能切到遊戲視窗") + (activation.Message.Length > 0 ? "（" + activation.Message + "）" : string.Empty)
            : null;
        var frozen = new Dictionary<int, ScreenCapture>();
        var refused = new HashSet<int>();
        IPickLayers? layers = null;
        var hidden = host.HideWindows();
        try
        {
            if (host.Monitors() is not { Count: > 0 } monitors || monitors.Any(rect => rect.Width <= 0 || rect.Height <= 0))
                return Failed(UiText.Pick("无法按物理像素读取显示器边界，请重试。", "無法依實體像素讀取顯示器邊界，請重試。"), note);
            layers = host.ShowLayers(monitors);
            await host.SettleAsync(cancellationToken);
            while (true)
            {
                switch (await layers.NextAsync(cancellationToken))
                {
                    case PickEvent.Entered(var index) when index >= 0 && index < monitors.Count && !frozen.ContainsKey(index) && !refused.Contains(index):
                        var monitor = monitors[index];
                        if (ScreenPickMath.ExceedsLimit(monitor))
                        {
                            refused.Add(index);
                            layers.ShowNotice(index, ScreenPickMath.TooLarge(monitor));
                            break;
                        }
                        layers.ShowCapturing(index);
                        ScreenCapture capture;
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            timeout.CancelAfter(CaptureTimeout);
                            var reply = await backend.CaptureScreenAsync(monitor, timeout.Token);
                            if (reply.Capture is null) return Failed(reply.Message, note);
                            capture = reply.Capture;
                        }
                        // 画面已完整读回本地，后端画面立即释放；下一块显示器的截屏不会替换掉仍需释放的画面。
                        await backend.ReleaseCaptureAsync(capture.Id, CancellationToken.None);
                        if (ScreenPickMath.CheckCapture(monitors, host.Monitors(), monitor, ScreenPickMath.Bounds(capture)) is { } mismatch)
                            return Failed(mismatch, note);
                        frozen[index] = capture;
                        layers.ShowFrozen(index, capture);
                        break;
                    case PickEvent.Confirmed(var cursor):
                        if (!ScreenPickMath.SameLayout(monitors, host.Monitors())) return Failed(ScreenPickMath.LayoutChanged, note);
                        // 光标不在已冻结的显示器上（还在截取或超过上限）时不结束，继续等待。
                        foreach (var picked in frozen.Values)
                            if (ScreenPickMath.HexAt(picked, cursor) is { } hex) return new(hex, null, note);
                        break;
                    case PickEvent.Cancelled:
                        return new(null, null, note);
                    case PickEvent.Failed(var reason):
                        return Failed(reason, note);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Failed(UiText.Pick("截屏超时，请重试。", "截圖逾時，請重試。"), note);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ExternalException)
        {
            return Failed(Localization.CodeText.Error(error), note);
        }
        finally
        {
            layers?.Dispose();
            hidden.Dispose();
        }
    }

    private static ScreenPickResult Failed(string reason, string? note) => new(null, reason, note);
}
