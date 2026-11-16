using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 素材包里的一张图（效果图标、乐器图标、清单封面），按种类与键取图。
/// 素材包有这张图时显示图片并隐藏其余子元素；没有时显示子元素（例如字章），
/// <see cref="CollapseWhenEmpty"/> 为真时整个控件不占位置。素材包载入或移除后自动更新。
/// </summary>
public sealed class PackImage : Panel
{
    public static readonly StyledProperty<AssetKind> KindProperty =
        AvaloniaProperty.Register<PackImage, AssetKind>(nameof(Kind));

    public static readonly StyledProperty<string?> KeyProperty =
        AvaloniaProperty.Register<PackImage, string?>(nameof(Key));

    public static readonly StyledProperty<bool> CollapseWhenEmptyProperty =
        AvaloniaProperty.Register<PackImage, bool>(nameof(CollapseWhenEmpty));

    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<PackImage, double>(nameof(Radius));

    public static readonly DirectProperty<PackImage, bool> HasImageProperty =
        AvaloniaProperty.RegisterDirect<PackImage, bool>(nameof(HasImage), image => image.HasImage);

    private readonly Image image = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false, IsVisible = false };
    private bool hasImage;

    public PackImage() => Children.Add(image);

    public AssetKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string? Key { get => GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public bool CollapseWhenEmpty { get => GetValue(CollapseWhenEmptyProperty); set => SetValue(CollapseWhenEmptyProperty, value); }

    /// <summary>图片的圆角；图片按这个圆角裁切。</summary>
    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    public bool HasImage { get => hasImage; private set => SetAndRaise(HasImageProperty, ref hasImage, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == KeyProperty || change.Property == CollapseWhenEmptyProperty)
            Update();
    }

    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        IconRegistry.Changed += OnPackChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        IconRegistry.Changed -= OnPackChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        image.Clip = Radius > 0
            ? new RectangleGeometry(new Rect(size), Radius, Radius)
            : null;
        return size;
    }

    private void OnPackChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var source = IconRegistry.Find(Kind, Key);
        image.Source = source;
        image.IsVisible = source is not null;
        foreach (var child in Children)
            if (!ReferenceEquals(child, image)) child.IsVisible = source is null;
        HasImage = source is not null;
        if (CollapseWhenEmpty) IsVisible = source is not null;
    }
}
