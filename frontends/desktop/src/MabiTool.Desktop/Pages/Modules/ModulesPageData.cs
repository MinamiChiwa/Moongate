using System.Windows.Input;
using Avalonia.Media;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Pages.Modules;

/// <summary>
/// 后端报告的模块状态；<see cref="Problems"/> 是后端给出的原因，原样显示。<see cref="Name"/> 与 <see cref="Description"/>
/// 取自模块清单的 <c>displayNames</c>、<c>descriptions</c>（按界面语言选取），旧宿主没有时为空。
/// <see cref="Essential"/> 取自模块清单的 <c>essential</c>：工具运行所必需的核心模块，始终启用，页面不提供停用入口。
/// </summary>
public sealed record ModuleState(string Id, string Version, bool Enabled, string State, string Problems,
    string? Name = null, string? Description = null, bool Essential = false);

/// <summary><see cref="Extensions"/> 是后端的扩展列表；后端没有扩展接口时为空，<see cref="CanInstall"/> 随之为假。</summary>
public sealed record ModulesPageData(bool Connected, IReadOnlyList<ModuleState> Modules, bool CanToggle, bool CanInstall,
    MabiTool.Contracts.ExtensionCatalog? Extensions = null);

public sealed record ModuleCard(string Id, string Glyph, IBrush Accent, string Name, string Source, string ToggleLabel,
    string State, string Adds, bool CanToggle, string Description = "", bool IsEssential = false)
{
    public string ToggleIntent => "modules.toggle:" + Id;
    public string Slot => "module." + Id;
    public bool HasDescription => Description.Length > 0;
    /// <summary>可启停的模块显示启停按钮；核心模块改为显示“核心模块，始终启用”。</summary>
    public bool ShowsToggle => !IsEssential;
}

/// <summary>按界面语言从模块清单的多语言文字里取一条：先取当前语言，没有时取另一种中文，都没有时为空。</summary>
public static class ModuleText
{
    public static string? Pick(IReadOnlyDictionary<string, string>? texts)
    {
        if (texts is null || texts.Count == 0) return null;
        var (first, second) = UiText.Language == UiLanguage.TraditionalChinese ? ("zh-Hant", "zh-Hans") : ("zh-Hans", "zh-Hant");
        return texts.TryGetValue(first, out var text) && text.Length > 0 ? text
            : texts.TryGetValue(second, out text) && text.Length > 0 ? text : null;
    }
}

public sealed record ExtensionPoint(string Name, string Description);

public sealed record AssetSlot(string Name, string Note, string Current);

/// <summary>
/// 模块卡片由前端的模块注册表（名称、字章、色调、页面与浮窗元件数量）和后端的模块状态合并而成；
/// 只有后端知道的模块以标识显示。
/// </summary>
public sealed partial class ModulesPageModel : BackendPageModel<ModulesPageData>
{
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#8A90A3"));
    /// <summary>切走后保留页面，回来时直接显示并重读一次。</summary>
    protected override bool Cacheable => true;

    private readonly ModuleCatalog catalog;
    private IReadOnlyList<string> skipped = [];
    private string packMessage = string.Empty;

    public ModulesPageModel(IDesktopBackend backend, ModuleCatalog catalog) : base(backend, source => source.GetModules())
    {
        this.catalog = catalog;
        ImportAssetsCommand = new RelayCommand(_ => _ = ImportAsync());
        RemoveAssetsCommand = new RelayCommand(_ => RemovePack());
        InstallExtensionCommand = new RelayCommand(_ => _ = PickAndPreviewAsync());
        InspectPendingCommand = new RelayCommand(name => _ = PreviewAsync(null, name as string));
        ConfirmInstallCommand = new RelayCommand(_ => _ = ConfirmInstallAsync());
        CancelPreviewCommand = new RelayCommand(_ => ClearPreview(string.Empty));
        ToggleExtensionCommand = new RelayCommand(id => _ = ToggleExtensionAsync(id as string));
        UninstallExtensionCommand = new RelayCommand(id => _ = UninstallAsync(id as string));
        UseAssetPackCommand = new RelayCommand(id => UseAssetPack(id as string));
        OpenExtensionsFolderCommand = new RelayCommand(_ => _ = OpenExtensionsFolderAsync());
        ToggleGrantCommand = new RelayCommand(row => _ = ToggleGrantAsync(row as PermissionRow));
        RestartExtensionCommand = new RelayCommand(id => _ = RestartAsync(id as string));
        ShowLogsCommand = new RelayCommand(id => _ = ShowLogsAsync(id as string));
        ToggleDevelopmentCommand = new RelayCommand(_ => _ = ToggleDevelopmentAsync());
        LoadDevelopmentCommand = new RelayCommand(_ => _ = LoadDevelopmentAsync());
        OnDataChanged();
    }

    /// <summary>选择素材包目录；由页面提供系统的目录选择框，取消时返回空。</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public ICommand ImportAssetsCommand { get; }
    public ICommand RemoveAssetsCommand { get; }

    /// <summary>素材包是本机目录，载入与移除都在前端完成，不经过后端。</summary>
    public string PackStatus => IconRegistry.PackName is { } name
        ? UiText.Pick($"当前素材包：{name}", $"目前素材包：{name}")
        : UiText.Pick("没有素材包，使用内建外观", "沒有素材包，使用內建外觀");

    public string PackPath => IconRegistry.PackDirectory ?? string.Empty;
    public bool HasPack => IconRegistry.PackName is not null;

    /// <summary>最近一次载入时被跳过的条目及原因。</summary>
    public IReadOnlyList<string> SkippedEntries => skipped;
    public bool HasSkipped => skipped.Count > 0;
    public string PackMessage => packMessage;
    public bool HasPackMessage => packMessage.Length > 0;

    /// <summary>载入一个素材包目录并记为用户的选择；不合格的条目跳过并列出原因。</summary>
    public void LoadPack(string directory)
    {
        var loaded = IconRegistry.Choose(directory, out var problems);
        skipped = loaded ? problems : [];
        packMessage = loaded
            ? problems.Count == 0
                ? UiText.Pick("已载入，全部条目生效", "已載入，全部條目生效")
                : UiText.Pick($"已载入，跳过 {problems.Count} 项", $"已載入，略過 {problems.Count} 項")
            : UiText.Pick($"没有载入：{string.Join("；", problems)}", $"沒有載入：{string.Join("；", problems)}");
        RaisePack();
    }

    private async Task ImportAsync()
    {
        if (PickFolder is null || await PickFolder() is not { Length: > 0 } directory) return;
        LoadPack(directory);
    }

    private void RemovePack()
    {
        IconRegistry.Remove();
        skipped = [];
        packMessage = UiText.Pick("已移除素材包，回到内建外观", "已移除素材包，回到內建外觀");
        RaisePack();
    }

    private void RaisePack()
    {
        foreach (var name in new[] { nameof(PackStatus), nameof(PackPath), nameof(HasPack), nameof(SkippedEntries),
                     nameof(HasSkipped), nameof(PackMessage), nameof(HasPackMessage), nameof(AssetSlots) })
            Raise(name);
    }

    public IReadOnlyList<ModuleCard> Cards
    {
        get
        {
            var states = Data.Modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
            var known = catalog.Modules.Where(module => module.Origin != ModuleOrigin.Shell).Select(module =>
            {
                states.Remove(module.Id, out var state);
                return new ModuleCard(module.Id, module.Glyph.Current, module.AccentBrush, module.Title.Current,
                    module.Origin == ModuleOrigin.BuiltIn ? UiText.Pick("内建", "內建") : UiText.Pick("扩展模块", "擴充模組"),
                    ToggleLabel(state), module.Pending ? UiText.Pick("未实现，", "未實作，") + StateText(state) : StateText(state), Adds(module),
                    Data.CanToggle && state is { Essential: false }, state?.Description ?? string.Empty, state?.Essential == true);
            }).ToList();
            // 只有后端有的模块：名称与功能说明取自模块清单，标识放在来源一行；旧宿主没有名称时以标识显示。
            known.AddRange(states.Values.Select(state => new ModuleCard(state.Id, "?", Neutral, state.Name ?? CodeText.Module(state.Id),
                UiText.Pick("后端模块，没有前端页面", "後端模組，沒有前端頁面"), ToggleLabel(state), StateText(state),
                UiText.Pick($"版本 {state.Version}", $"版本 {state.Version}"), Data.CanToggle && !state.Essential,
                state.Description ?? UiText.Pick("后端没有提供功能说明", "後端沒有提供功能說明"), state.Essential)));
            return known;
        }
    }

    public IReadOnlyList<ExtensionPoint> ExtensionPoints =>
    [
        new(UiText.Pick("导航", "導覽"), UiText.Pick("侧栏“扩展模块”区的入口，字章与色调可自定", "側欄「擴充模組」區的入口，字章與色調可自訂")),
        new(UiText.Pick("页面", "頁面"), UiText.Pick("用同一套玻璃面板与格线组成的模块页", "用同一套玻璃面板與格線組成的模組頁")),
        new(UiText.Pick("浮窗元件", "浮窗元件"), UiText.Pick("出现在浮窗版面的元件库，可拖放与设置", "出現在浮窗版面的元件庫，可拖放與設定")),
        new(UiText.Pick("设置", "設定"), UiText.Pick("依模块提供的设置结构自动生成表单", "依模組提供的設定結構自動產生表單")),
        new(UiText.Pick("活动栏", "活動列"), UiText.Pick("需要游戏输入的模块共用同一条活动栏与紧急停止", "需要遊戲輸入的模組共用同一條活動列與緊急停止")),
        new(UiText.Pick("快捷键", "快捷鍵"), UiText.Pick("登记到统一的快捷键清单，冲突时提示", "登記到統一的快捷鍵清單，衝突時提示"))
    ];

    public IReadOnlyList<AssetSlot> AssetSlots
    {
        get
        {
            var builtIn = UiText.Pick("目前：内建", "目前：內建");
            // 某种素材在当前素材包里有生效的条目时显示“素材包”与条目数，否则是内建外观。
            string From(AssetKind kind) => IconRegistry.Count(kind) is var count and > 0
                ? UiText.Pick($"素材包：{count} 张", $"素材包：{count} 張")
                : builtIn;
            return
            [
                new(UiText.Pick("窗口背景", "視窗背景"), UiText.Pick("每个模块可各自一张", "每個模組可各自一張"), From(AssetKind.Background)),
                new(UiText.Pick("模块图标", "模組圖示"), UiText.Pick("图片与动效都可替换", "圖片與動效都可替換"),
                    IconRegistry.PackName is { } pack ? UiText.Pick($"素材包：{pack}", $"素材包：{pack}") : builtIn),
                new(UiText.Pick("效果图标包", "效果圖示包"), UiText.Pick("按效果编号对应", "依效果編號對應"), From(AssetKind.Effect)),
                new(UiText.Pick("清单封面", "清單封面"), UiText.Pick("按乐谱清单对应", "依樂譜清單對應"), From(AssetKind.Playlist)),
                new(UiText.Pick("浮窗外框", "浮窗外框"), UiText.Pick("在浮窗版面页逐个元件设置", "在浮窗版面頁逐個元件設定"),
                    UiText.Pick("目前：逐个元件", "目前：逐個元件")),
                new(UiText.Pick("乐器图标", "樂器圖示"), UiText.Pick("按乐器目录对应", "依樂器目錄對應"), From(AssetKind.Instrument))
            ];
        }
    }

    public bool Disconnected => !Data.Connected;

    private static string ToggleLabel(ModuleState? state) => state switch
    {
        null => UiText.Pick("状态未知", "狀態未知"),
        { Essential: true } => UiText.Pick("核心模块，始终启用", "核心模組，始終啟用"),
        { Enabled: true } => UiText.Pick("已启用", "已啟用"),
        _ => UiText.Pick("已停用", "已停用")
    };

    private static string StateText(ModuleState? state) => state switch
    {
        null => UiText.Pick("状态未知", "狀態未知"),
        { Problems.Length: > 0 } => $"{state.State}：{state.Problems}",
        _ => UiText.Pick($"{state.State}，版本 {state.Version}", $"{state.State}，版本 {state.Version}")
    };

    private static string Adds(ModuleDescriptor module) => module.Widgets.Count == 0
        ? UiText.Pick("提供 1 个页面", "提供 1 個頁面")
        : UiText.Pick($"提供 1 个页面、{module.Widgets.Count} 个浮窗元件", $"提供 1 個頁面、{module.Widgets.Count} 個浮窗元件");
}
