using Avalonia.Controls;
using Avalonia.Media;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Overlay;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Modules;

/// <summary>Shell 表示外壳自带的入口（模块管理、设置），排在侧栏底部。</summary>
public enum ModuleOrigin { BuiltIn, Extension, Shell }

/// <summary>
/// 模块提供给浮窗版面的元件；设置项由模块声明，版面编辑器按声明生成表单。
/// <see cref="CreateView"/> 返回元件的内容；内建元件留空，由 <see cref="OverlayWidgetView"/> 按标识显示。
/// <see cref="PresentationTopic"/> 是元件依赖的展示接口主题（例如 nearby）；该主题缺失时元件在浮窗里隐藏，编辑页标注原因。
/// <see cref="DefaultSize"/> 与 <see cref="MinimumSize"/> 是元件外框在缩放前的默认与最小尺寸（逻辑像素）：拖动手柄改变尺寸时
/// 不小于最小尺寸；内建元件留空时取 <see cref="OverlayWidgetSizes"/> 的登记值；扩展元件没有声明默认尺寸时按内容的自然尺寸显示。
/// </summary>
public sealed record WidgetDescriptor(string Id, LocalizedText Title, IReadOnlyList<string> SettingKeys,
    Func<IDesktopBackend, Control>? CreateView = null, string? PresentationTopic = null)
{
    public Avalonia.Size? DefaultSize { get; init; }
    public Avalonia.Size? MinimumSize { get; init; }
}

/// <summary>
/// 一个模块在桌面前端的全部外观与入口：导航项、页面工厂、浮窗元件。
/// 扩展模块提供同一结构即可获得字章、色调、背景光晕和导航位置。
/// </summary>
public sealed class ModuleDescriptor
{
    public ModuleDescriptor(string id, LocalizedText title, LocalizedText glyph, Color accent, ModuleOrigin origin,
        Func<object> createPage, IReadOnlyList<WidgetDescriptor>? widgets = null, string? iconAssetId = null, bool pending = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title.Simplified);
        Id = id;
        Title = title;
        Glyph = glyph;
        Accent = accent;
        Origin = origin;
        CreatePage = createPage;
        Widgets = widgets ?? [];
        IconAssetId = iconAssetId;
        Pending = pending;
        AccentBrush = new SolidColorBrush(accent);
        lightHalo = Halo(accent, 0x99);
        lightText = AccentBrush;
        darkText = new SolidColorBrush(UiTheme.TextTone(accent, dark: true));
    }

    private readonly IBrush lightHalo;
    private readonly IBrush lightText;
    private readonly IBrush darkText;

    public string Id { get; }
    public LocalizedText Title { get; }
    /// <summary>字章：没有自定义图示时显示的一个字。</summary>
    public LocalizedText Glyph { get; }
    public Color Accent { get; }
    public IBrush AccentBrush { get; }
    /// <summary>当前主题下的背景主光晕：浅色为模块色调 60%；深色为固定的蓝色主光晕，不随模块变化。</summary>
    public IBrush HaloBrush => HaloFor(UiTheme.IsDark);
    /// <summary>当前主题下把色调当文字颜色用的画笔：深色下与白色按 45% 混合。</summary>
    public IBrush TextBrush => TextFor(UiTheme.IsDark);
    public IBrush HaloFor(bool dark) => dark ? UiTheme.DarkMainHalo : lightHalo;
    public IBrush TextFor(bool dark) => dark ? darkText : lightText;
    public ModuleOrigin Origin { get; }
    public Func<object> CreatePage { get; }
    public IReadOnlyList<WidgetDescriptor> Widgets { get; }
    /// <summary>自定义模块图示的资产 ID；为空时使用字章。</summary>
    public string? IconAssetId { get; }
    /// <summary>模块登记上的静态标记：核心功能尚未实现。侧栏把这类模块折叠进“未实现模块”分组，元件放进“需要游戏数据的元件”层。</summary>
    public bool Pending { get; }
    /// <summary>图标槽名：素材包按这个名字替换图片与动效。</summary>
    public string IconSlot => IconAssetId ?? "module." + Id;

    private static RadialGradientBrush Halo(Color accent, byte alpha) => new()
    {
        Center = new Avalonia.RelativePoint(0.08, 0.06, Avalonia.RelativeUnit.Relative),
        GradientOrigin = new Avalonia.RelativePoint(0.08, 0.06, Avalonia.RelativeUnit.Relative),
        RadiusX = new Avalonia.RelativeScalar(0.55, Avalonia.RelativeUnit.Relative),
        RadiusY = new Avalonia.RelativeScalar(0.65, Avalonia.RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(alpha, accent.R, accent.G, accent.B), 0),
            new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1)
        }
    };
}

public sealed class ModuleCatalog
{
    private readonly List<ModuleDescriptor> modules = [];

    public IReadOnlyList<ModuleDescriptor> Modules => modules;

    /// <summary>游戏浮窗的控制器，由内建模块注册时建立。</summary>
    public OverlayController? Overlay { get; set; }

    /// <summary>迷你窗的控制器，由内建模块注册时建立；乐谱库页从这里打开迷你窗。</summary>
    public Shell.MiniWindowController? Mini { get; set; }

    /// <summary>扩展模块注册或撤下后触发；侧栏与浮窗画布据此更新入口与元件。</summary>
    public event EventHandler? Changed;

    public void Register(ModuleDescriptor module)
    {
        if (modules.Any(existing => string.Equals(existing.Id, module.Id, StringComparison.Ordinal)))
            throw new InvalidOperationException($"模块 ID 已存在：{module.Id}");
        modules.Add(module);
    }

    /// <summary>
    /// 把扩展模块换成 <paramref name="extensions"/>：只增删与替换 <see cref="ModuleOrigin.Extension"/> 的条目，内建与外壳入口不变。
    /// 集合没有变化时不触发 <see cref="Changed"/>。
    /// </summary>
    public void ReplaceExtensions(IReadOnlyList<ModuleDescriptor> extensions)
    {
        var current = modules.Where(module => module.Origin == ModuleOrigin.Extension).ToList();
        if (current.Count == extensions.Count && current.Zip(extensions).All(pair => ReferenceEquals(pair.First, pair.Second))) return;
        modules.RemoveAll(module => module.Origin == ModuleOrigin.Extension);
        foreach (var module in extensions)
        {
            if (module.Origin != ModuleOrigin.Extension || modules.Any(existing => existing.Id == module.Id))
                throw new InvalidOperationException($"扩展模块无效或 ID 重复：{module.Id}");
            modules.Add(module);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
