using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Shell;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        UiText.Apply(this, UiLanguage.SimplifiedChinese);
        UiTheme.Apply(this, UiThemeChoice.System);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 先按命令行确定工具根，之后的偏好、版面、备注与发现文件都在它下面。
            DesktopFiles.Configure(desktop.Args ?? []);
            // 目录包里宿主未运行时由前端启动它（首次运行自动初始化数据），前端关闭后宿主继续运行。
            PackagedHost.StartIfNeeded(desktop.Args ?? []);
            new DesktopPreferenceStore(DesktopPreferenceStore.DefaultPath).ApplyAndTrack(this);
            UiMotion.ReadSystemPreference();
            Pages.Inventory.InventoryNoteStore.Shared = new(Pages.Inventory.InventoryNoteStore.DefaultPath);
            Pages.Combat.CombatEffectFavorites.Use(Pages.Combat.CombatEffectFavorites.DefaultPath);
            var args = desktop.Args ?? [];
            LoadAssetPack(args);
            var backend = CreateBackend(args);
            desktop.MainWindow = CreateMainWindow(backend, new OverlayLayoutStore(OverlayLayoutStore.DefaultPath));
            if (backend is LiveBackend live)
            {
                var window = desktop.MainWindow;
                live.CopyText = text => window.Clipboard is { } clipboard
                ? clipboard.SetTextAsync(text)
                : throw new InvalidOperationException("clipboard.unavailable");
                live.Start();
                desktop.Exit += (_, _) => live.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>以指定后端组装主窗口；侧栏内容来自模块注册表。</summary>
    public static Window CreateMainWindow(IDesktopBackend backend, OverlayLayoutStore? layouts = null) =>
        new MainWindow { DataContext = new ShellViewModel(BuiltInModules.CreateCatalog(backend, layouts), backend) };

    /// <summary>默认素材包目录：工具根下的 desktopssets（存在时载入）。</summary>
    public static string DefaultAssetDirectory => DesktopFiles.File("assets");

    /// <summary>启动时载入的素材包目录：--assets 优先，其次是用户在模块与扩展页选定的目录，最后是默认目录。</summary>
    public static string AssetPackDirectory(IReadOnlyList<string> args, string? chosen)
    {
        var index = args.ToList().IndexOf("--assets");
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : chosen ?? DefaultAssetDirectory;
    }

    private static void LoadAssetPack(IReadOnlyList<string> args)
    {
        var directory = AssetPackDirectory(args, IconRegistry.ChosenDirectory);
        if (File.Exists(Path.Combine(directory, "pack.json"))) IconRegistry.LoadPack(directory);
    }

    /// <summary>
    /// 默认连接工具根下的发现文件；--portable-root 指定工具根，--instance 指定其他发现文件（例如模拟宿主），--sample 使用固定样本。
    /// </summary>
    private static IDesktopBackend CreateBackend(IReadOnlyList<string> args)
    {
        if (args.Contains("--sample")) return new SampleBackend();
        var index = args.ToList().IndexOf("--instance");
        var path = index >= 0 && index + 1 < args.Count ? args[index + 1] : LiveBackend.DefaultDiscoveryPath;
        return new LiveBackend(path, action => Dispatcher.UIThread.Post(action));
    }
}
