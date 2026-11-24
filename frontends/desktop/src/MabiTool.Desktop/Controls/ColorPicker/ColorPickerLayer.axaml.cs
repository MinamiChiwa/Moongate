using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 选色浮层（染色页与浮窗配色共用）。放在页面根容器里铺满页面；<see cref="Open"/> 按锚点控件的位置打开并绑定选色模型，
/// <see cref="Close"/> 淡出后隐藏并清空数据上下文。点浮层外、按 Esc、按“✕”或“确定”都执行 <see cref="CloseCommand"/>，
/// 由页面决定关闭时做什么（记入最近颜色、清除所选项）后再调用 <see cref="Close"/>。
/// </summary>
public sealed partial class ColorPickerLayer : UserControl
{
    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<ColorPickerLayer, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ColorPickerLayer, string?>(nameof(Title));

    public static readonly StyledProperty<bool> HexEditableProperty =
        AvaloniaProperty.Register<ColorPickerLayer, bool>(nameof(HexEditable));

    private readonly LayerFade fade = new();

    public ColorPickerLayer() => InitializeComponent();

    /// <summary>关闭浮层的命令：点浮层外、Esc、“✕”与“确定”。</summary>
    public ICommand? CloseCommand { get => GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    /// <summary>标题行的文字（染色页“选择目标色”，浮窗配色为所选项的名称）。</summary>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>标题行的 HEX 可以直接输入（只认六位十六进制）；为假时只显示。</summary>
    public bool HexEditable { get => GetValue(HexEditableProperty); set => SetValue(HexEditableProperty, value); }

    /// <summary>浮层是否打开（关闭过渡期间为假）。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>
    /// 以 <paramref name="model"/> 打开，贴在 <paramref name="anchor"/> 下方（放不下时按 <see cref="AnchoredLayer.Place"/> 的规则）。
    /// 锚点矩形在打开的这一刻取得并保持，之后的数据刷新不移动浮层。锚点不在本控件的视觉树里时不打开。
    /// </summary>
    public void Open(ColorPickerModel model, Control anchor)
    {
        if (anchor.TranslatePoint(default, this) is not { } origin) return;
        PickerContent.DataContext = model;
        IsOpen = true;
        fade.Show(PickerLayer, PickerHost, PickerPanel, new Rect(origin, anchor.Bounds.Size));
    }

    /// <summary>淡出并隐藏；隐藏之后清空数据上下文，淡出期间内容保持关闭那一刻的样子。</summary>
    public void Close()
    {
        IsOpen = false;
        fade.Hide(PickerLayer, PickerPanel, () =>
        {
            if (!IsOpen) PickerContent.DataContext = null;
        });
    }

    private void OnOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        CloseCommand?.Execute(null);
        e.Handled = true;
    }

    private void OnLayerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseCommand?.Execute(null);
        e.Handled = true;
    }
}
