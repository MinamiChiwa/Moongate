using System.Windows.Input;
using Avalonia.Media;
using MabiTool.Desktop.Controls;
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
/// <summary>
/// 元件库里的一块：标识、名称、所属模块、色点、是否启用（已放进版面且没有在浮窗上关闭，右侧开关的状态）、
/// 是否为当前选中的元件（描边，与启用分开表示）与展示主题缺失的标注。
/// </summary>
public sealed record LibraryItem(string WidgetId, string Title, string Group, IBrush Accent, bool Enabled, bool Selected, string Notice = "")
{
    public bool HasNotice => Notice.Length > 0;
    /// <summary>开关的无障碍名称与悬停提示。</summary>
    public string ToggleName => UiText.Pick($"{Title}：在浮窗上显示", $"{Title}：在浮窗上顯示");
    public string ToggleTip => Enabled ? UiText.Pick("关闭后从浮窗隐藏，保留位置与设置", "關閉後從浮窗隱藏，保留位置與設定")
        : UiText.Pick("打开后显示在浮窗上", "打開後顯示在浮窗上");
}

public sealed record SkinOption(string Id, string Label, bool Selected);

/// <summary>收藏浮窗卡片里的一个收藏：名称、极性图标与是否勾选了“自身”“目标”。</summary>
public sealed record FavoriteChoice(string Id, string Name, Pages.Combat.EffectPolarity Polarity, bool Self, bool Target)
{
    private readonly Pages.Combat.EffectRow icon = new(string.Empty, Name, string.Empty, string.Empty, string.Empty, Id, string.Empty, Polarity);
    public IBrush Fill => icon.Fill;
    public double Angle => icon.Angle;
    public string SelfKey => FavoriteBoard.SelfPrefix + Id;
    public string TargetKey => FavoriteBoard.TargetPrefix + Id;
    public string Tip => $"{Name}（{Id}）";
}

/// <summary>
/// “徽章觉醒”的提示设置：标题、闪烁开关与颜色、闪烁时长、音效开关、声音与音量。
/// 选中觉醒监控浮窗或 DPS 元件时卡片里显示的是同一份（<see cref="OverlayLayoutStore.Awakening"/>）。
/// </summary>
public sealed record AlertView(string Title, bool Flash, ColorEntry Color, string Seconds, bool Sound, string SoundText, bool HasFile, string Volume)
{
    public string FlashState => Flash ? Localization.UiText.Pick("已打开", "已打開") : Localization.UiText.Pick("已关闭", "已關閉");
    public string SoundState => Sound ? Localization.UiText.Pick("已打开", "已打開") : Localization.UiText.Pick("已关闭", "已關閉");
}

/// <summary>图标对照的一项：客户端属性图标的键与名称。</summary>
public sealed record IconLegendItem(string Icon, string Name);

/// <summary>
/// 配色卡片里的一项：范围（global 为整体配色，widget 为所选元件的覆盖）、键、名称、实际使用的颜色与是否可调；
/// <see cref="Note"/> 是停用原因或“跟随整体”等说明。
/// </summary>
public sealed record ColorEntry(string Scope, string Key, string Label, string Hex, bool Enabled = true, string Note = "")
{
    public IBrush Brush => new SolidColorBrush(Color.Parse(Hex));
    public bool HasNote => Note.Length > 0;
    public string Tip => Enabled ? UiText.Pick($"{Label} {Hex}，点按选色", $"{Label} {Hex}，點按選色") : Note;
}

/// <summary>配色卡片里的一组：标题、各项、复位按钮的文字与是否有可复位的改动（<see cref="Id"/> 是复位命令的参数“范围:组”）。</summary>
public sealed record ColorGroupView(string Id, string Title, IReadOnlyList<ColorEntry> Items, string ResetLabel, bool Changed, string Hint = "")
{
    public bool HasHint => Hint.Length > 0;
}

/// <summary>配色卡片的示意元件用到的画刷（按当前整体配色）与几处示意文字（强调、正面、警示与四种效果条目的名称）。</summary>
public sealed record OverlayColorPreview(IBrush Plate, IBrush Text, IBrush Muted, IBrush Accent, IBrush Positive, IBrush Warning,
    IBrush BarFill, IBrush BarTrack, IBrush EffectPositive, IBrush EffectNegative, IBrush EffectOther, IBrush EffectWarning,
    IBrush EffectText, IBrush EffectMuted, IReadOnlyList<IBrush> Icons, IReadOnlyList<string> Labels);

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
        // 收藏增减时收藏浮窗的勾选列表随之更新。
        Pages.Combat.CombatEffectFavorites.Changed += OnChanged;
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
        // 点元件块只选中，在右侧卡片里编辑；不放置、不移除。
        SelectWidgetCommand = new RelayCommand(id => { if (id is string widget) SelectedWidgetId = widget; });
        // 块右侧的开关只管是否显示在浮窗上，不改变选中：开 → 关等同浮窗上的关闭按钮（保留位置、尺寸与设置），
        // 关 → 开时没放进版面的放进去、在浮窗上关闭过的重新显示。
        EnableWidgetCommand = new RelayCommand(id =>
        {
            if (id is not string widget) return;
            if (Store.Active.Widgets.FirstOrDefault(placement => placement.WidgetId == widget) is not { } placed) Store.Place(widget);
            else if (placed.Closed) Store.Reopen(placed);
            else Store.Close(placed);
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
        DpsAwakeningCommand = new RelayCommand(choice => Edit(placement => placement.HideAwakening = choice is "hide"));
        FavoriteItemCommand = new RelayCommand(value =>
        {
            if (Selected is { } placement && value is string key) Store.SetFavoriteItem(placement, key, !FavoriteBoard.Effective(placement).Contains(key));
        });
        FavoriteLayoutCommand = new RelayCommand(choice => Edit(placement => placement.Horizontal = choice is "horizontal"));
        AlertFlashCommand = new RelayCommand(_ => EditAlert(alert => alert.Flash = !alert.Flash));
        AlertSoundCommand = new RelayCommand(_ => EditAlert(alert => alert.Sound = !alert.Sound));
        AlertBuiltinCommand = new RelayCommand(_ => EditAlert(alert => alert.SoundFile = null));
        AlertSecondsCommand = new RelayCommand(step =>
        {
            if (step is "+" or "-") EditAlert(alert => alert.FlashSeconds += step is "+" ? AwakeningAlert.SecondsStep : -AwakeningAlert.SecondsStep);
        });
        AlertVolumeCommand = new RelayCommand(step =>
        {
            if (step is "+" or "-") EditAlert(alert => alert.Volume += step is "+" ? AwakeningAlert.VolumeStep : -AwakeningAlert.VolumeStep);
        });
        ResetColorsCommand = new RelayCommand(value =>
        {
            if (value is not string id || id.Split(':') is not [var scope, var name] || !Enum.TryParse<OverlayColorGroup>(name, out var group)) return;
            if (scope == "widget") { if (Selected is { } placement) Store.ResetWidgetColors(placement, group); }
            else Store.ResetColors(group);
        });
        ApplyColorsToAllCommand = new RelayCommand(_ =>
        {
            if (Selected is { } placement) Store.ApplyWidgetColorsToAll(placement);
        });
        ClosePickerCommand = new RelayCommand(_ => ClosePicker());
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

    // ---- 配色 ----

    /// <summary>浮窗配色的最近颜色：本次运行期间保留，与染色页分开。</summary>
    public static RecentColors RecentColors { get; } = new();

    /// <summary>参数“global:组”或“widget:组”：整体配色的一组恢复原配色，或所选元件的一组覆盖去掉（跟随整体）。</summary>
    public ICommand ResetColorsCommand { get; }

    /// <summary>所选元件的底板与文字配色应用到当前版面的全部元件。</summary>
    public ICommand ApplyColorsToAllCommand { get; }

    /// <summary>关闭选色浮层：颜色改过时记进最近颜色。</summary>
    public ICommand ClosePickerCommand { get; }

    private ColorEntry? pickerEntry;
    private ColorPickerModel? picker;

    /// <summary>选色浮层是否打开。</summary>
    public bool IsPickerOpen => picker is not null;

    /// <summary>选色浮层的标题：所选项的名称，元件的覆盖前面加元件名。</summary>
    public string PickerTitle => pickerEntry is not { } entry ? string.Empty
        : entry.Scope == "widget" ? $"{SelectedTitle} · {entry.Label}" : entry.Label;

    /// <summary>
    /// 为 <paramref name="entry"/> 打开选色浮层，返回浮层的模型；停用的项或元件已不在版面上时为空。
    /// 选色时每次改动立即写进版面并保存，游戏浮窗随之更新。
    /// </summary>
    public ColorPickerModel? OpenColorPicker(ColorEntry entry)
    {
        ClosePicker();
        if (!entry.Enabled) return null;
        WidgetPlacement? target = null;
        if (entry.Scope == "widget" && (target = Selected) is null) return null;
        var key = entry.Key;
        var alert = entry.Scope == "alert" ? Store.Awakening.Badge : null;
        var model = new ColorPickerModel(
            () => alert?.Color ?? OverlayColors.Resolve(key, Store.Colors, target?.Colors),
            hex =>
            {
                if (alert is not null) { alert.Color = hex; Store.Commit(); }
                else if (target is not null) Store.SetWidgetColor(target, key, hex);
                else Store.SetColor(key, hex);
            },
            RecentColors);
        model.Open();
        (pickerEntry, picker) = (entry, model);
        Raise(nameof(IsPickerOpen));
        Raise(nameof(PickerTitle));
        return model;
    }

    private void ClosePicker()
    {
        if (picker is null) return;
        picker.Close();
        (pickerEntry, picker) = (null, null);
        Raise(nameof(IsPickerOpen));
    }

    private ColorEntry Entry(string scope, OverlayColorItem item, IReadOnlyDictionary<string, string>? widget, bool enabled, string note) =>
        new(scope, item.Key, item.Name, OverlayColors.Resolve(item.Key, Store.Colors, widget), enabled, note);

    /// <summary>“配色”卡片的六组：整体配色，每组可以恢复原配色。当前版面的元件都用自定图像时背景色停用。</summary>
    public IReadOnlyList<ColorGroupView> ColorGroups
    {
        get
        {
            var allImages = Store.Active.Widgets.Count > 0 && Store.Active.Widgets.All(widget => widget.Skin == "image");
            return Enum.GetValues<OverlayColorGroup>().Select(group =>
            {
                var items = OverlayColors.Items.Where(item => item.Group == group)
                    .Select(item => item.Key == OverlayColors.Plate && allImages
                        ? Entry("global", item, null, false, UiText.Pick("当前版面的元件都用自定图像，底板已是图片", "目前版面的元件都用自訂圖像，底板已是圖片"))
                        : Entry("global", item, null, true, string.Empty))
                    .ToList();
                return new ColorGroupView("global:" + group, OverlayColors.GroupName(group), items, UiText.Pick("恢复原配色", "恢復原配色"),
                    items.Any(entry => Store.Colors.ContainsKey(entry.Key)), Hint(group));
            }).ToList();
        }
    }

    private static string Hint(OverlayColorGroup group) => group switch
    {
        OverlayColorGroup.Plate => UiText.Pick("玻璃与纯色外观的底色；透明度由背景不透明度调整", "玻璃與純色外觀的底色；透明度由背景不透明度調整"),
        OverlayColorGroup.Bars => UiText.Pick("乐谱进度与定时测量的进度条；轨道按 25% 不透明画在底板上", "樂譜進度與定時測量的進度條；軌道按 25% 不透明畫在底板上"),
        OverlayColorGroup.Effects => UiText.Pick("只改浮窗上的效果条目，DPS检测页不变", "只改浮窗上的效果條目，DPS檢測頁不變"),
        OverlayColorGroup.Icons => UiText.Pick("只改浮窗上的图标；没改过的沿用客户端给出的颜色", "只改浮窗上的圖示；沒改過的沿用用戶端給出的顏色"),
        _ => string.Empty
    };

    /// <summary>所选元件的底板与文字两组：没有覆盖的项显示整体配色并注明“跟随整体”；外观为自定图像时背景色停用。</summary>
    public IReadOnlyList<ColorGroupView> WidgetColorGroups
    {
        get
        {
            if (Selected is not { } placement) return [];
            var own = placement.Colors;
            return new[] { OverlayColorGroup.Plate, OverlayColorGroup.Text }.Select(group =>
            {
                var items = OverlayColors.Items.Where(item => item.Group == group).Select(item =>
                {
                    if (item.Key == OverlayColors.Plate && placement.Skin == "image")
                        return Entry("widget", item, own, false, UiText.Pick("外观为自定图像，底板已是图片", "外觀為自訂圖像，底板已是圖片"));
                    var follows = own is null || !own.ContainsKey(item.Key);
                    return Entry("widget", item, own, true, follows ? UiText.Pick("跟随整体", "跟隨整體") : string.Empty);
                }).ToList();
                return new ColorGroupView("widget:" + group, OverlayColors.GroupName(group), items, UiText.Pick("跟随整体", "跟隨整體"),
                    own is not null && items.Any(entry => own.ContainsKey(entry.Key)));
            }).ToList();
        }
    }

    public string ApplyColorsLabel => UiText.Pick("应用到全部元件", "套用到全部元件");

    /// <summary>示意元件的画刷：按当前整体配色。</summary>
    public OverlayColorPreview Preview
    {
        get
        {
            IBrush Of(string key, byte alpha = 0xFF)
            {
                var color = Color.Parse(OverlayColors.Resolve(key, Store.Colors));
                return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            }
            return new(Of(OverlayColors.Plate, OverlayColors.SolidAlpha), Of(OverlayColors.Text), Of(OverlayColors.Muted), Of(OverlayColors.Accent),
                Of(OverlayColors.Positive), Of(OverlayColors.Warning), Of(OverlayColors.BarFill), Of(OverlayColors.BarTrack, OverlayColors.TrackAlpha),
                Of(OverlayColors.EffectPositive), Of(OverlayColors.EffectNegative), Of(OverlayColors.EffectOther), Of(OverlayColors.EffectWarning),
                Of(OverlayColors.EffectText), Of(OverlayColors.EffectMuted),
                OverlayColors.Items.Where(item => item.Group == OverlayColorGroup.Icons).Select(item => Of(item.Key)).ToList(),
                new[] { OverlayColors.Accent, OverlayColors.Positive, OverlayColors.Warning, OverlayColors.EffectPositive, OverlayColors.EffectNegative,
                    OverlayColors.EffectOther, OverlayColors.EffectWarning }.Select(key => OverlayColors.Find(key)!.Name).ToList());
        }
    }

    public OverlayLayoutStore Store => controller.Store;
    public ModuleCatalog Catalog => catalog;
    public ICommand SelectLayoutCommand { get; }
    public ICommand AddLayoutCommand { get; }
    public ICommand RemoveLayoutCommand { get; }
    public ICommand SelectWidgetCommand { get; }
    public ICommand EnableWidgetCommand { get; }
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
            Store.Active.Widgets.Any(placement => placement.WidgetId == widget.Id && !placement.Closed), widget.Id == selected,
            widget.PresentationTopic is { } topic ? controller.Backend.GetPresentationNotice(topic)?.Text ?? string.Empty : string.Empty), module.Pending || widget.PresentationTopic is not null)));

    /// <summary>主元件库：不依赖游戏内数据、所属模块已实现核心功能的元件。</summary>
    public IReadOnlyList<LibraryItem> Library => AllWidgets.Where(entry => !entry.NeedsGame)
        .Select(entry => entry.Item).ToList();

    /// <summary>二级层里的元件：依赖游戏内数据的元件和属于未实现模块的元件；主题缺失时带标注，仍可加入版面。</summary>
    public IReadOnlyList<LibraryItem> GameDataLibrary => AllWidgets.Where(entry => entry.NeedsGame)
        .Select(entry => entry.Item).ToList();

    private LibraryItem? Find(string? widgetId) => AllWidgets.Select(entry => entry.Item).FirstOrDefault(item => item.WidgetId == widgetId);

    private WidgetPlacement? Selected => Store.Active.Widgets.FirstOrDefault(placement => placement.WidgetId == selected);

    /// <summary>选中了元件库里的一个元件（不论是否启用）。</summary>
    public bool HasSelection => selected is not null && Find(selected) is not null;
    public bool NoSelection => !HasSelection;
    /// <summary>选中的元件有摆放数据（放进过版面，包括在浮窗上关闭的）：设置可调；从未放进版面时设置停用。</summary>
    public bool HasPlacement => Selected is not null;
    /// <summary>选中的元件未启用（没有放进版面或在浮窗上关闭）：卡片写明打开开关后显示在浮窗上。</summary>
    public bool SelectedInactive => HasSelection && Selected is not { Closed: false };
    public string InactiveText => UiText.Pick("未启用，打开开关后显示在浮窗上", "未啟用，打開開關後顯示在浮窗上");
    public bool SelectedUnplaced => HasSelection && Selected is null;
    public string UnplacedText => UiText.Pick("还没放进版面，打开开关后可调整设置", "還沒放進版面，打開開關後可調整設定");

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
                details.Add(new(UiText.Pick("浮窗", "浮窗"), UiText.Pick("已在浮窗上关闭，打开元件库里的开关重新显示", "已在浮窗上關閉，打開元件庫裡的開關重新顯示")));
            return details;
        }
    }

    /// <summary>
    /// 所选元件里以客户端图标显示的标签（DPS 与定时测量元件的暴击、强击、连击、追击）：图标与名称的对照。
    /// 浮窗上锁时鼠标穿透、看不到图标的悬浮提示，这里给出每个图标的含义。
    /// </summary>
    public IReadOnlyList<IconLegendItem> SelectedIcons => Selected is { WidgetId: "combat.dps" or "combat.measurement" }
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

    /// <summary>所选元件是 DPS 元件：详情里有“觉醒监控”开关，打开时下方显示与觉醒监控浮窗同一份的觉醒提示设置。</summary>
    public bool HasDpsAwakeningSetting => Selected is { WidgetId: AwakeningBoard.DpsId };
    /// <summary>DPS 元件内显示徽章觉醒（默认显示）。</summary>
    public bool DpsAwakeningShown => Selected is { HideAwakening: false };
    public bool DpsAwakeningHidden => !DpsAwakeningShown;
    public string DpsAwakeningLabel => UiText.Pick("觉醒监控", "覺醒監控");
    public string DpsAwakeningHint => UiText.Pick("在 DPS 数值下方显示徽章觉醒：觉醒中亮起并按设定颜色闪烁，冷却中显示冷却剩余；提示设置与觉醒监控浮窗共用",
        "在 DPS 數值下方顯示徽章覺醒：覺醒中亮起並按設定顏色閃爍，冷卻中顯示冷卻剩餘；提示設定與覺醒監控浮窗共用");
    /// <summary>参数 show 或 hide：DPS 元件内的徽章觉醒显示或隐藏，立即保存。</summary>
    public ICommand DpsAwakeningCommand { get; }

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
        ClosePicker();
        active = false;
        Store.Changed -= OnChanged;
        controller.Changed -= OnChanged;
        controller.Backend.Changed -= OnChanged;
    }

    // 觉醒监控浮窗与 DPS 元件的卡片都改版面顶层的同一份设置，保存后两处元件与两张卡片随 Changed 一起更新。
    private void EditAlert(Action<AwakeningAlert> change)
    {
        change(Store.Awakening.Badge);
        Store.Commit();
    }

    /// <summary>觉醒提示改用本机声音文件（wav 或 mp3）并打开音效。</summary>
    public void SetAlertFile(string path) => EditAlert(alert => { alert.SoundFile = path; alert.Sound = true; });

    // ---- 收藏浮窗 ----

    /// <summary>所选元件是收藏浮窗：卡片里有收藏勾选与横竖排。</summary>
    public bool HasFavoritesSetting => Selected is { } placement && FavoriteBoard.IsBoard(placement.WidgetId);

    /// <summary>
    /// 全部收藏与所选收藏浮窗的勾选（只勾选，不排序；顺序在游戏浮窗上拖动调整）。按极性（增益、减益、未分类）再按名称排列。
    /// </summary>
    public IReadOnlyList<FavoriteChoice> FavoriteChoices
    {
        get
        {
            if (!HasFavoritesSetting || Selected is not { } placement) return [];
            var chosen = FavoriteBoard.Effective(placement).ToHashSet(StringComparer.Ordinal);
            return Pages.Combat.CombatEffectFavorites.Favorites
                .Select(id => new FavoriteChoice(id, FavoriteBoard.NameOf(id), Pages.Combat.CombatEffectFavorites.PolarityOf(id),
                    chosen.Contains(FavoriteBoard.SelfPrefix + id), chosen.Contains(FavoriteBoard.TargetPrefix + id)))
                .OrderBy(choice => choice.Polarity switch { Pages.Combat.EffectPolarity.Positive => 0, Pages.Combat.EffectPolarity.Negative => 1, _ => 2 })
                .ThenBy(choice => choice.Name, StringComparer.Ordinal).ThenBy(choice => choice.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    public bool NoFavorites => HasFavoritesSetting && Pages.Combat.CombatEffectFavorites.Favorites.Count == 0;
    public string FavoritesLabel => UiText.Pick("显示的收藏", "顯示的收藏");
    public string FavoritesHint => UiText.Pick("每个收藏可勾自身与目标；目标是 DPS 浮窗血量条的当前目标。顺序在浮窗解锁后按住一项拖动",
        "每個收藏可勾自身與目標；目標是 DPS 浮窗血量條的目前目標。順序在浮窗解鎖後按住一項拖動");
    public string NoFavoritesText => UiText.Pick("还没有收藏，先在 DPS 页收藏效果", "還沒有收藏，先在 DPS 頁收藏效果");
    public string SelfLabel => UiText.Pick("自身", "自身");
    public string TargetLabel => UiText.Pick("目标", "目標");
    public string LayoutLabel => UiText.Pick("排列", "排列");
    public string VerticalLabel => UiText.Pick("竖排", "豎排");
    public string HorizontalLabel => UiText.Pick("横排", "橫排");
    public bool FavoritesHorizontal => Selected?.Horizontal == true;
    public bool FavoritesVertical => !FavoritesHorizontal;
    /// <summary>所选元件是觉醒监控浮窗：卡片里有横竖排与“徽章觉醒”的提示设置。</summary>
    public bool HasAwakeningSetting => Selected is { WidgetId: AwakeningBoard.Id };
    /// <summary>收藏浮窗与觉醒监控浮窗都可切换竖排与横排。</summary>
    public bool HasLayoutSetting => HasFavoritesSetting || HasAwakeningSetting;
    /// <summary>觉醒提示设置：选中觉醒监控浮窗，或选中打开了觉醒显示的 DPS 元件时显示。</summary>
    public bool ShowAlerts => HasAwakeningSetting || HasDpsAwakeningSetting && DpsAwakeningShown;
    public string AlertsLabel => UiText.Pick("觉醒提示", "覺醒提示");
    public string AlertsHint => UiText.Pick("徽章觉醒按效果编号识别；触发时按这里的设置闪烁，打开音效时播放提示音。觉醒监控浮窗与 DPS 元件的觉醒显示共用这组设置",
        "徽章覺醒按效果編號識別；觸發時按這裡的設定閃爍，打開音效時播放提示音。覺醒監控浮窗與 DPS 元件的覺醒顯示共用這組設定");
    public string FlashLabel => UiText.Pick("闪烁", "閃爍");
    public string FlashSecondsLabel => UiText.Pick("闪烁时长", "閃爍時長");
    public string SoundLabel => UiText.Pick("音效", "音效");
    public string VolumeLabel => UiText.Pick("音量", "音量");
    public string PickSoundLabel => UiText.Pick("选择声音文件", "選擇聲音檔案");
    public string BuiltinSoundLabel => UiText.Pick("改用内置提示音", "改用內建提示音");

    /// <summary>“徽章觉醒”的提示设置；不显示提示设置时为空。</summary>
    public AlertView? Alert
    {
        get
        {
            if (!ShowAlerts) return null;
            var alert = Store.Awakening.Badge;
            var title = AwakeningBoard.BadgeTitle;
            var color = new ColorEntry("alert", FavoriteEffectTracker.Badge, UiText.Pick($"{title}闪烁色", $"{title}閃爍色"), alert.Color);
            var sound = alert.SoundFile is { } file
                ? File.Exists(file) ? Path.GetFileName(file) : UiText.Pick("文件不存在，改用内置提示音", "檔案不存在，改用內建提示音")
                : UiText.Pick("内置提示音", "內建提示音");
            return new AlertView(title, alert.Flash, color, $"{alert.FlashSeconds:0} 秒", alert.Sound, sound, alert.SoundFile is not null,
                $"{alert.Volume:P0}");
        }
    }

    public ICommand FavoriteItemCommand { get; }
    public ICommand FavoriteLayoutCommand { get; }
    public ICommand AlertFlashCommand { get; }
    public ICommand AlertSoundCommand { get; }
    public ICommand AlertBuiltinCommand { get; }
    public ICommand AlertSecondsCommand { get; }
    public ICommand AlertVolumeCommand { get; }

    private void Edit(Action<WidgetPlacement> change)
    {
        if (Selected is not { } placement) return;
        change(placement);
        Store.Commit();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        // 选中可以是未启用的元件；只在元件库里已没有它（提供它的模块被卸载）时取消选中。
        if (selected is not null && Find(selected) is null) selected = null;
        Raise(string.Empty);
    }
}
