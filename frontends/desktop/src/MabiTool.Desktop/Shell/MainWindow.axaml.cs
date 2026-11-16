using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Shell;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UiMotion.Attach(this);
        void ApplyTransition() => PageHost.PageTransition = UiMotion.PageTransition;
        ApplyTransition();
        EventHandler motionChanged = (_, _) => ApplyTransition();
        UiMotion.Changed += motionChanged;
        Closed += (_, _) => UiMotion.Changed -= motionChanged;
        Closed += (_, _) => (DataContext as ShellViewModel)?.Dispose();
        // 合奏、DPS 检测页的“前往设置”：打开设置页并滚动到抓包区。
        Action openCapture = OpenCaptureSettings;
        Infrastructure.ShellRequests.CaptureSettingsRequested += openCapture;
        Closed += (_, _) => Infrastructure.ShellRequests.CaptureSettingsRequested -= openCapture;
        // 顶部带内的空白处拖动窗口；按钮等控件先处理按下事件，冒泡到窗口时已标记为已处理。
        AddHandler(PointerPressedEvent, OnCaptionPressed, RoutingStrategies.Bubble);
        // 点空白处时输入框失去焦点并提交。
        FocusRelease.Attach(this);
        // 侧栏模块入口在各自分组内拖动排序。
        foreach (var list in new[] { BuiltInList, ExtensionList, PendingList })
            SidebarReorder.Attach(list, () => DataContext as ShellViewModel);
        SnapFrame.Attach(this, MaximizeButton);
        UpdateMaximizeButton();
        // 迷你窗的“主窗口”按钮回到这个窗口。
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel { Catalog.Mini: { } mini }) mini.Main = this;
            if (DataContext is ShellViewModel shell) { WatchPicker(shell.GamePicker); WatchNpcapGuide(shell.NpcapGuide); }
        };
        AddHandler(KeyDownEvent, OnPickerKeyDown, RoutingStrategies.Tunnel);
        PropertyChanged += (_, e) =>
        {
            // 最大化时占满工作区，不保留圆角。
            if (e.Property != WindowStateProperty) return;
            Frame.CornerRadius = new Avalonia.CornerRadius(WindowState == WindowState.Maximized ? 0 : 30);
            UpdateMaximizeButton();
        };
    }

    /// <summary>在顶部空白处按下、即将开始拖动窗口时触发；无界面测试用它确认按下位置是否算作拖动区。</summary>
    public event EventHandler? CaptionDragStarting;

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // 窗口状态无论由按钮、双击、快捷键还是系统贴靠改变，按钮都显示下一步会做的事。
    private void UpdateMaximizeButton()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        var key = maximized ? "S.Restore" : "S.Maximize";
        maximizeTip?.Dispose();
        maximizeName?.Dispose();
        maximizeTip = MaximizeButton.Bind(ToolTip.TipProperty, MaximizeButton.GetResourceObservable(key));
        maximizeName = MaximizeButton.Bind((AvaloniaProperty)AutomationProperties.NameProperty, MaximizeButton.GetResourceObservable(key));
    }

    private IDisposable? maximizeTip;
    private IDisposable? maximizeName;

    private void OnCaptionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || !WindowCaption.IsDragSurface(this, point.Position, WindowCaption.MainBandHeight)) return;
        e.Handled = true;
        // 连续双击时点击次数会累加到 4、6……，偶数次都按双击处理。
        if (e.ClickCount >= 2 && e.ClickCount % 2 == 0)
        {
            ToggleMaximize();
            return;
        }
        CaptionDragStarting?.Invoke(this, EventArgs.Empty);
        BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // 页面标题右侧的空位跟随右上角按钮组的实际宽度（出现更新提示时变宽），标题不与按钮重叠。
    // 按钮组右侧离栏边 2，页面标题右侧离栏边 8，两者之间再留 12。
    private void OnWindowButtonsSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var reserve = Math.Max(200, e.NewSize.Width + 2 - 8 + 12);
        Resources["TitleReserve"] = new Thickness(0, 0, reserve, 0);
    }

    private void OpenCaptureSettings()
    {
        if (DataContext is not ShellViewModel shell) return;
        shell.OpenSettings();
        // 要马上滚到页面里的某一区，设置页此时立即建立（不等调度帧）。
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            (shell.CurrentFrame?.EnsureBuilt() as Pages.Settings.SettingsPage)?.Reveal("CapturePanel"), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // 更新提示：打开设置页并滚动到更新区。
    private void OnUpdateBadgeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        shell.OpenSettings();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            (shell.CurrentFrame?.EnsureBuilt() as Pages.Settings.SettingsPage)?.RevealUpdates(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // 当前选中的模块在分组内时分组保持展开：开关被按成“收起”后，在绑定写入结束后让它回到实际状态。
    private void OnPendingToggleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => PendingToggle.SetCurrentValue(
            Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, shell.IsPendingOpen));
    }

    private GamePickerModel? picker;

    // 弹出层从绑定卡正下方展开；读取完成后焦点落在第一行（没有候选时落在刷新），关闭后回到绑定卡。
    private void WatchPicker(GamePickerModel model)
    {
        if (picker is not null) picker.PropertyChanged -= OnPickerChanged;
        picker = model;
        picker.PropertyChanged += OnPickerChanged;
    }

    private void OnPickerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (picker is null) return;
        if (e.PropertyName == nameof(GamePickerModel.IsOpen))
        {
            if (picker.IsOpen)
            {
                if (BindingCard.TranslatePoint(new Point(0, BindingCard.Bounds.Height), Frame) is { } anchor)
                    GamePickerPanel.Margin = new Thickness(anchor.X, anchor.Y + 6, 14, 14);
                GamePickerClose.Focus(NavigationMethod.Tab);
            }
            else BindingCard.Focus(NavigationMethod.Tab);
        }
        else if (e.PropertyName == nameof(GamePickerModel.CanAct) && picker is { IsOpen: true, CanAct: true })
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var first = GameWindowList.GetVisualDescendants().OfType<Button>().FirstOrDefault();
                ((Control?)first ?? GamePickerRefresh).Focus(NavigationMethod.Tab);
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnPickerOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        picker?.Close();
        e.Handled = true;
    }

    private void OnPickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (npcapGuide is { IsOpen: true }) { npcapGuide.Close(); e.Handled = true; return; }
        if (picker is not { IsOpen: true }) return;
        picker.Close();
        e.Handled = true;
    }

    private NpcapGuideModel? npcapGuide;
    private IInputElement? npcapGuideReturn;

    // 安装指引打开时焦点落在“打开下载页”，关闭后回到打开前的焦点；“复制地址”写入本窗口的剪贴板。
    private void WatchNpcapGuide(NpcapGuideModel model)
    {
        if (npcapGuide is not null) npcapGuide.PropertyChanged -= OnNpcapGuideChanged;
        npcapGuide = model;
        npcapGuide.CopyText = text => Clipboard is { } clipboard ? clipboard.SetTextAsync(text) : throw new InvalidOperationException("clipboard.unavailable");
        npcapGuide.PropertyChanged += OnNpcapGuideChanged;
    }

    private void OnNpcapGuideChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (npcapGuide is null || e.PropertyName != nameof(NpcapGuideModel.IsOpen)) return;
        if (npcapGuide.IsOpen)
        {
            npcapGuideReturn = FocusManager?.GetFocusedElement();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => NpcapGuideOpen.Focus(NavigationMethod.Tab), Avalonia.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            (npcapGuideReturn as InputElement ?? CaptureChip)?.Focus(NavigationMethod.Tab);
            npcapGuideReturn = null;
        }
    }

    private void OnNpcapGuideOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        npcapGuide?.Close();
        e.Handled = true;
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string edge } && Enum.TryParse<WindowEdge>(edge, out var windowEdge)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(windowEdge, e);
    }
}
