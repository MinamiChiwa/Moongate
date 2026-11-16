using Avalonia;
using Avalonia.Headless;
using MabiTool.Desktop.Tests;
using MabiTool.Desktop.Theme;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace MabiTool.Desktop.Tests;

public static class TestAppBuilder
{
    // 使用真实 Skia 绘制和随包字体，文字量测与截图才与实际窗口一致。
    // 空闲预建常用页面会让每个打开主窗口的用例多建三个页面；只在专门的用例里打开。
    static TestAppBuilder() => MabiTool.Desktop.Shell.ShellViewModel.PrebuildOnIdle = false;

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseBundledFonts()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
