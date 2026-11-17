using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 说明问号：页面与卡片里的解释性文字（统计口径、功能介绍、表单项的背景）收在这里，不占页面行。
/// 指针悬停显示悬停提示；键盘 Tab 聚焦时自动显示，回车或空格切换显示；点按同样切换，便于触屏。
/// 无障碍名称为“说明”，说明全文放在帮助文本里，读屏聚焦即可读出。
/// </summary>
public sealed class HelpTip : Control
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<HelpTip, string?>(nameof(Text));

    private const double Diameter = 16;

    static HelpTip()
    {
        FocusableProperty.OverrideDefaultValue<HelpTip>(true);
        IsTabStopProperty.OverrideDefaultValue<HelpTip>(true);
        AffectsRender<HelpTip>(TextProperty);
        TextProperty.Changed.AddClassHandler<HelpTip>((tip, _) => tip.Apply());
    }

    public HelpTip()
    {
        Width = Diameter + 4;
        Height = Diameter + 4;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Cursor = new Cursor(StandardCursorType.Help);
        ToolTip.SetShowDelay(this, 200);
        Apply();
    }

    /// <summary>说明全文。</summary>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private void Apply()
    {
        ToolTip.SetTip(this, Text is { Length: > 0 } text ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } : null);
        AutomationProperties.SetName(this, UiText.Pick("说明", "說明"));
        AutomationProperties.SetHelpText(this, Text);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) InvalidateVisual();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.NavigationMethod == NavigationMethod.Tab) ToolTip.SetIsOpen(this, true);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space)
        {
            ToolTip.SetIsOpen(this, !ToolTip.GetIsOpen(this));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ToolTip.GetIsOpen(this))
        {
            ToolTip.SetIsOpen(this, false);
            e.Handled = true;
        }
    }

    // 问号可能放在卡片标题的切换按钮里：按下由问号自己处理，不让外层按钮捕获指针或折叠卡片。
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        ToolTip.SetIsOpen(this, !ToolTip.GetIsOpen(this));
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var brush = Brush(IsFocused ? "InkBrush" : "MutedBrush");
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, new Pen(brush, 1.2), center, Diameter / 2 - 0.6, Diameter / 2 - 0.6);
        var mark = new FormattedText("?", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
        context.DrawText(mark, new Point(center.X - mark.Width / 2, center.Y - mark.Height / 2));
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
}
