using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Pages.Modules;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Shell;

public sealed class NavigationItem(ModuleDescriptor module, ICommand select) : ObservableObject
{
    private bool isSelected;
    public ModuleDescriptor Module { get; } = module;
    public ICommand SelectCommand { get; } = select;
    public string Title => Module.Title.Current;
    public string Glyph => Module.Glyph.Current;
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }

    public void RefreshText()
    {
        Raise(nameof(Title));
        Raise(nameof(Glyph));
    }
}

public sealed class StageViewModel(LocalizedText doneText, LocalizedText pendingText, EffectStage stage)
{
    public string Text => stage switch
    {
        EffectStage.Done => doneText.Current,
        EffectStage.Unknown => UiText.Pick("效果未知", "效果未知"),
        _ => pendingText.Current
    };
    public bool IsDone => stage == EffectStage.Done;
    public bool IsUnknown => stage == EffectStage.Unknown;
    public bool IsPending => stage == EffectStage.Pending;
}

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ModuleCatalog catalog;
    private readonly IDesktopBackend backend;
    private NavigationItem selected;

    public ShellViewModel(ModuleCatalog catalog, IDesktopBackend backend)
    {
        this.catalog = catalog;
        this.backend = backend;
        SelectCommand = new RelayCommand(item => { if (item is NavigationItem navigation) Select(navigation); });
        EmergencyStopCommand = new RelayCommand(_ => _ = backend.RequestEmergencyStopAsync(CancellationToken.None));
        UseSimplifiedCommand = new RelayCommand(_ => UseLanguage(UiLanguage.SimplifiedChinese));
        UseTraditionalCommand = new RelayCommand(_ => UseLanguage(UiLanguage.TraditionalChinese));
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        // 未抓包的原因是 Npcap 缺失时，按下抓包状态打开安装指引；其余时候前往设置页抓包区。
        OpenCaptureSettingsCommand = new RelayCommand(_ =>
        {
            if (Capture.NpcapMissingReason) NpcapGuide.Show();
            else ShellRequests.OpenCaptureSettings();
        });
        // 模块入口按保存的顺序排列（各分组内排序，不跨分组），没有保存过的模块排在末尾、保持注册顺序。
        // 每个分组保留全部入口的顺序；侧栏只列出其中没有被停用的入口，重新启用的模块回到原来的位置。
        allBuiltIn = Ordered(ItemsFor(ModuleOrigin.BuiltIn, pending: false));
        allExtensions = Ordered(ItemsFor(ModuleOrigin.Extension, pending: false));
        allPending = Ordered(catalog.Modules.Where(m => m.Origin != ModuleOrigin.Shell && m.Pending)
            .Select(m => new NavigationItem(m, SelectCommand)).ToList());
        disabledModules = DisabledModules(backend.GetModules(), new HashSet<string>());
        BuiltIn = new(allBuiltIn.Where(Shown));
        Extensions = new(allExtensions.Where(Shown));
        PendingModules = new(allPending.Where(Shown));
        ShellEntries = ItemsFor(ModuleOrigin.Shell, pending: false);
        // 默认页是排在第一位的已实现内建模块；未实现的模块不作为启动页。
        selected = DefaultEntry();
        Activity = backend.GetActivity();
        Binding = backend.GetGameBinding();
        Capture = backend.GetCaptureStatus();
        UpdateRelease = AvailableUpdate(backend);
        GamePicker = new GamePickerModel(backend);
        NpcapGuide.Observe(Capture);
        ShellRequests.NpcapGuideRequested += NpcapGuide.Show;
        Select(selected);
        if (PrebuildOnIdle) Avalonia.Threading.Dispatcher.UIThread.Post(PrebuildNext, Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        UiText.LanguageChanged += OnLanguageChanged;
        UiTheme.Changed += OnThemeChanged;
        IconRegistry.Changed += OnPackChanged;
        backend.Changed += OnBackendChanged;
        backend.ShortcutTriggered += OnShortcutTriggered;
        catalog.Changed += OnCatalogChanged;
    }

    // 运行中扩展提供的页面随扩展启停出现或消失：只增删扩展分组里的入口，保留已有入口与其排序；当前页所属扩展消失时回到默认页。
    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        var current = catalog.Modules.Where(m => m.Origin == ModuleOrigin.Extension && !m.Pending).ToList();
        allExtensions.RemoveAll(item => !current.Contains(item.Module));
        foreach (var module in current.Where(module => allExtensions.All(item => item.Module != module)))
            allExtensions.Add(new NavigationItem(module, SelectCommand));
        Sync(Extensions, allExtensions);
        Raise(nameof(HasExtensions));
        if (selected.Module.Origin == ModuleOrigin.Extension && !current.Contains(selected.Module)) Select(DefaultEntry());
    }

    /// <summary>模块注册表；主窗口借它把自己交给迷你窗，迷你窗据此回到主窗口。</summary>
    public ModuleCatalog Catalog => catalog;

    /// <summary>素材包为当前模块提供的窗口背景；叠在底色与光晕之上、面板之下。</summary>
    public IImage? BackdropImage => IconRegistry.Backdrop(selected.Module.Id)?.Image;

    /// <summary>背景图上随主题的遮罩，透明度按图里最暗与最亮处保证文字对比度。</summary>
    public IBrush? BackdropScrim => UiTheme.BackdropScrim(IconRegistry.Backdrop(selected.Module.Id));

    public bool HasBackdrop => IconRegistry.Backdrop(selected.Module.Id) is not null;

    private readonly List<NavigationItem> allBuiltIn;
    private readonly List<NavigationItem> allExtensions;
    private readonly List<NavigationItem> allPending;
    private IReadOnlySet<string> disabledModules;

    /// <summary>侧栏列出的内建模块入口：不含后端报告为已停用的模块。</summary>
    public ObservableCollection<NavigationItem> BuiltIn { get; }
    public ObservableCollection<NavigationItem> Extensions { get; }
    public IReadOnlyList<NavigationItem> ShellEntries { get; }
    public bool HasExtensions => Extensions.Count > 0;

    /// <summary>核心功能尚未实现的模块，折叠在侧栏“未实现模块”分组里；同样不含已停用的模块。</summary>
    public ObservableCollection<NavigationItem> PendingModules { get; }

    /// <summary>
    /// 后端报告为已停用（<see cref="ModuleState.Enabled"/> 为假）的模块。后端没有报告的模块按未知处理，照常显示；
    /// 未连接后端时沿用上一次读到的结果。
    /// </summary>
    private static IReadOnlySet<string> DisabledModules(ModulesPageData data, IReadOnlySet<string> previous) => data.Connected
        ? data.Modules.Where(module => !module.Enabled).Select(module => module.Id).ToHashSet(StringComparer.Ordinal)
        : previous;

    private bool Shown(NavigationItem item) => !disabledModules.Contains(item.Module.Id);

    private NavigationItem DefaultEntry() => BuiltIn.Concat(Extensions).Concat(ShellEntries).Concat(PendingModules).FirstOrDefault()
        ?? throw new InvalidOperationException("至少需要一个模块");

    /// <summary>模块启用状态变化后重新列出侧栏入口；当前页所属模块被停用时回到默认页。</summary>
    private void RefreshEnabledModules()
    {
        var disabled = DisabledModules(backend.GetModules(), disabledModules);
        if (disabled.SetEquals(disabledModules)) return;
        disabledModules = disabled;
        Sync(BuiltIn, allBuiltIn);
        Sync(Extensions, allExtensions);
        Sync(PendingModules, allPending);
        Raise(nameof(HasExtensions));
        Raise(nameof(HasPending));
        if (selected.Module.Origin != ModuleOrigin.Shell && !Shown(selected)) Select(DefaultEntry());
    }

    // 只插入与移除变化的入口，其余入口的容器保留，不打断侧栏的选中与动效。
    private void Sync(ObservableCollection<NavigationItem> visible, List<NavigationItem> all)
    {
        var wanted = all.Where(Shown).ToList();
        for (var i = visible.Count - 1; i >= 0; i--)
            if (!wanted.Contains(visible[i])) visible.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
            if (i >= visible.Count || visible[i] != wanted[i]) visible.Insert(i, wanted[i]);
    }

    /// <summary>模块入口所在的可排序分组（内建、扩展、未实现）；底部的外壳入口不参与排序，返回空。</summary>
    public ObservableCollection<NavigationItem>? GroupOf(NavigationItem item) =>
        new[] { BuiltIn, Extensions, PendingModules }.FirstOrDefault(group => group.Contains(item));

    private List<NavigationItem> AllOf(ObservableCollection<NavigationItem> group) =>
        group == BuiltIn ? allBuiltIn : group == Extensions ? allExtensions : allPending;

    /// <summary>
    /// 把模块入口移到所在分组的 <paramref name="index"/> 位置（不跨分组），并把新顺序写入偏好文件。
    /// 返回是否移动。
    /// </summary>
    public bool MoveModule(NavigationItem item, int index)
    {
        if (GroupOf(item) is not { } group) return false;
        var from = group.IndexOf(item);
        index = Math.Clamp(index, 0, group.Count - 1);
        if (from == index) return false;
        group.Move(from, index);
        // 隐藏的（已停用的）入口留在原来的位置，显示的入口按新顺序依次填回它们原来占用的位置。
        var all = AllOf(group);
        var slots = all.Select((entry, position) => (entry, position)).Where(pair => group.Contains(pair.entry))
            .Select(pair => pair.position).ToList();
        for (var i = 0; i < slots.Count; i++) all[slots[i]] = group[i];
        SidebarPreference.SetModuleOrder(allBuiltIn.Concat(allExtensions).Concat(allPending).Select(entry => entry.Module.Id).ToList());
        return true;
    }

    private static List<NavigationItem> Ordered(IReadOnlyList<NavigationItem> items)
    {
        var saved = SidebarPreference.ModuleOrder.Select((id, position) => (id, position))
            .GroupBy(pair => pair.id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().position, StringComparer.Ordinal);
        int Rank(NavigationItem item) => saved.TryGetValue(item.Module.Id, out var position) ? position : int.MaxValue;
        return items.Select((item, registered) => (item, registered)).OrderBy(pair => Rank(pair.item))
            .ThenBy(pair => pair.registered).Select(pair => pair.item).ToList();
    }
    public bool HasPending => PendingModules.Count > 0;

    /// <summary>
    /// 分组是否展开：取用户的选择（保存在偏好文件，缺省收起）；当前选中的模块在分组内时保持展开。
    /// </summary>
    public bool IsPendingOpen
    {
        get => SidebarPreference.PendingExpanded || PendingModules.Contains(selected);
        set
        {
            SidebarPreference.SetPendingExpanded(value);
            // 选中的模块在分组内时分组仍展开：开关的选中状态在本次设置结束后再回到实际状态，避免在绑定写入过程中回写。
            if (value != IsPendingOpen) Avalonia.Threading.Dispatcher.UIThread.Post(() => Raise(nameof(IsPendingOpen)));
            else Raise(nameof(IsPendingOpen));
        }
    }
    public ICommand SelectCommand { get; }
    public ICommand EmergencyStopCommand { get; }
    public ICommand UseSimplifiedCommand { get; }
    public ICommand UseTraditionalCommand { get; }

    /// <summary>窗口右上角的主题按钮：在浅色与深色之间切换。</summary>
    public ICommand ToggleThemeCommand { get; }

    /// <summary>当前实际生效的是深色；主题按钮据此显示太阳（切到浅色）或月亮（切到深色）。</summary>
    public bool IsDarkTheme => UiTheme.IsDark;
    public bool IsLightTheme => !UiTheme.IsDark;
    public bool IsSimplified => UiText.Language == UiLanguage.SimplifiedChinese;
    public bool IsTraditional => UiText.Language == UiLanguage.TraditionalChinese;
    public BindingView Binding { get; private set; }
    public ActivitySnapshot Activity { get; private set; }

    /// <summary>
    /// 窗口左上角的全局抓包状态：未抓包及原因、抓包中但未识别角色（提示切换地图）、已识别时显示当前角色名（游戏原文）。
    /// 点击前往设置页抓包区。
    /// </summary>
    public CaptureStatusView Capture { get; private set; } = CaptureStatusView.Offline;
    public string CaptureLine => CaptureText.Shell(Capture);
    public bool CaptureIdentified => Capture is { Running: true, Character.Length: > 0 };
    public bool CaptureRunning => Capture.Running;

    /// <summary>抓包状态下方的“地图 · 频道”一行；地图、频道是全局信息，只在侧栏显示。</summary>
    public LocationText Place => CaptureText.Place(Capture.Location);
    public ICommand OpenCaptureSettingsCommand { get; }

    /// <summary>抓包状态的悬停提示与读屏名称：Npcap 缺失时按下打开安装指引，其余时候前往设置。</summary>
    public string CaptureTip => Capture.NpcapMissingReason
        ? UiText.Pick("未安装 Npcap，点击查看安装指引", "未安裝 Npcap，點擊查看安裝指引")
        : UiText.Pick("抓包状态，点击前往设置", "抓包狀態，點擊前往設定");

    /// <summary>缺少 Npcap 时的安装指引弹出层。</summary>
    public NpcapGuideModel NpcapGuide { get; } = new();

    /// <summary>
    /// 窗口右上角更新提示的版本：自动检查更新开启且后端报告发现了新版本时为该版本标识，其余时候为空。
    /// 取自设置页同一份更新状态与策略，随后端状态刷新，不另外查询。
    /// </summary>
    public string? UpdateRelease { get; private set; }
    public bool HasUpdate => UpdateRelease is not null;
    public string UpdateLabel => UpdateRelease is { } release
        ? UiText.Pick($"有新版本 {release}", $"有新版本 {release}")
        : string.Empty;

    private static string? AvailableUpdate(IDesktopBackend source)
    {
        var settings = source.GetSettings();
        return settings.Policy is { AutomaticChecks: true } && settings.AvailableRelease is { Length: > 0 } release ? release : null;
    }

    /// <summary>侧栏绑定卡打开的选择游戏窗口弹出层。</summary>
    public GamePickerModel GamePicker { get; }

    /// <summary>持有输入的是已注册模块时显示模块名称，否则显示后端给出的说明。</summary>
    public string OwnerLine => OwnerModule is { } owner
        ? UiText.Pick($"{owner.Title.Current} 持有游戏输入", $"{owner.Title.Current} 持有遊戲輸入")
        : Activity.OwnerLine;
    /// <summary>占用说明的颜色：已注册模块用其色调（深色下提亮），否则用次要文字色。</summary>
    public IBrush? ActivityOwnerBrush => OwnerModule?.TextBrush ?? UiTheme.Brush("MutedBrush");
    public IBrush ModuleHaloBrush => selected.Module.HaloBrush;
    // 三段状态只在阶段变化时换新列表；例行刷新不重建活动胶囊里的项。
    public IReadOnlyList<StageViewModel> Stages
    {
        get
        {
            var key = (Activity.Accepted, Activity.Dispatched, Activity.Observed, UiText.Language);
            if (stages is not null && stagesKey == key) return stages;
            stagesKey = key;
            return stages =
            [
                new(new("已受理", "已受理"), new("尚未受理", "尚未受理"), Activity.Accepted),
                new(new("已送出", "已送出"), new("尚未送出", "尚未送出"), Activity.Dispatched),
                new(new("已观测生效", "已觀測生效"), new("尚未观测", "尚未觀測"), Activity.Observed)
            ];
        }
    }
    private IReadOnlyList<StageViewModel>? stages;
    private (EffectStage, EffectStage, EffectStage, UiLanguage)? stagesKey;
    public bool ShowStages => Activity.OwnerModuleId is not null;
    public NavigationItem Selected => selected;

    /// <summary>页面区显示的外框（<see cref="PageFrame"/>）：每个模块一个，建立后一直复用。</summary>
    public PageFrame? CurrentFrame
    {
        get => currentFrame;
        private set
        {
            var previous = currentFrame;
            if (!Set(ref currentFrame, value)) return;
            previous?.Leave();
            Raise(nameof(CurrentPage));
        }
    }

    /// <summary>当前模块已经建立的页面；切换后、页面在调度帧里建立之前为空（外框显示骨架）。</summary>
    public object? CurrentPage => currentFrame?.Page;

    private PageFrame? currentFrame;
    private bool windowShown;

    /// <summary>
    /// 主窗口显示或最小化、隐藏：向后端报告可见处 <c>main</c>，最小化期间当前页面停止接收通知与本页专用的读取（保留界面与数据），
    /// 还原后重新激活并补读一次。
    /// </summary>
    public void SetWindowShown(bool shown)
    {
        if (windowShown == shown || disposed && shown) return;
        windowShown = shown;
        backend.SetSurfaceVisible("main", shown);
        if (shown) currentFrame?.Resume();
        else currentFrame?.Suspend();
    }

    // 每个模块的页面外框；页面是否在切走后保留由外框按页面模型的 IPageLifecycle.Cacheable 决定。
    private readonly Dictionary<string, PageFrame> frames = new(StringComparer.Ordinal);

    /// <summary>模块的页面外框（没有时建立）；测试据此观察骨架与页面的建立。</summary>
    public PageFrame FrameFor(ModuleDescriptor module)
    {
        if (!frames.TryGetValue(module.Id, out var frame))
        {
            frame = new PageFrame(module);
            frame.Built += (sender, _) => { if (ReferenceEquals(sender, currentFrame)) Raise(nameof(CurrentPage)); };
            frames[module.Id] = frame;
        }
        return frame;
    }

    private void Show(ModuleDescriptor module)
    {
        var frame = FrameFor(module);
        CurrentFrame = frame;
        frame.Show();
        if (!frame.ShowsSkeleton) Raise(nameof(CurrentPage));
    }

    // 释放所有外框里的页面（换语言、关闭窗口）；外框本身保留。
    /// <summary>
    /// 启动后空闲时预建的常用页面（侧栏里可见、当前没有显示的才建）：DPS检测、合奏、背包。每个页面单独一个空闲任务，
    /// 建好后立即停用；第一次点开时不再建立控件树与模型，只挂上并分块排版。测试宿主关闭它，避免每个用例多建三个页面。
    /// </summary>
    public static bool PrebuildOnIdle { get; set; } = true;

    internal static readonly string[] PrebuiltModules = ["combat-observation", "rooms", "inventory"];

    private int prebuilt;
    private bool disposed;

    private void PrebuildNext()
    {
        if (disposed) return;
        while (prebuilt < PrebuiltModules.Length)
        {
            var id = PrebuiltModules[prebuilt++];
            var entry = BuiltIn.FirstOrDefault(item => item.Module.Id == id);
            if (entry is null || ReferenceEquals(entry, selected)) continue;
            FrameFor(entry.Module).Prebuild();
            break;
        }
        if (prebuilt < PrebuiltModules.Length)
            Avalonia.Threading.Dispatcher.UIThread.Post(PrebuildNext, Avalonia.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ReleasePages()
    {
        foreach (var frame in frames.Values) frame.Release();
    }

    private ModuleDescriptor? OwnerModule => catalog.Modules.FirstOrDefault(m => m.Id == Activity.OwnerModuleId);

    /// <summary>打开设置页（已在设置页时不重建）。</summary>
    public void OpenSettings()
    {
        if (selected.Module.Id != "shell.settings" && ShellEntries.FirstOrDefault(item => item.Module.Id == "shell.settings") is { } settings)
            Select(settings);
    }

    public void Select(NavigationItem item)
    {
        // 再次选中当前模块时重新建立页面（与原先一致）：释放旧页面，外框回到骨架后再建立。
        if (ReferenceEquals(item, selected) && currentFrame is { } same && same.Module == item.Module) same.Release();
        selected.IsSelected = false;
        selected = item;
        selected.IsSelected = true;
        Raise(nameof(Selected));
        Raise(nameof(IsPendingOpen));
        Raise(nameof(ModuleHaloBrush));
        RaiseBackdrop();
        Show(item.Module);
    }

    public void Dispose()
    {
        disposed = true;
        UiText.LanguageChanged -= OnLanguageChanged;
        UiTheme.Changed -= OnThemeChanged;
        IconRegistry.Changed -= OnPackChanged;
        backend.Changed -= OnBackendChanged;
        backend.ShortcutTriggered -= OnShortcutTriggered;
        ShellRequests.NpcapGuideRequested -= NpcapGuide.Show;
        SetWindowShown(false);
        // 关闭窗口时当前页面与保留的页面都退订，并释放它们持有的后端资源（例如取色用的冻结画面）。
        ReleasePages();
        catalog.Overlay?.Hide();
        catalog.Mini?.Hide();
    }

    private void OnPackChanged(object? sender, EventArgs e) => RaiseBackdrop();

    private void RaiseBackdrop()
    {
        Raise(nameof(BackdropImage));
        Raise(nameof(BackdropScrim));
        Raise(nameof(HasBackdrop));
    }

    private void OnBackendChanged(object? sender, EventArgs e)
    {
        RefreshBackendState(onlyChanges: true);
        RefreshEnabledModules();
    }

    private void OnShortcutTriggered(object? sender, string actionId)
    {
        if (actionId == "overlay-toggle") catalog.Overlay?.Toggle();
        else if (actionId == "overlay-click-through-toggle") catalog.Overlay?.ToggleAllLocks();
        // 定时测量：空闲时开始，预备或计时中再按为取消；由后端判定当前阶段。
        else if (actionId == "combat-measurement-toggle") _ = backend.SendIntentAsync("combat.measure.toggle", null, CancellationToken.None);
    }

    /// <param name="onlyChanges">后端通知：活动、绑定与抓包状态的值都没变时不重发这些属性（多数通知来自战斗数据）。</param>
    private void RefreshBackendState(bool onlyChanges = false)
    {
        var activity = backend.GetActivity();
        var binding = backend.GetGameBinding();
        var capture = backend.GetCaptureStatus();
        if (!onlyChanges || !Equals(activity, Activity) || !Equals(binding, Binding) || !Equals(capture, Capture))
            RaiseBackendState(activity, binding, capture);
        var release = AvailableUpdate(backend);
        if (release != UpdateRelease)
        {
            UpdateRelease = release;
            Raise(nameof(UpdateRelease));
            Raise(nameof(HasUpdate));
            Raise(nameof(UpdateLabel));
        }
        else if (!onlyChanges) Raise(nameof(UpdateLabel));
        GamePicker.OnBackendChanged();
    }

    private void RaiseBackendState(ActivitySnapshot activity, BindingView binding, CaptureStatusView capture)
    {
        Activity = activity;
        Binding = binding;
        Capture = capture;
        Raise(nameof(Activity));
        Raise(nameof(Binding));
        Raise(nameof(Capture));
        Raise(nameof(CaptureLine));
        Raise(nameof(CaptureIdentified));
        Raise(nameof(CaptureRunning));
        Raise(nameof(Place));
        Raise(nameof(CaptureTip));
        NpcapGuide.Observe(Capture);
        Raise(nameof(OwnerLine));
        Raise(nameof(ActivityOwnerBrush));
        Raise(nameof(Stages));
        Raise(nameof(ShowStages));
    }

    // 界面文字换语言后，由后端状态组成的文字和当前页面都重新取一次。
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var item in allBuiltIn.Concat(allExtensions).Concat(allPending).Concat(ShellEntries)) item.RefreshText();
        Raise(nameof(IsSimplified));
        Raise(nameof(IsTraditional));
        RefreshBackendState();
        // 换语言后所有页面按新语言重建：保留的页面一并释放，外框的骨架换成新语言的标题，当前页随后重新建立。
        ReleasePages();
        currentFrame?.Show();
        // 候选行的说明由界面语言拼成，打开时重新读取一次。
        if (GamePicker.IsOpen) _ = GamePicker.RefreshAsync();
    }

    // 换主题时页面不重建：令牌由动态资源跟随，这里只重取代码生成的颜色。
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Raise(nameof(ActivityOwnerBrush));
        Raise(nameof(ModuleHaloBrush));
        Raise(nameof(IsDarkTheme));
        Raise(nameof(IsLightTheme));
        Raise(nameof(BackdropScrim));
    }

    // 切到与当前实际主题相反的一个；原本跟随系统时，选择改为明确的浅色或深色。选择照常写入偏好文件。
    private static void ToggleTheme()
    {
        if (Application.Current is { } application)
            UiTheme.Apply(application, UiTheme.IsDark ? UiThemeChoice.Light : UiThemeChoice.Dark);
    }

    private static void UseLanguage(UiLanguage language)
    {
        if (UiText.Language != language && Application.Current is { } application) UiText.Apply(application, language);
    }

    private List<NavigationItem> ItemsFor(ModuleOrigin origin, bool pending) =>
        catalog.Modules.Where(m => m.Origin == origin && m.Pending == pending).Select(m => new NavigationItem(m, SelectCommand)).ToList();
}
