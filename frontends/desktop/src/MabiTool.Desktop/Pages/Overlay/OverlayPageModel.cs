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
        // 不透明度步进 0.1，限制在 0.2–1；游戏浮窗上元件的不透明度按钮改的是同一个值。
        OpacityCommand = new RelayCommand(step => Edit(placement => placement.Opacity = WidgetPlacement.StepOpacity(placement.Opacity, step is "+")));
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
        ResetPositionCommand = new RelayCommand(_ =>
        {
            if (Selected is { } placement) Store.ResetPosition(placement);
        });
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
    public ICommand OpacityCommand { get; }
    public ICommand SkinCommand { get; }
    public ICommand ShowOverlayCommand { get; }
    public ICommand HideOverlayCommand { get; }
    public ICommand ToggleAllLocksCommand { get; }
    public ICommand ToggleLockCommand { get; }
    public ICommand ToggleCaptureCommand { get; }
    public ICommand ToggleHideWithGameCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand MoveAllToPrimaryCommand { get; }
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
        }
    }

    /// <summary>各显示器的说明，例如“主显示器（1920×1080 · 100%）、左侧显示器（1920×1080 · 125%）”。</summary>
    public string MonitorsText => OverlayArea.Caption(screens);

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
    public string OpacityText => Selected is { } placement ? $"{placement.Opacity:P0}" : string.Empty;

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
    /// <summary>“游戏最小化时隐藏”开关；缺省关闭，浮窗始终显示。</summary>
    public bool HideWithGame => Store.HideWhenGameMinimized;

    public string StatusLine => IsEditing
        ? UiText.Pick("正在编辑：元件显示在各显示器上，拖动摆放（可拖到其他显示器），拖右下角改尺寸，对齐格线 8 px；完成后保存",
            "正在編輯：元件顯示在各顯示器上，拖動擺放（可拖到其他顯示器），拖右下角改尺寸，對齊格線 8 px；完成後保存")
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
