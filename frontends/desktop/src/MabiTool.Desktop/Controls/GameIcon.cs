using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MabiTool.Desktop.Theme;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 随包游戏图标（<see cref="GameIcons"/>）：<see cref="Icon"/> 为“分组/键”。白色字形按 <see cref="Tint"/> 着色（不设时用继承的前景色，
/// 随浅色与深色主题变化），彩色图标原样显示。图标对应不上（没有登记或为空）时不占位，界面只剩名称与数值。
/// 名称由所在行的文字或悬停提示给出，图标本身不接收输入。
/// </summary>
public sealed class GameIcon : Control
{
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<GameIcon, string?>(nameof(Icon));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<GameIcon, double>(nameof(Size), 20);
    public static readonly StyledProperty<IBrush?> TintProperty = AvaloniaProperty.Register<GameIcon, IBrush?>(nameof(Tint));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<GameIcon>();

    private GameIconEntry? entry;
    private Bitmap? bitmap;

    static GameIcon()
    {
        AffectsMeasure<GameIcon>(IconProperty, SizeProperty);
        AffectsRender<GameIcon>(IconProperty, TintProperty, ForegroundProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<GameIcon>(false);
        FocusableProperty.OverrideDefaultValue<GameIcon>(false);
    }

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public IBrush? Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>此刻是否有图可画。</summary>
    public bool HasIcon => bitmap is not null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IconProperty) return;
        entry = GameIcons.Find(Icon);
        bitmap = entry is null ? null : GameIcons.Bitmap(entry);
    }

    protected override Size MeasureOverride(Size availableSize) => bitmap is null ? default : new Size(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (bitmap is null || entry is null) return;
        var target = new Rect((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2, Size, Size);
        var image = entry.Glyph ? ClientIconArt.Compose(bitmap, Color(Tint ?? Foreground), outline: false) : bitmap;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(image, new Rect(image.Size), target);
    }

    private static Color Color(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;
}
