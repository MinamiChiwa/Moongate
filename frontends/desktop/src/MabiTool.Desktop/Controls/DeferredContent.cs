using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using Avalonia.Threading;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 页面里较重的一块：页面先按 <see cref="PlaceholderHeight"/> 排出它的位置，内容模板在挂到窗口后的下一个调度帧（后台优先级）里建立，
/// 不占用切换页面的那一帧；建立后保留，页面切走再回来不重建。同一页的几块按挂上的先后各占一帧，中间照常处理输入与绘制。
/// 写法：<c>&lt;controls:DeferredContent PlaceholderHeight="240"&gt;&lt;DataTemplate x:DataType="..."&gt;...&lt;/DataTemplate&gt;&lt;/controls:DeferredContent&gt;</c>。
/// 模板里的 <c>x:Name</c> 属于模板自己的名称范围，代码后置取不到；需要按名称取的控件不要放进来。
/// </summary>
public sealed class DeferredContent : Decorator
{
    public static readonly StyledProperty<IDataTemplate?> ContentTemplateProperty =
        AvaloniaProperty.Register<DeferredContent, IDataTemplate?>(nameof(ContentTemplate));

    public static readonly StyledProperty<double> PlaceholderHeightProperty =
        AvaloniaProperty.Register<DeferredContent, double>(nameof(PlaceholderHeight));

    private bool scheduled;

    [Content]
    public IDataTemplate? ContentTemplate { get => GetValue(ContentTemplateProperty); set => SetValue(ContentTemplateProperty, value); }

    /// <summary>内容建立之前占的高度，宜接近内容的实际高度，建立后不再跳动。</summary>
    public double PlaceholderHeight { get => GetValue(PlaceholderHeightProperty); set => SetValue(PlaceholderHeightProperty, value); }

    /// <summary>内容是否已经建立。</summary>
    public bool IsBuilt => Child is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Child is not null || scheduled) return;
        scheduled = true;
        Dispatcher.UIThread.Post(Build, DispatcherPriority.Background);
    }

    /// <summary>立即建立内容（已建立时不变）。</summary>
    public void Build()
    {
        scheduled = false;
        if (Child is not null || ContentTemplate is not { } template) return;
        Child = template.Build(DataContext);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        Child is null ? new Size(0, Math.Min(PlaceholderHeight, availableSize.Height)) : base.MeasureOverride(availableSize);
}
