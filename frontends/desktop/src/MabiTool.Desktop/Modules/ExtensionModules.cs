using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Controls;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Modules;

/// <summary>
/// 把运行中扩展提供的页面与浮窗元件登记为扩展模块：每个扩展一个侧栏入口（“扩展”分组），页面依次排在入口页里，
/// 浮窗元件进入元件库（标识 <c>extension.&lt;扩展&gt;.&lt;元件&gt;</c>，沿用上锁、按显示器记忆与尺寸档）。
/// 界面集合不变时沿用原有登记，扩展停止后入口与元件随之撤下，已摆放的元件保留摆放数据，重新运行后恢复。
/// </summary>
public sealed class ExtensionModules
{
    private static readonly Color Accent = Color.Parse("#4B5563");
    private readonly ModuleCatalog catalog;
    private readonly IDesktopBackend backend;
    private readonly Dictionary<string, (string Signature, ModuleDescriptor Module)> built = new(StringComparer.Ordinal);

    private ExtensionModules(ModuleCatalog catalog, IDesktopBackend backend)
    {
        this.catalog = catalog;
        this.backend = backend;
        backend.Changed += (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess()) Sync();
            else Dispatcher.UIThread.Post(Sync);
        };
        UiText.LanguageChanged += (_, _) => { built.Clear(); Sync(); };
    }

    /// <param name="pickFile">文件请求的选择框；留空时用主窗口的系统文件选择框。</param>
    public static ExtensionModules Attach(ModuleCatalog catalog, IDesktopBackend backend, Func<string, Task<string?>>? pickFile = null)
    {
        var modules = new ExtensionModules(catalog, backend);
        if (pickFile is not null) modules.PickFile = pickFile;
        modules.Sync();
        return modules;
    }

    private readonly HashSet<string> answered = new(StringComparer.Ordinal);

    /// <summary>
    /// 为扩展的文件请求弹出系统文件选择框；参数是标题，返回所选本机文件或空（取消）。默认用主窗口的文件选择框，测试可替换。
    /// </summary>
    public Func<string, Task<string?>> PickFile { get; set; } = DefaultPickFileAsync;

    // 每个请求只弹一次框；用户选择或取消后把结果交给后端，扩展停止或请求过期时后端撤下请求。
    private void AnswerFileRequests()
    {
        var requests = backend.GetExtensionSurfaces()?.FileRequests ?? [];
        answered.IntersectWith(requests.Select(x => x.Id));
        foreach (var request in requests.Where(x => answered.Add(x.Id)))
        {
            var name = Pick(request.ExtensionNames) ?? request.ExtensionId;
            var title = UiText.Pick($"扩展 {name} 请求读取一个文件", $"擴充 {name} 請求讀取一個檔案") + (request.Title.Length > 0 ? "：" + request.Title : string.Empty);
            _ = AnswerAsync(request, title);
        }
    }

    private async Task AnswerAsync(ExtensionFileRequest request, string title)
    {
        string? path = null;
        try { path = await PickFile(title); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        await backend.AnswerExtensionFileRequestAsync(request.ExtensionId, request.Id, path, CancellationToken.None);
    }

    private static async Task<string?> DefaultPickFileAsync(string title)
    {
        if ((Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow
            is not { StorageProvider: { CanOpen: true } storage }) return null;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count == 0 ? null : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(files[0]);
    }

    public void Sync()
    {
        AnswerFileRequests();
        var surfaces = backend.GetExtensionSurfaces()?.Surfaces ?? [];
        var names = backend.GetExtensions()?.Installed.ToDictionary(x => x.Id, x => x.Package?.DisplayNames, StringComparer.Ordinal) ?? [];
        var result = new List<ModuleDescriptor>();
        foreach (var group in surfaces.GroupBy(x => x.ExtensionId).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var id = group.Key;
            var title = Pick(names.GetValueOrDefault(id)) ?? Pick(group.First().Titles) ?? id;
            var signature = title + "|" + string.Join(",", group.Select(x => x.Kind + ":" + x.Id + ":" + Pick(x.Titles) + ":" + x.Size));
            if (!built.TryGetValue(id, out var existing) || existing.Signature != signature)
            {
                var pages = group.Where(x => x.Kind == "page").Select(x => (x.Id, Title: Pick(x.Titles) ?? x.Id)).ToArray();
                var widgets = group.Where(x => x.Kind == "overlay").Select(x =>
                {
                    var surface = x.Id;
                    return new WidgetDescriptor("extension." + id + "." + surface, new(Pick(x.Titles) ?? surface), ["scale", "skin"],
                        source => new ExtensionSurfaceView(source, id, surface)) { DefaultSize = Size(x.Size), MinimumSize = new Avalonia.Size(120, 48) };
                }).ToArray();
                existing = (signature, new ModuleDescriptor("extension." + id, new(title), new(title[..1]), Accent, ModuleOrigin.Extension,
                    () => Page(id, title, pages), widgets));
                built[id] = existing;
            }
            result.Add(existing.Module);
        }
        foreach (var stale in built.Keys.Except(result.Select(x => x.Id["extension.".Length..])).ToArray()) built.Remove(stale);
        catalog.ReplaceExtensions(result);
    }

    private object Page(string id, string title, IReadOnlyList<(string Id, string Title)> pages)
    {
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(8, 4, 8, 18) };
        panel.Children.Add(new TextBlock { Classes = { "h1" }, Text = title });
        if (pages.Count == 0)
            panel.Children.Add(new TextBlock { Classes = { "note" }, Text = UiText.Pick("这个扩展只提供浮窗元件，可在浮窗版面里添加", "這個擴充只提供浮窗元件，可在浮窗版面裡新增") });
        foreach (var (surface, pageTitle) in pages)
        {
            if (pages.Count > 1) panel.Children.Add(new TextBlock { Classes = { "name", "strong" }, Text = pageTitle });
            panel.Children.Add(new ExtensionSurfaceView(backend, id, surface));
        }
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private static Avalonia.Size Size(string? size) => size switch
    {
        "large" => new(380, 240),
        "medium" => new(300, 160),
        _ => new(220, 96)
    };

    private static string? Pick(IReadOnlyDictionary<string, string>? texts)
    {
        if (texts is null || texts.Count == 0) return null;
        var (first, second) = UiText.Language == UiLanguage.TraditionalChinese ? ("zh-Hant", "zh-Hans") : ("zh-Hans", "zh-Hant");
        return texts.TryGetValue(first, out var text) ? text : texts.TryGetValue(second, out text) ? text : null;
    }
}
