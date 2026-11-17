using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace MabiTool.Desktop.Overlay;

/// <summary>
/// 浮窗拖动诊断，默认关闭，由诊断页打开，只在本次运行内有效。打开后每次拖动或改尺寸记一段：
/// 开始时的显示器枚举、各显示器浮窗窗口的范围与缩放、光标限制区域（ClipCursor）、前台窗口是否属于本程序、持有鼠标捕获的窗口；
/// 拖动中每次取到的光标物理坐标、换算后的画布坐标、来源（轮询或指针事件）与判定的显示器；结束原因与最终落点。
/// 只记坐标与窗口信息，不记录屏幕内容。拖动结束时一次写入工具根的 desktop/overlay-drag.log，超过 2 MB 时从头重写。
/// </summary>
public static class OverlayDragTrace
{
    private const int MaxSamples = 4000;
    private const long MaxBytes = 2 * 1024 * 1024;

    private static StringBuilder? current;
    private static Stopwatch clock = new();
    private static int samples;
    private static string? lastSample;
    private static bool enabled;

    /// <summary>是否记录；关闭时各方法立即返回。</summary>
    public static bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            current = null;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? Changed;

    /// <summary>记录文件。</summary>
    public static string FilePath => Infrastructure.DesktopFiles.File("overlay-drag.log");

    /// <summary>测试用：替换写入目标；为空时写 <see cref="FilePath"/>。</summary>
    internal static string? PathOverride { get; set; }

    /// <summary>拖动开始时补充的环境说明（各显示器的浮窗窗口），由浮窗控制器提供。</summary>
    public static Func<IEnumerable<string>>? Environment { get; set; }

    internal static void Begin(OverlayCanvas canvas, WidgetPlacement placement, bool resize, Point origin)
    {
        if (!enabled) return;
        var text = new StringBuilder();
        text.Append("== ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(resize ? " resize " : " drag ").Append(placement.WidgetId)
            .Append(" from screen=").Append(placement.Screen ?? "(primary)")
            .Append(" x=").Append(F(placement.X)).Append(" y=").Append(F(placement.Y))
            .Append(" pressed=").Append(F(origin.X)).Append(',').Append(F(origin.Y)).AppendLine();
        text.Append("canvas own=").Append(canvas.OwnMonitorKey ?? "(all)");
        if (canvas.Area is { } area)
            text.Append(" pixels=").Append(Rect(area.Pixels)).Append(" scaling=").Append(F(area.Scaling));
        text.Append(" cursorSource=").Append(canvas.CursorSource is null ? "none" : "system").AppendLine();
        foreach (var monitor in canvas.Monitors)
            text.Append("monitor ").Append(monitor.Key).Append(monitor.Primary ? " primary" : string.Empty)
                .Append(" area=").Append(F(monitor.Area.X)).Append(',').Append(F(monitor.Area.Y)).Append(' ')
                .Append(F(monitor.Area.Width)).Append('x').Append(F(monitor.Area.Height)).Append(" dpi=").Append(F(monitor.Dpi)).AppendLine();
        try
        {
            if (Environment?.Invoke() is { } lines)
                foreach (var line in lines) text.AppendLine(line);
        }
        catch (Exception error) when (error is InvalidOperationException or NullReferenceException) { text.AppendLine("environment unavailable"); }
        if (OperatingSystem.IsWindows()) AppendSystem(text);
        current = text;
        samples = 0;
        lastSample = null;
        clock = Stopwatch.StartNew();
    }

    internal static void Sample(string source, PixelPoint? physical, Point logical, string? monitor)
    {
        if (current is null || samples >= MaxSamples) return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{source} cursor={(physical is { } p ? $"{p.X},{p.Y}" : "-")} canvas={F(logical.X)},{F(logical.Y)} monitor={monitor ?? "-"}");
        if (line == lastSample) return;
        lastSample = line;
        samples++;
        current.Append('+').Append(clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)).Append("ms ").AppendLine(line);
    }

    internal static void Note(string note)
    {
        current?.Append('+').Append(clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)).Append("ms ").AppendLine(note);
    }

    internal static void End(string reason, WidgetPlacement? placement)
    {
        if (current is not { } text) return;
        current = null;
        text.Append('+').Append(clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)).Append("ms end ").Append(reason);
        if (placement is not null)
            text.Append(" screen=").Append(placement.Screen ?? "(primary)").Append(" x=").Append(F(placement.X)).Append(" y=").Append(F(placement.Y))
                .Append(" width=").Append(placement.Width is { } w ? F(w) : "-").Append(" height=").Append(placement.Height is { } h ? F(h) : "-");
        if (samples >= MaxSamples) text.Append(" (samples truncated)");
        text.AppendLine().AppendLine();
        try
        {
            var path = PathOverride ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
            File.AppendAllText(path, text.ToString(), Encoding.UTF8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void AppendSystem(StringBuilder text)
    {
        text.Append("clip=").Append(GetClipCursor(out var clip) ? $"{clip.Left},{clip.Top},{clip.Right},{clip.Bottom}" : "-");
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var process);
        text.Append(" foregroundOwn=").Append(process == (uint)System.Environment.ProcessId ? "yes" : "no");
        var capture = GetCapture();
        text.Append(" capture=").Append(capture == IntPtr.Zero ? "none" : "0x" + capture.ToString("X", CultureInfo.InvariantCulture));
        text.Append(" cursor=").Append(Win32OverlayCursor.GetCursorPos(out var cursor) ? $"{cursor.X},{cursor.Y}" : "-").AppendLine();
    }

    private static string F(double value) => Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);

    private static string Rect(PixelRect rect) => string.Create(CultureInfo.InvariantCulture, $"{rect.X},{rect.Y} {rect.Width}x{rect.Height}");

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursor(out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();
}
