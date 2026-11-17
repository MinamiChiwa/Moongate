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
/// </summary>
public static class ScreenPickMath
{
    /// <summary>后端单张截屏的像素上限（ColorCaptureService.MaximumPixels，4096×4096）。</summary>
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

    public static bool ExceedsLimit(PixelRect desktop) => (long)desktop.Width * desktop.Height > MaximumPixels;

    /// <summary>
    /// 核对冻结画面与本机按物理像素读到的虚拟桌面（截屏前 <paramref name="before"/>、截屏后 <paramref name="after"/>）。
    /// 一致时为空；截屏期间显示布局变化、画面按缩放后的逻辑像素截取（DPI 感知不一致）或其他不一致时给出原因，
    /// 原因里带两边的实际边界，便于对照。
    /// </summary>
    public static string? CheckCapture(PixelRect before, PixelRect after, PixelRect capture)
    {
        if (before != after)
            return UiText.Pick($"截屏期间显示布局变化（{Describe(before)} → {Describe(after)}），请重新取色。",
                $"截圖期間顯示配置變化（{Describe(before)} → {Describe(after)}），請重新取色。");
        if (capture == before) return null;
        if (LogicalScale(before, capture) is { } scale)
            return UiText.Pick($"后端画面 {Describe(capture)} 是按 {scale:0.##} 倍缩放后的逻辑像素，本机物理桌面是 {Describe(before)}（DPI 感知不一致），无法按物理像素取色。",
                $"後端畫面 {Describe(capture)} 是按 {scale:0.##} 倍縮放後的邏輯像素，本機實體桌面是 {Describe(before)}（DPI 感知不一致），無法依實體像素取色。");
        return UiText.Pick($"后端画面 {Describe(capture)} 与本机物理桌面 {Describe(before)} 不一致，请重新取色。",
            $"後端畫面 {Describe(capture)} 與本機實體桌面 {Describe(before)} 不一致，請重新取色。");
    }

    /// <summary>画面宽高按同一比例（大于 1，误差 1% 内）小于物理桌面时，返回这个比例：后端在 DPI 虚拟化的上下文里读取了桌面。</summary>
    public static double? LogicalScale(PixelRect physical, PixelRect capture)
    {
        if (capture.Width <= 0 || capture.Height <= 0) return null;
        var x = (double)physical.Width / capture.Width;
        var y = (double)physical.Height / capture.Height;
        return x > 1.01 && Math.Abs(x - y) <= 0.01 * x ? Math.Round(x * 4) / 4 : null;
    }

    /// <summary>边界的文字：原点与宽×高。</summary>
    public static string Describe(PixelRect rect) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({rect.X}, {rect.Y}) {rect.Width}×{rect.Height}");
}

/// <summary>取色层的结果：确认时带物理光标位置；失败时带原因。</summary>
public sealed record PickLayerResult(PixelPoint? Cursor, string? Error = null)
{
    public static PickLayerResult Cancelled { get; } = new((PixelPoint?)null);
}

/// <summary>一次吸管的结果：取到的颜色、取消（两者都为空）或失败原因；<see cref="Note"/> 是没能切到游戏窗口等不影响取色的说明。</summary>
public sealed record ScreenPickResult(string? Hex, string? Error, string? Note = null);

/// <summary>
/// 吸管需要的桌面能力：隐藏与恢复 Moongate 的窗口、读取虚拟桌面的物理边界、显示全屏取色层。
/// 真实实现见 <see cref="DesktopScreenPickHost"/>；测试替换成记录调用的实现。
/// </summary>
public interface IScreenPickHost
{
    /// <summary>隐藏当前可见的全部 Moongate 窗口；释放返回值时恢复它们。</summary>
    IDisposable HideWindows();

    /// <summary>等隐藏与游戏窗口前置生效再截屏（原工具 150 ms），避免把自己的窗口冻结进画面。</summary>
    Task SettleAsync(CancellationToken cancellationToken);

    /// <summary>虚拟桌面的物理像素边界（含负原点）；读不到或进不了物理像素上下文时为空。</summary>
    PixelRect? DesktopBounds();

    /// <summary>显示覆盖整个虚拟桌面的取色层，直到确认、取消或出错。</summary>
    Task<PickLayerResult> ShowLayerAsync(ScreenCapture capture, CancellationToken cancellationToken);
}

/// <summary>
/// 吸管流程（原工具 qt_ui/eyedropper.py）：有绑定时先激活游戏窗口 → 隐藏 Moongate 全部窗口 → 等待生效 → 读物理桌面边界 →
/// 后端冻结整个虚拟桌面 → 再读一次边界并核对画面 → 取色层 → 确认或取消 → 释放画面 → 恢复窗口。
/// 激活失败不阻止取色，结果里注明没能切到游戏窗口。任何路径（确认、取消、出错、超时）都释放冻结画面并恢复隐藏的窗口。
/// </summary>
public static class EyedropperSession
{
    /// <summary>截屏请求的超时。</summary>
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(15);

    public static async Task<ScreenPickResult> RunAsync(IDesktopBackend backend, IScreenPickHost host, CancellationToken cancellationToken)
    {
        // Moongate 仍在前台时请求激活，系统才允许把前台交给游戏。
        var activation = await backend.ActivateGameWindowAsync(cancellationToken);
        var note = activation.Missed
            ? UiText.Pick("未能切到游戏窗口", "未能切到遊戲視窗") + (activation.Message.Length > 0 ? "（" + activation.Message + "）" : string.Empty)
            : null;
        ScreenCapture? capture = null;
        var hidden = host.HideWindows();
        try
        {
            await host.SettleAsync(cancellationToken);
            if (host.DesktopBounds() is not { } desktop || desktop.Width <= 0 || desktop.Height <= 0)
                return Failed(UiText.Pick("无法按物理像素读取屏幕边界，请重试。", "無法依實體像素讀取螢幕邊界，請重試。"), note);
            if (ScreenPickMath.ExceedsLimit(desktop))
                return Failed(UiText.Pick($"屏幕总像素超过后端截屏上限（{ScreenPickMath.Describe(desktop)}）", $"螢幕總像素超過後端截圖上限（{ScreenPickMath.Describe(desktop)}）"), note);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(CaptureTimeout);
                var reply = await backend.CaptureScreenAsync(timeout.Token);
                capture = reply.Capture;
                if (capture is null) return Failed(reply.Message, note);
            }
            var after = host.DesktopBounds() ?? desktop;
            if (ScreenPickMath.CheckCapture(desktop, after, ScreenPickMath.Bounds(capture)) is { } mismatch) return Failed(mismatch, note);
            var result = await host.ShowLayerAsync(capture, cancellationToken);
            if (result.Error is { } error) return Failed(error, note);
            if (result.Cursor is not { } cursor) return new(null, null, note);
            return ScreenPickMath.HexAt(capture, cursor) is { } hex
                ? new(hex, null, note)
                : Failed(UiText.Pick("鼠标已超出冻结画面，请重新取色。", "滑鼠已超出凍結畫面，請重新取色。"), note);
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
            if (capture is not null) await backend.ReleaseCaptureAsync(capture.Id, CancellationToken.None);
            hidden.Dispose();
        }
    }

    private static ScreenPickResult Failed(string reason, string? note) => new(null, reason, note);
}
