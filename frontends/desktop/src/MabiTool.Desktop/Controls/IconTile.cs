using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 色块图标：按 <see cref="Slot"/> 从 <see cref="IconRegistry"/> 取内建矢量图标或素材包里的图片，
/// 没有登记时显示 <see cref="Fallback"/> 字章。所在按钮被悬停时播放悬停动效，
/// <see cref="IsActive"/> 变为真时播放选中动效；动效关闭时保持静止。
/// </summary>
public sealed class IconTile : Control
{
    public static readonly StyledProperty<string?> SlotProperty =
        AvaloniaProperty.Register<IconTile, string?>(nameof(Slot));
    public static readonly StyledProperty<string?> FallbackProperty =
        AvaloniaProperty.Register<IconTile, string?>(nameof(Fallback));
    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<IconTile, IBrush?>(nameof(Accent));
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<IconTile, bool>(nameof(IsActive));
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<IconTile, double>(nameof(Progress));

    private static readonly IPen Line = new Pen(Brushes.White, 1.9, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private string motion = IconMotion.None;
    private InputElement? host;
    private CancellationTokenSource? playing;

    static IconTile()
    {
        AffectsRender<IconTile>(SlotProperty, FallbackProperty, AccentProperty, ProgressProperty);
        IsActiveProperty.Changed.AddClassHandler<IconTile>((tile, change) =>
        {
            if (change.NewValue is true && tile.IsAttachedToVisualTree())
                tile.Play(IconRegistry.Resolve(tile.Slot)?.ActiveMotion);
        });
    }

    public IconTile()
    {
        Width = 30;
        Height = 30;
    }

    public string? Slot { get => GetValue(SlotProperty); set => SetValue(SlotProperty, value); }
    public string? Fallback { get => GetValue(FallbackProperty); set => SetValue(FallbackProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    /// <summary>当前动效的进度，0 到 1；静止时为 0。</summary>
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    /// <summary>正在播放的动效预设，供测试和调试查看。</summary>
    public string CurrentMotion => motion;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 悬停以所在按钮为准，指针在文字上时图标也会动。
        host = this.FindAncestorOfType<Button>() ?? (InputElement)this;
        host.PointerEntered += OnHostPointerEntered;
        IconRegistry.Changed += OnRegistryChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (host is not null) host.PointerEntered -= OnHostPointerEntered;
        host = null;
        IconRegistry.Changed -= OnRegistryChanged;
        playing?.Cancel();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var spec = IconRegistry.Resolve(Slot);
        var tile = new Rect(Bounds.Size);
        var radius = size * 0.3;

        var center = tile.Center;
        var transform = Matrix.CreateTranslation(-center.X, -center.Y) * IconMotion.Transform(motion, Progress, size)
                        * Matrix.CreateTranslation(center.X, center.Y);
        if (spec?.Image is { } image)
        {
            // 素材包图片铺满色块：色块与图片作为一个整体动，放大时圆角随之放大，图片不被色块自己的边界裁掉。
            using (context.PushTransform(transform))
            {
                context.DrawRectangle(Accent, null, tile, radius, radius);
                using (context.PushClip(new RoundedRect(tile, radius)))
                    context.DrawImage(image, tile);
            }
            return;
        }

        // 矢量线稿与字章：色块不动，字形在色块内动（字形占色块的 62%，动效的最大幅度也留在色块内）。
        context.DrawRectangle(Accent, null, tile, radius, radius);
        using (context.PushClip(new RoundedRect(tile, radius)))
        using (context.PushTransform(transform))
        {
            if (spec?.Vector is { } vector)
            {
                var scale = size * 0.62 / 24;
                var offset = (size - 24 * scale) / 2;
                using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset, offset)))
                    context.DrawGeometry(null, Line, vector);
            }
            else if (Fallback is { Length: > 0 } text)
            {
                var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(Avalonia.Controls.Documents.TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Bold), size * 0.5, Brushes.White);
                context.DrawText(formatted, new Point((size - formatted.Width) / 2, (size - formatted.Height) / 2));
            }
        }
    }

    /// <summary>播放一次动效；动效关闭或预设为 none 时不做任何事。</summary>
    public void Play(string? preset)
    {
        var next = IconMotion.Normalize(preset);
        if (!UiMotion.Active || next == IconMotion.None) return;
        playing?.Cancel();
        playing = new CancellationTokenSource();
        motion = next;
        var animation = new Animation
        {
            Duration = UiMotion.Icon, Easing = new LinearEasing(), FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ProgressProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ProgressProperty, 1d) } }
            }
        };
        _ = animation.RunAsync(this, playing.Token);
    }

    /// <summary>把图标定格在某个动效预设的某个进度上，供测试检查画出的外观。</summary>
    internal void Pose(string preset, double progress)
    {
        motion = IconMotion.Normalize(preset);
        Progress = progress;
    }

    private void OnHostPointerEntered(object? sender, PointerEventArgs e) => Play(IconRegistry.Resolve(Slot)?.HoverMotion);

    private void OnRegistryChanged(object? sender, EventArgs e) => InvalidateVisual();
}
