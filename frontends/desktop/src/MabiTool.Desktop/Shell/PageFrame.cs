using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using MabiTool.Desktop.Modules;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 一个模块的页面外框，所有模块（内建、扩展、外壳入口）共用，每个模块一个、建立后一直复用。
/// 切到该模块时外框立即显示骨架（模块标题与两块占位面板，标题位置与页面相同），页面本身在之后的调度帧（后台优先级）里建立，
/// 不占用这一次点击与随后的首帧；还没建立就切走时取消建立。页面建立后：可保留的页面（<see cref="IPageLifecycle.Cacheable"/>）
/// 留在外框里，回来时直接显示；其余页面切走时释放模型，下次回来先显示骨架再重建。
/// 页面报告 <see cref="IPageLifecycle.PendingRefresh"/> 时右上角显示“更新中”，不占布局、不接收指针。
/// </summary>
public sealed class PageFrame : Grid
{
    private readonly Decorator host = new();
    private readonly Border refreshing;
    private readonly TextBlock refreshingText;
    private int ticket;
    private bool scheduled;
    private bool stale;
    private INotifyPropertyChanged? watched;

    public PageFrame(ModuleDescriptor module)
    {
        Module = module;
        refreshingText = new TextBlock { FontSize = 12, FontWeight = Avalonia.Media.FontWeight.Bold };
        refreshingText.Classes.Add("muted");
        refreshing = new Border
        {
            Child = refreshingText, Padding = new Thickness(10, 3), CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 46, 8, 0), IsHitTestVisible = false, IsVisible = false
        };
        refreshing.Classes.Add("inset");
        refreshing.Name = "PageRefreshing";
        Children.Add(host);
        Children.Add(refreshing);
        host.Child = Skeleton();
    }

    /// <summary>
    /// 测试用：为真时切换页面在返回前就建立页面（不经调度帧）。默认假；测试也可以在切换后调用
    /// <see cref="EnsureBuilt"/> 或推进调度队列等待建立完成。
    /// </summary>
    public static bool BuildImmediately { get; set; }

    public ModuleDescriptor Module { get; }

    /// <summary>已经建立的页面；还在显示骨架时为空。</summary>
    public Control? Page { get; private set; }

    /// <summary>当前显示的是骨架（页面尚未建立或已释放）。</summary>
    public bool ShowsSkeleton => Page is null;

    /// <summary>页面建立的次数；测试据此确认回到页面时没有重建。</summary>
    public int BuildCount { get; private set; }

    private IPageLifecycle? Lifecycle => Page?.DataContext as IPageLifecycle;

    /// <summary>页面切走后保留。</summary>
    internal bool Keeps => Lifecycle is { Cacheable: true };

    /// <summary>切到这个模块：保留的页面重新激活；页面已释放或还没建立时显示骨架并安排建立。</summary>
    internal void Show()
    {
        if (stale) Discard();
        if (Page is not null)
        {
            if (!laidOut) HoldOffscreenPanels(Page);
            Lifecycle?.Activate();
            return;
        }
        if (BuildImmediately) EnsureBuilt();
        else Schedule();
    }

    /// <summary>切走：保留的页面停止接收通知；其余页面释放模型（界面留到下次显示前再换成骨架，淡出时不闪）；还没建立的取消建立。</summary>
    internal void Leave()
    {
        Cancel();
        RevealAll();
        if (Page is null) return;
        if (Keeps)
        {
            Lifecycle!.Deactivate();
            return;
        }
        Unwatch();
        (Page.DataContext as IDisposable)?.Dispose();
        stale = true;
    }

    /// <summary>立即建立页面（已建立时直接返回）；需要马上操作页面内容时（例如滚到设置页的某一区）使用。</summary>
    public Control EnsureBuilt()
    {
        if (stale) Discard();
        Cancel();
        if (Page is { } existing) return existing;
        var page = Module.CreatePage() as Control ?? throw new InvalidOperationException($"模块 {Module.Id} 的页面不是控件");
        Page = page;
        BuildCount++;
        // 已在页面区里时分块排出；空闲时预建（还没挂上）的页面等第一次显示时再分块。
        if (((Visual)this).IsAttachedToVisualTree()) HoldOffscreenPanels(page);
        host.Child = page;
        Watch(page.DataContext as INotifyPropertyChanged);
        Built?.Invoke(this, EventArgs.Empty);
        return page;
    }

    /// <summary>页面建立完成。</summary>
    public event EventHandler? Built;

    /// <summary>释放页面模型并回到骨架（再次选中当前模块、切换界面语言、关闭窗口时）。</summary>
    internal void Release()
    {
        Cancel();
        if (Page is not null && !stale) (Page.DataContext as IDisposable)?.Dispose();
        Discard();
    }

    private void Discard()
    {
        laidOut = false;
        RevealAll();
        Unwatch();
        Page = null;
        stale = false;
        host.Child = Skeleton();
    }

    /// <summary>
    /// 页面首屏之外、尚未显示出来的顶层面板数；为 0 时页面已完整排出。
    /// </summary>
    public int PendingPanels => holds.Count;

    /// <summary>页面建立时先排出的顶层面板数（按文档顺序，约等于首屏）。</summary>
    internal const int FirstScreenPanels = 3;

    private readonly Queue<IDisposable> holds = new();

    // 分块排版：页面控件一次建好，但首屏之后的顶层面板（Border.panel，不含弹出层与嵌在别的面板里的）先以动画优先级隐藏，
    // 第一次排版只量首屏；之后每个调度帧（后台优先级）放出一块，每块的排版各占一帧，中间照常处理输入与绘制。
    // 动画优先级不覆盖页面自己对 IsVisible 的绑定，放出时撤销即回到页面原来的可见性。
    private bool laidOut;

    /// <summary>
    /// 空闲时预建：建立页面控件树与模型后立即停用（不接收后端通知、不跑计时器），第一次点开时只需挂上并排版。
    /// 不保留的页面不预建。
    /// </summary>
    internal void Prebuild()
    {
        if (Page is not null || stale) return;
        EnsureBuilt();
        if (Keeps) Lifecycle!.Deactivate();
        else Release();
    }

    private void HoldOffscreenPanels(Control page)
    {
        laidOut = true;
        var panels = page.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("panel") && !border.Classes.Contains("popup"))
            .Where(border => !border.GetLogicalAncestors().TakeWhile(ancestor => ancestor != page)
                .Any(ancestor => ancestor is Border { Classes: var classes } && classes.Contains("panel")))
            .Skip(FirstScreenPanels).ToList();
        foreach (var panel in panels)
            if (panel.SetValue(IsVisibleProperty, false, Avalonia.Data.BindingPriority.Animation) is { } hold) holds.Enqueue(hold);
        if (holds.Count > 0) ScheduleReveal();
    }

    private void ScheduleReveal() => Dispatcher.UIThread.Post(() =>
    {
        if (RevealNext()) ScheduleReveal();
    }, DispatcherPriority.Background);

    /// <summary>放出下一块；返回是否还有没放出的。</summary>
    internal bool RevealNext()
    {
        if (holds.Count > 0) holds.Dequeue().Dispose();
        return holds.Count > 0;
    }

    private void RevealAll()
    {
        while (holds.Count > 0) holds.Dequeue().Dispose();
    }

    private void Schedule()
    {
        if (scheduled) return;
        scheduled = true;
        var mine = ++ticket;
        Dispatcher.UIThread.Post(() =>
        {
            if (scheduled && mine == ticket) EnsureBuilt();
        }, DispatcherPriority.Background);
    }

    private void Cancel()
    {
        scheduled = false;
        ticket++;
    }

    private void Watch(INotifyPropertyChanged? model)
    {
        Unwatch();
        watched = model;
        if (watched is not null) watched.PropertyChanged += OnModelChanged;
        UpdateRefreshing();
    }

    private void Unwatch()
    {
        if (watched is not null) watched.PropertyChanged -= OnModelChanged;
        watched = null;
        refreshing.IsVisible = false;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(IPageLifecycle.PendingRefresh)) UpdateRefreshing();
    }

    private void UpdateRefreshing()
    {
        var pending = Lifecycle?.PendingRefresh == true;
        if (pending) refreshingText.Text = UiText.Pick("更新中", "更新中");
        refreshing.IsVisible = pending;
    }

    // 骨架：与页面相同位置的标题，下面两块占位面板；不读取任何数据。
    private Control Skeleton()
    {
        var title = new TextBlock { Text = Module.Title.Current };
        title.Classes.Add("h1");
        var head = new StackPanel { Margin = new Thickness(8, 4, 8, 14), Children = { title } };
        DockPanel.SetDock(head, Dock.Top);
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 0, 18) };
        foreach (var height in new[] { 180.0, 260.0 })
        {
            var lines = new StackPanel { Spacing = 10 };
            foreach (var width in new[] { 160.0, double.NaN, double.NaN })
            {
                var bar = new Border { Height = 14, Width = width, HorizontalAlignment = HorizontalAlignment.Stretch };
                bar.Classes.Add("skeleton");
                lines.Children.Add(bar);
            }
            var block = new Border { Height = height, Child = lines };
            block.Classes.Add("panel");
            block.Classes.Add("skeletonpanel");
            body.Children.Add(block);
        }
        return new DockPanel { Name = "PageSkeleton", Children = { head, body } };
    }
}
