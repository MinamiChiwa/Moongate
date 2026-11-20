using System.Windows.Input;
using Avalonia.Media;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;
using MabiTool.Desktop.Overlay;

namespace MabiTool.Desktop.Pages.Overlay;

public sealed record LayoutTab(string Id, string Name, bool Selected);

/// <summary>
/// 元件库里的一项：来自哪个模块，是否已在当前版面上。<see cref="Notice"/> 是依赖的展示主题缺失时的标注
/// （台服暂不支持，或“暂不可用：原因码”），没有缺失时为空；带标注的元件仍可摆放。
/// </summary>
public sealed record LibraryItem(string WidgetId, string Title, string Group, IBrush Accent, bool Placed, string Notice = "")
{
    public bool HasNotice => Notice.Length > 0;
}

public sealed record SkinOption(string Id, string Label, bool Selected);

/// <summary>图标对照的一项：客户端属性图标的键与名称。</summary>
public sealed record IconLegendItem(string Icon, string Name);

/// <summary>显示器选项：标识（<see cref="ScreenInfo.Key"/>）、方位名与是否为当前所在的显示器；完整说明放在悬停提示里。</summary>
public sealed record MonitorOption(string Key, string Label, string Tip, bool Selected);

/// <summary>
/// 浮窗版面页的状态：版面列表、元件库、所选元件的设置，以及在屏幕上编辑浮窗的入口与显示器说明。
/// 所有改动直接写进 <see cref="OverlayLayoutStore"/>，各显示器的游戏浮窗随之更新；元件的位置与尺寸在实际屏幕上编辑。
/// </summary>
public sealed class OverlayPageModel : ObservableObject, IDisposable, IPageLifecycle
{
    private bool active = true;

    // 页面保留：版面与浮窗状态都在内存里，切走时只退订通知，回来时重读一次。
    public bool Cacheable => true;

    public void Deactivate()
    {
        if (!active) return;
        active = false;
        Store.Changed -= OnChanged;
        controller.Changed -= OnChanged;
        controller.Backend.Changed -= OnChanged;
    }

    public void Activate()
    {
        if (active) return;
        active = true;
        Store.Changed += OnChanged;
        controller.Changed += OnChanged;
        controller.Backend.Changed += OnChanged;
        OnChanged(this, EventArgs.Empty);
    }

    private readonly ModuleCatalog catalog;
    private readonly OverlayController controller;
    private string? selected;

    public OverlayPageModel(ModuleCatalog catalog, OverlayController controller)
    {
        this.catalog = catalog;
        this.controller = controller;
        SelectLayoutCommand = new RelayCommand(id => { if (id is string layout) Store.Select(layout); });
        AddLayoutCommand = new RelayCommand(_ =>
            Store.Add(UiText.Pick($"版面 {Store.Layouts.Count + 1}", $"版面 {Store.Layouts.Count + 1}")));
        RemoveLayoutCommand = new RelayCommand(_ => Store.Remove(Store.Active.Id));
        ToggleWidgetCommand = new RelayCommand(id =>
        {
            if (id is not string widget) return;
            if (Store.Active.Widgets.FirstOrDefault(placement => placement.WidgetId == widget) is { } placed)
            {
                // 在浮窗上关闭过的元件：点一次重新显示，不移出版面。
                if (placed.Closed) { Store.Reopen(placed); SelectedWidgetId = widget; }
                else if (SelectedWidgetId == widget) Store.Unplace(widget);
                else SelectedWidgetId = widget;
            }
            else
            {
                Store.Place(widget);
                SelectedWidgetId = widget;
            }
        });
        RemoveWidgetCommand = new RelayCommand(_ =>
        {
            if (SelectedWidgetId is not { } widget) return;
            Store.Unplace(widget);
            SelectedWidgetId = null;
        });
        // 与浮窗上锁左侧的“－／＋”同一规则：步进 0.1，限制在 0.5–2。
        ScaleCommand = new RelayCommand(step => Edit(placement => placement.Scale = WidgetPlacement.Step(placement.Scale, step is "+")));
        // 背景与内容不透明度各自步进 0.1（背景 0–1，内容 0.2–1）；游戏浮窗上元件的两个不透明度按钮改的是同一份值。
        BackgroundOpacityCommand = new RelayCommand(step => Edit(placement => placement.BackgroundOpacity =
            WidgetPlacement.StepOpacity(placement.BackgroundOpacity, step is "+", WidgetPlacement.MinimumBackgroundOpacity)));
        ContentOpacityCommand = new RelayCommand(step => Edit(placement => placement.ContentOpacity =
            WidgetPlacement.StepOpacity(placement.ContentOpacity, step is "+", WidgetPlacement.MinimumContentOpacity)));
        SkinCommand = new RelayCommand(skin => Edit(placement => placement.Skin = skin as string ?? "glass"));
        ShowOverlayCommand = new RelayCommand(_ => controller.Show());
        HideOverlayCommand = new RelayCommand(_ => controller.Hide());
        ToggleAllLocksCommand = new RelayCommand(_ => controller.ToggleAllLocks());
        ToggleLockCommand = new RelayCommand(_ =>
        {
            if (Selected is { } placement) Store.ToggleLock(placement);
        });
        ToggleCaptureCommand = new RelayCommand(_ =>
        {
            Store.HideFromCapture = !Store.HideFromCapture;
            Store.Commit();
        });
        ToggleHideWithGameCommand = new RelayCommand(_ =>
        {
            Store.HideWhenGameMinimized = !Store.HideWhenGameMinimized;
            Store.Commit();
        });
        EditCommand = new RelayCommand(_ =>
        {
            if (Store.IsEditing) controller.FinishEdit();
            else controller.Edit();
        });
        MoveAllToPrimaryCommand = new RelayCommand(_ => Store.MoveAllToPrimary());
        MoveToMonitorCommand = new RelayCommand(key => { if (key is string screen) Edit(placement => placement.Screen = screen); });
        MoveAllToMonitorCommand = new RelayCommand(key => { if (key is string screen) Store.MoveAllTo(screen); });
        ResetPositionCommand = new RelayCommand(_ =>
        {
            if (Selected is { } placement) Store.ResetPosition(placement);
        });
        HealthCommand = new RelayCommand(choice => Edit(placement => placement.HideMonsterHealth = choice is "hide"));
        OpenGameDataCommand = new RelayCommand(_ => IsGameDataOpen = true);
        CloseGameDataCommand = new RelayCommand(_ => IsGameDataOpen = false);
        Store.Changed += OnChanged;
        controller.Changed += OnChanged;
        controller.Backend.Changed += OnChanged;
    }

    private bool isGameDataOpen;

    /// <summary>“需要游戏数据的元件”二级层是否打开。</summary>
    public bool IsGameDataOpen
    {
        get => isGameDataOpen;
        set => Set(ref isGameDataOpen, value);
    }

    public ICommand OpenGameDataCommand { get; }
    public ICommand CloseGameDataCommand { get; }

    public OverlayLayoutStore Store => controller.Store;
    public ModuleCatalog Catalog => catalog;
    public ICommand SelectLayoutCommand { get; }
    public ICommand AddLayoutCommand { get; }
    public ICommand RemoveLayoutCommand { get; }
    public ICommand ToggleWidgetCommand { get; }
    public ICommand RemoveWidgetCommand { get; }
    public ICommand ScaleCommand { get; }
    public ICommand BackgroundOpacityCommand { get; }
    public ICommand ContentOpacityCommand { get; }
    public ICommand SkinCommand { get; }
    public ICommand ShowOverlayCommand { get; }
    public ICommand HideOverlayCommand { get; }
    public ICommand ToggleAllLocksCommand { get; }
    public ICommand ToggleLockCommand { get; }
    public ICommand ToggleCaptureCommand { get; }
    public ICommand ToggleHideWithGameCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand MoveAllToPrimaryCommand { get; }
    /// <summary>所选元件移到参数指定的显示器（比例位置不变，立即保存）；不需要拖动跨屏。</summary>
    public ICommand MoveToMonitorCommand { get; }
    /// <summary>当前版面的元件全部移到参数指定的显示器。</summary>
    public ICommand MoveAllToMonitorCommand { get; }
    public ICommand ResetPositionCommand { get; }

    private IReadOnlyList<ScreenInfo> screens = [];

    /// <summary>当前的显示器（由页面按窗口的显示器信息写入）；显示器说明与所选元件所在的显示器据此写出。</summary>
    public IReadOnlyList<ScreenInfo> Screens
    {
        get => screens;
        set
        {
            if (value.SequenceEqual(screens)) return;
            screens = value;
            Raise(nameof(MonitorsText));
            Raise(nameof(SelectedDetails));
            Raise(nameof(MonitorChoices));
            Raise(nameof(AllMonitorChoices));
            Raise(nameof(HasMonitorChoice));
        }
    }

    /// <summary>各显示器的说明，例如“主显示器（1920×1080 · 100%）、左侧显示器（1920×1080 · 125%）”。</summary>
    public string MonitorsText => OverlayArea.Caption(screens);

    public string MoveToLabel => UiText.Pick("移到显示器", "移到顯示器");
    public string MoveAllLabel => UiText.Pick("全部移到", "全部移到");

    /// <summary>连接了不止一块显示器：所选元件的“显示器”选择与“全部移到”才显示。</summary>
    public bool HasMonitorChoice => screens.Count(screen => screen.Bounds is { Width: > 0, Height: > 0 }) > 1;

    private IReadOnlyList<MonitorOption> Options(Func<string, bool> selected) => HasMonitorChoice
        ? OverlayArea.Resolve(screens)!.Screens.OrderBy(screen => screen.Bounds.X).ThenBy(screen => screen.Bounds.Y)
            .Select(screen => new MonitorOption(screen.Key, OverlayArea.Place(screen, screens), OverlayArea.Name(screen, screens), selected(screen.Key)))
            .ToList()
        : [];

    // 元件实际所在的显示器（记住的显示器不在时回到主显示器，同 OverlayArea.Locate）。
    private string? LocatedKey(WidgetPlacement placement) =>
        OverlayArea.Resolve(screens) is { } all ? OverlayArea.Locate(all.Monitors, placement.Screen)?.Key : null;

    /// <summary>所选元件可选的显示器（从左到右），当前所在的一块为选中项；只有一块显示器时为空。</summary>
    public IReadOnlyList<MonitorOption> MonitorChoices => Selected is { } placement && LocatedKey(placement) is var current
        ? Options(key => key == current) : [];

    /// <summary>“全部移到”的显示器选项；当前版面的元件都在某一块上时该项为选中项。</summary>
    public IReadOnlyList<MonitorOption> AllMonitorChoices => Options(key =>
        Store.Active.Widgets.Count > 0 && Store.Active.Widgets.All(widget => LocatedKey(widget) == key));

    /// <summary>版面处于在屏幕上编辑的状态。</summary>
    public bool IsEditing => Store.IsEditing;

    /// <summary>编辑入口的文字：未编辑时“编辑位置”，编辑中“完成编辑”。</summary>
    public string EditAction => IsEditing ? UiText.Pick("完成编辑", "完成編輯") : UiText.Pick("编辑位置", "編輯位置");

    public string? SelectedWidgetId
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            Raise(string.Empty);
        }
    }

    public IReadOnlyList<LayoutTab> Layouts =>
        Store.Layouts.Select(layout => new LayoutTab(layout.Id, layout.Name, layout == Store.Active)).ToList();

    public bool CanRemoveLayout => Store.Layouts.Count > 1;

    private IEnumerable<(WidgetDescriptor Widget, LibraryItem Item, bool NeedsGame)> AllWidgets => catalog.Modules.SelectMany(module => module.Widgets.Select(widget =>
        (widget, new LibraryItem(widget.Id, widget.Title.Current, module.Title.Current, module.AccentBrush,
            Store.Active.Widgets.Any(placement => placement.WidgetId == widget.Id),
            widget.PresentationTopic is { } topic ? controller.Backend.GetPresentationNotice(topic)?.Text ?? string.Empty : string.Empty), module.Pending || widget.PresentationTopic is not null)));

    /// <summary>主元件库：不依赖游戏内数据、所属模块已实现核心功能的元件。</summary>
    public IReadOnlyList<LibraryItem> Library => AllWidgets.Where(entry => !entry.NeedsGame)
        .Select(entry => entry.Item).ToList();

    /// <summary>二级层里的元件：依赖游戏内数据的元件和属于未实现模块的元件；主题缺失时带标注，仍可加入版面。</summary>
    public IReadOnlyList<LibraryItem> GameDataLibrary => AllWidgets.Where(entry => entry.NeedsGame)
        .Select(entry => entry.Item).ToList();

    private LibraryItem? Find(string? widgetId) => AllWidgets.Select(entry => entry.Item).FirstOrDefault(item => item.WidgetId == widgetId);

    private WidgetPlacement? Selected => Store.Active.Widgets.FirstOrDefault(placement => placement.WidgetId == selected);

    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;

    public string SelectedTitle => Find(selected)?.Title
        ?? UiText.Pick("尚未选取元件", "尚未選取元件");

    public IReadOnlyList<KeyValue> SelectedDetails
    {
        get
        {
            if (Selected is not { } placement) return [];
            var item = Find(selected);
            var details = new List<KeyValue>
            {
                new(UiText.Pick("位置", "位置"), UiText.Pick($"横向 {placement.X:P0}，纵向 {placement.Y:P0}", $"橫向 {placement.X:P0}，縱向 {placement.Y:P0}")),
                new(UiText.Pick("显示器", "顯示器"), OverlayArea.Where(screens, placement.Screen)),
                new(UiText.Pick("来自", "來自"), item?.Group ?? string.Empty)
            };
            if (SizeOf(placement) is { } size)
                details.Insert(1, new(UiText.Pick("尺寸", "尺寸"), $"{size.Width:0} × {size.Height:0}"));
            if (item is { HasNotice: true }) details.Add(new(UiText.Pick("状态", "狀態"), item.Notice));
            if (placement.Closed)
                details.Add(new(UiText.Pick("浮窗", "浮窗"), UiText.Pick("已在浮窗上关闭，点元件库里的它重新显示", "已在浮窗上關閉，點元件庫裡的它重新顯示")));
            return details;
        }
    }

    /// <summary>
    /// 所选元件里以客户端图标显示的标签（DPS、定时测量与覆盖率元件的暴击、强击、连击、追击）：图标与名称的对照。
    /// 浮窗上锁时鼠标穿透、看不到图标的悬浮提示，这里给出每个图标的含义。
    /// </summary>
    public IReadOnlyList<IconLegendItem> SelectedIcons => Selected is { WidgetId: "combat.dps" or "combat.measurement" or "combat.coverage" }
        ? new[] { Backend.ClientIconNames.Critical, Backend.ClientIconNames.PowerfulHit, Backend.ClientIconNames.RapidHit, Backend.ClientIconNames.DoubleHit }
            .Select(icon => new IconLegendItem(icon, Backend.ClientIconNames.Name(icon))).ToList()
        : [];

    public bool HasSelectedIcons => SelectedIcons.Count > 0;

    /// <summary>所选元件是 DPS 元件：详情里有“怪物血量”开关。</summary>
    public bool HasHealthSetting => Selected is { WidgetId: "combat.dps" };
    /// <summary>DPS 元件显示怪物血量区块（默认显示）。</summary>
    public bool HealthShown => Selected is { HideMonsterHealth: false };
    public bool HealthHidden => !HealthShown;
    public string HealthLabel => UiText.Pick("怪物血量", "怪物血量");
    public string HealthHint => UiText.Pick("在 DPS 数值下方显示当前攻击目标的名称、剩余血量与百分比；目标由后端按攻击情况选定",
        "在 DPS 數值下方顯示目前攻擊目標的名稱、剩餘血量與百分比；目標由後端按攻擊情況選定");
    public string ShowLabel => UiText.Pick("显示", "顯示");
    public string HideLabel => UiText.Pick("隐藏", "隱藏");
    /// <summary>参数 show 或 hide：DPS 元件的怪物血量区块显示或隐藏，立即保存。</summary>
    public ICommand HealthCommand { get; }

    // 元件实际使用的尺寸（缩放前）：保存的宽高或元件的默认尺寸，不小于最小尺寸。
    private Avalonia.Size? SizeOf(WidgetPlacement placement) =>
        catalog.Modules.SelectMany(module => module.Widgets).FirstOrDefault(widget => widget.Id == placement.WidgetId) is { } widget
            ? OverlayWidgetSizes.Resolve(placement, OverlayWidgetSizes.DefaultOf(widget), OverlayWidgetSizes.MinimumOf(widget))
            : null;

    /// <summary>所选元件在游戏浮窗上是否上锁。</summary>
    public bool SelectedLocked => Selected?.Locked ?? true;
    public string LockState => SelectedLocked ? UiText.Pick("已上锁", "已上鎖") : UiText.Pick("已解锁", "已解鎖");
    /// <summary>锁状态切换按钮的文字：已上锁时为“解锁”，已解锁时为“上锁”。</summary>
    public string LockAction => SelectedLocked ? UiText.Pick("解锁", "解鎖") : UiText.Pick("上锁", "上鎖");

    public string ScaleText => Selected is { } placement ? $"{placement.Scale:P0}" : string.Empty;
    public string BackgroundOpacityText => Selected is { } placement ? $"{placement.BackgroundOpacity:P0}" : string.Empty;
    public string ContentOpacityText => Selected is { } placement ? $"{placement.ContentOpacity:P0}" : string.Empty;

    public IReadOnlyList<SkinOption> Skins
    {
        get
        {
            var current = Selected?.Skin ?? "glass";
            return
            [
                new("glass", UiText.Pick("玻璃", "玻璃"), current == "glass"),
                new("solid", UiText.Pick("纯色", "純色"), current == "solid"),
                new("image", UiText.Pick("自定图像", "自訂圖像"), current == "image")
            ];
        }
    }

    public bool UsesImage => Selected?.Skin == "image";

    public string ImagePath
    {
        get => Selected?.Image ?? string.Empty;
        set => Edit(placement => placement.Image = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>浮窗是否正在显示，取自浮窗的实际状态（包括被快捷键切换后），显示开关的选中项随它变化。</summary>
    public bool OverlayShown => controller.IsShown;
    public bool OverlayHidden => !controller.IsShown;

    /// <summary>当前版面的元件都已上锁；“全部上锁”开关的选中状态。</summary>
    public bool AllLocked => Store.AllLocked;
    public bool HideFromCapture => Store.HideFromCapture;

    /// <summary>“录屏时隐藏”需要 Windows 10 2004 或更新；更早的系统上停用并在提示里写明原因。</summary>
    public bool CaptureSupported => OverlayWindow.CaptureExclusionSupported;

    public string? CaptureHint => CaptureSupported ? null
        : UiText.Pick("需要 Windows 10 2004 或更新，当前系统不能让浮窗不进入录屏", "需要 Windows 10 2004 或更新，目前系統不能讓浮窗不進入錄影");
    /// <summary>“游戏最小化时隐藏”开关；缺省关闭，浮窗始终显示。</summary>
    public bool HideWithGame => Store.HideWhenGameMinimized;

    public string StatusLine => IsEditing
        ? UiText.Pick("正在编辑：元件显示在各显示器上，拖动摆放，按元件上的箭头或下方“移到显示器”换屏，拖右下角改尺寸，对齐格线 8 px；完成后保存",
            "正在編輯：元件顯示在各顯示器上，拖動擺放，按元件上的箭頭或下方「移到顯示器」換屏，拖右下角改尺寸，對齊格線 8 px；完成後保存")
        : UiText.Pick("按“编辑位置”后元件直接显示在屏幕上，可以拖到任意显示器；关闭或看不见的元件也会显示出来",
            "按「編輯位置」後元件直接顯示在螢幕上，可以拖到任意顯示器；關閉或看不見的元件也會顯示出來");

    public void Dispose()
    {
        active = false;
        Store.Changed -= OnChanged;
        controller.Changed -= OnChanged;
        controller.Backend.Changed -= OnChanged;
    }

    private void Edit(Action<WidgetPlacement> change)
    {
        if (Selected is not { } placement) return;
        change(placement);
        Store.Commit();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (selected is not null && Selected is null) selected = null;
        Raise(string.Empty);
    }
}
