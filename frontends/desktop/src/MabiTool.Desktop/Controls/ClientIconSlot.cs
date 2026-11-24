using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MabiTool.Desktop.Backend;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 属性标签的位置：<see cref="Icon"/> 对应的客户端图标可用时画出着色后的图标，<see cref="Label"/> 进悬浮提示与无障碍名称，
/// 子元素（原来的文字）变为透明；图标不可用（未绑定游戏、目录读取中、读取失败、该键不可用）时显示子元素。
/// 占位取图标与子元素两者的较大尺寸，图标出现或消失时整行不重排。图标边长默认为字号的 1.25 倍（随字号与所在元件的缩放变化）。
/// 着色：所在位置继承的 <see cref="TintsProperty"/>（浮窗配色里改过的图标颜色）优先，其次目录给出的颜色，
/// 没有时按 <see cref="ClientIconNames.DefaultTint"/>，再没有时用前景色；浅色主题（或 <see cref="Outline"/> 为真）
/// 时字形外加一圈深色描边，在浅色底上也看得清。挂上可见窗口时向 <see cref="ClientIconHub"/> 登记使用，有登记时后端才下载图标。
/// </summary>
public sealed class ClientIconSlot : Decorator
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<ClientIconSlot, string?>(nameof(Icon));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<ClientIconSlot, string?>(nameof(Label));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<ClientIconSlot, double>(nameof(IconSize), double.NaN);

    public static readonly StyledProperty<HorizontalAlignment> IconAlignmentProperty =
        AvaloniaProperty.Register<ClientIconSlot, HorizontalAlignment>(nameof(IconAlignment), HorizontalAlignment.Left);

    public static readonly StyledProperty<bool?> OutlineProperty =
        AvaloniaProperty.Register<ClientIconSlot, bool?>(nameof(Outline));

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<ClientIconSlot>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<ClientIconSlot>();

    /// <summary>
    /// 着色覆盖，按视觉树继承：浮窗给每个元件设上浮窗配色里改过的图标颜色，其余位置（主窗口、明细窗口）没有，图标保持原来的颜色。
    /// </summary>
    public static readonly AttachedProperty<ClientIconTints?> TintsProperty =
        AvaloniaProperty.RegisterAttached<ClientIconSlot, Control, ClientIconTints?>("Tints", inherits: true);

    public static ClientIconTints? GetTints(Control control) => control.GetValue(TintsProperty);
    public static void SetTints(Control control, ClientIconTints? value) => control.SetValue(TintsProperty, value);

    private ClientIconImage? image;
    private bool registered;

    static ClientIconSlot()
    {
        AffectsMeasure<ClientIconSlot>(IconSizeProperty, FontSizeProperty);
        AffectsRender<ClientIconSlot>(IconAlignmentProperty, OutlineProperty, ForegroundProperty, TintsProperty);
    }

    /// <summary>图标键（例如 critical、element.fire）；为空时只显示子元素。</summary>
    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <summary>条目名称：显示图标时放进悬浮提示与无障碍名称；为空时取目录的显示名称或工具自己的名称。</summary>
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>图标边长；不设时为字号的 1.25 倍。</summary>
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    /// <summary>图标在占位里的水平位置。</summary>
    public HorizontalAlignment IconAlignment { get => GetValue(IconAlignmentProperty); set => SetValue(IconAlignmentProperty, value); }

    /// <summary>是否加深色描边；不设时浅色主题下加。浮窗固定为真（亮色游戏画面上同样清楚）。</summary>
    public bool? Outline { get => GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }

    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>此刻是否显示图标（为假时显示子元素）。</summary>
    public bool ShowsIcon => image is not null;

    /// <summary>悬浮提示与无障碍名称使用的名称。</summary>
    public string DisplayLabel => Label is { Length: > 0 } label ? label
        : image?.DisplayName is { Length: > 0 } name ? name : ClientIconNames.Name(Icon);

    private double Side => double.IsFinite(IconSize) && IconSize > 0 ? IconSize : Math.Round(FontSize * 1.25);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ClientIconHub.Changed += OnIconsChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        Register(true);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClientIconHub.Changed -= OnIconsChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        Register(false);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty)
        {
            Register(this.IsAttachedToVisualTree());
            Update();
        }
        else if (change.Property == LabelProperty || change.Property == ChildProperty) Update();
    }

    // 只有带图标键的位置才算在使用图标。
    private void Register(bool attached)
    {
        var want = attached && Icon is { Length: > 0 };
        if (want == registered) return;
        registered = want;
        if (want) ClientIconHub.Acquire();
        else ClientIconHub.Release();
    }

    private void OnIconsChanged(object? sender, EventArgs e) => Update();

    // 浅色与深色主题的描边不同：主题切换后重画。
    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void Update()
    {
        var next = Icon is { Length: > 0 } key && this.IsAttachedToVisualTree() ? ClientIconHub.Get(key) : null;
        var changed = !ReferenceEquals(next, image);
        image = next;
        if (Child is { } child)
        {
            child.Opacity = image is null ? 1 : 0;
            child.IsHitTestVisible = image is null;
        }
        ToolTip.SetTip(this, image is null ? null : DisplayLabel);
        AutomationProperties.SetName(this, DisplayLabel);
        if (changed) InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Side;
        Child?.Measure(availableSize);
        var child = Child?.DesiredSize ?? default;
        return new Size(Math.Max(side, child.Width), Math.Max(side, child.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    /// <summary>图标在本控件坐标里的位置（不含描边）。</summary>
    public Rect IconRect
    {
        get
        {
            var side = Math.Min(Side, Math.Max(0, Bounds.Height));
            var x = IconAlignment switch
            {
                HorizontalAlignment.Center => (Bounds.Width - side) / 2,
                HorizontalAlignment.Right => Bounds.Width - side,
                _ => 0
            };
            return new Rect(Math.Max(0, x), (Bounds.Height - side) / 2, side, side);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (image is null) return;
        // 透明底让整个占位接收悬停（悬浮提示），图标外的空白处同样显示名称。
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var outline = Outline ?? ActualThemeVariant != ThemeVariant.Dark;
        var tint = GetTints(this)?.For(image.Key) ?? Tint(image, Foreground);
        var glyph = ClientIconArt.Compose(image.Bitmap, tint, outline);
        var target = IconRect;
        if (outline)
        {
            // 描边位于字形外侧，按源图的留边比例向外扩出。
            var grow = target.Width * ClientIconArt.Pad(image.Bitmap) / Math.Max(1, image.Bitmap.PixelSize.Width);
            target = target.Inflate(grow);
        }
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(glyph, new Rect(glyph.Size), target);
    }

    private static Color Tint(ClientIconImage image, IBrush? foreground)
    {
        var hex = image.Tint is { Length: > 0 } tint ? tint : ClientIconNames.DefaultTint(image.Key);
        if (hex is not null && Color.TryParse(hex, out var parsed)) return parsed;
        return foreground is ISolidColorBrush solid ? solid.Color : Colors.White;
    }

    /// <summary>
    /// <paramref name="root"/> 内在 <paramref name="point"/>（<paramref name="root"/> 的坐标）处显示着的图标的名称；没有时为空。
    /// 浮窗编辑状态下元件内容不接收输入，外框据此给出图标的悬浮提示。
    /// </summary>
    public static string? NameAt(Visual root, Point point)
    {
        foreach (var slot in root.GetVisualDescendants().OfType<ClientIconSlot>())
        {
            if (!slot.ShowsIcon || !slot.IsEffectivelyVisible) continue;
            if (root.TranslatePoint(point, slot) is { } local && new Rect(slot.Bounds.Size).Contains(local)) return slot.DisplayLabel;
        }
        return null;
    }
}

/// <summary>
/// 图标的着色覆盖：键为图标键（元素为 <c>element.</c> 加元素名，无防备为 <c>breakedHit</c>）或 <see cref="AttributeKey"/>
/// （其余属性图标：暴击、强击、连击、追击、破防、多重打击），值为颜色；没有的键沿用原来的颜色。
/// </summary>
public sealed class ClientIconTints(IReadOnlyDictionary<string, Color> colors)
{
    /// <summary>属性图标（元素与无防备以外）共用的键。</summary>
    public const string AttributeKey = "iconAttribute";
    /// <summary>无防备的键（浮窗配色里的项名）。</summary>
    public const string ExposedKey = "iconExposed";

    public Color? For(string iconKey)
    {
        var key = iconKey.StartsWith("element.", StringComparison.Ordinal) ? iconKey
            : iconKey == ClientIconNames.BreakedHit ? ExposedKey : AttributeKey;
        return colors.TryGetValue(key, out var color) ? color : null;
    }
}

/// <summary>
/// 把白色字形按颜色着色，需要时外加深色描边；结果按“源位图、颜色、是否描边”缓存，源位图释放后随之回收。
/// 字形像素按预乘 BGRA 处理：输出颜色 = 着色 × 源亮度，透明度不变。
/// </summary>
internal static class ClientIconArt
{
    private static readonly ConditionalWeakTable<Bitmap, Dictionary<(uint, bool), Bitmap>> Cache = new();

    /// <summary>浅色主题与浮窗的描边颜色（不透明度约 85%）。</summary>
    private static readonly Color Edge = Color.FromArgb(0xD9, 0x16, 0x1A, 0x26);

    /// <summary>描边在源图里的宽度：源图边长的 1/16（64 px 的图为 4 px，显示为 16 px 时约 1 px）。</summary>
    public static int Pad(Bitmap source) => Math.Max(1, (int)Math.Round(Math.Max(source.PixelSize.Width, source.PixelSize.Height) / 16.0));

    public static Bitmap Compose(Bitmap source, Color tint, bool outline)
    {
        var table = Cache.GetOrCreateValue(source);
        var key = (tint.ToUInt32(), outline);
        lock (table)
        {
            if (table.TryGetValue(key, out var cached)) return cached;
            var built = Build(source, tint, outline);
            table[key] = built;
            return built;
        }
    }

    private static Bitmap Build(Bitmap source, Color tint, bool outline)
    {
        var (width, height) = (source.PixelSize.Width, source.PixelSize.Height);
        var pixels = new byte[width * height * 4];
        using (var copy = new WriteableBitmap(source.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
        {
            using (var frame = copy.Lock())
            {
                source.CopyPixels(frame);
                for (var y = 0; y < height; y++)
                    Marshal.Copy(frame.Address + y * frame.RowBytes, pixels, y * width * 4, width * 4);
            }
        }
        var pad = outline ? Pad(source) : 0;
        var (outWidth, outHeight) = (width + pad * 2, height + pad * 2);
        var output = new byte[outWidth * outHeight * 4];
        if (outline)
        {
            // 描边：字形透明度在半径 pad 的圆内取最大值，乘以描边颜色（预乘）。
            var radius = pad * pad;
            for (var y = 0; y < outHeight; y++)
            for (var x = 0; x < outWidth; x++)
            {
                var alpha = 0;
                for (var dy = -pad; dy <= pad && alpha < 255; dy++)
                for (var dx = -pad; dx <= pad; dx++)
                {
                    if (dx * dx + dy * dy > radius) continue;
                    var (sx, sy) = (x - pad + dx, y - pad + dy);
                    if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                    alpha = Math.Max(alpha, pixels[(sy * width + sx) * 4 + 3]);
                }
                var a = alpha * Edge.A / 255;
                var at = (y * outWidth + x) * 4;
                output[at] = (byte)(Edge.B * a / 255);
                output[at + 1] = (byte)(Edge.G * a / 255);
                output[at + 2] = (byte)(Edge.R * a / 255);
                output[at + 3] = (byte)a;
            }
        }
        // 着色后的字形叠在描边之上（预乘的“源在上”合成）。
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var from = (y * width + x) * 4;
            var sa = pixels[from + 3];
            if (sa == 0) continue;
            var (b, g, r) = (tint.B * pixels[from] / 255, tint.G * pixels[from + 1] / 255, tint.R * pixels[from + 2] / 255);
            var to = ((y + pad) * outWidth + x + pad) * 4;
            var keep = 255 - sa;
            output[to] = (byte)(b + output[to] * keep / 255);
            output[to + 1] = (byte)(g + output[to + 1] * keep / 255);
            output[to + 2] = (byte)(r + output[to + 2] * keep / 255);
            output[to + 3] = (byte)(sa + output[to + 3] * keep / 255);
        }
        var result = new WriteableBitmap(new PixelSize(outWidth, outHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var frame = result.Lock())
            for (var y = 0; y < outHeight; y++)
                Marshal.Copy(output, y * outWidth * 4, frame.Address + y * frame.RowBytes, outWidth * 4);
        return result;
    }
}
