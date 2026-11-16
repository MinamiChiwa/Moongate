using Avalonia;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 目录包里同一工具根只运行一个前端：已有前端时把它的窗口提到前台，本进程直接结束。
        if (!Infrastructure.PackagedHost.ClaimInstance(args)) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().UseBundledFonts();
}
