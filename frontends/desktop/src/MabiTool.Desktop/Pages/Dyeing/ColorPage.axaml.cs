using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MabiTool.Desktop.Pages.Dyeing;

public sealed partial class ColorPage : UserControl
{
    private ColorPageModel? watched;
    // 每个层的开关序号：关闭后的延时隐藏只在期间没有再次打开时生效。
    private readonly Dictionary<Panel, int> tickets = [];
    private Point? dragFrom;
    private Vector dragOffset;

    public ColorPage()
    {
        InitializeComponent();
        // 区域卡的网格是项目列表的面板，建立之后才能交给标题栏的编辑入口。
        Loaded += (_, _) => LayoutEditBar.Target ??= CardList.ItemsPanelRoot as Controls.CardGrid;
        DataContextChanged += (_, _) =>
        {
            if (watched is not null) watched.PropertyChanged -= OnModelChanged;
            watched = DataContext as ColorPageModel;
            if (watched is not null) watched.PropertyChanged += OnModelChanged;
        };
        AtlasViewport.SizeChanged += (_, e) =>
        {
            if (watched is not null) watched.AtlasViewport = e.NewSize;
        };
        AtlasScroller.AddHandler(PointerPressedEvent, OnAtlasDragStart, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AtlasScroller.AddHandler(PointerMovedEvent, OnAtlasDrag, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AtlasScroller.AddHandler(PointerReleasedEvent, (_, _) => dragFrom = null, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // 浮层打开时锚定到对应卡片的色块或缩略图。锚点矩形在打开的这一刻同步取得并交给 AnchoredLayer，
    // 浮层在同一次排版里定位后才显示；之后的数据刷新（含页面模型的整体通知）不再改动位置。
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (watched is null) return;
        switch (e.PropertyName)
        {
            case nameof(ColorPageModel.IsPickerOpen):
                if (watched.PickerCard is { } picked) PickerContent.DataContext = picked.Picker;
                Toggle(PickerLayer, PickerHost, PickerPanel, watched.PickerCard is { } card ? Anchor("TargetSwatch", card) : null);
                break;
            case nameof(ColorPageModel.IsAtlasOpen):
                Toggle(AtlasLayer, AtlasHost, AtlasPanel, watched.AtlasCard is { } atlas ? Anchor("AtlasThumb", atlas) : null);
                break;
        }
        if (e.PropertyName is nameof(ColorPageModel.AtlasZoom) or nameof(ColorPageModel.IsAtlasOpen) or nameof(ColorPageModel.AtlasImage) or ""
            && watched.AtlasImage is { } image && !watched.AtlasFitted)
        {
            AtlasZoomed.Width = Math.Max(1, image.PixelSize.Width * watched.AtlasZoom);
            AtlasZoomed.Height = Math.Max(1, image.PixelSize.Height * watched.AtlasZoom);
        }
    }

    private Control? Anchor(string name, RegionCardModel card) => CardList.GetVisualDescendants().OfType<Control>()
        .FirstOrDefault(control => control.Name == name && ReferenceEquals(control.DataContext, card) && control.IsEffectivelyVisible);

    /// <summary>
    /// 打开：取锚点在页面里的矩形，显示层并加上 shown（不透明度与位移过渡）；关闭：去掉 shown、层不再接收指针，
    /// 不透明度过渡走到 0 后隐藏（减弱动效时立即隐藏）。关闭过程中再次打开时直接接着显示。
    /// 关闭时不改锚点与内容：选色浮层的数据上下文在隐藏之后才清空，淡出期间色相环、方块与位置保持关闭那一刻的样子。
    /// </summary>
    private void Toggle(Panel layer, AnchoredLayer host, Border panel, Control? anchor)
    {
        var current = tickets[layer] = tickets.GetValueOrDefault(layer) + 1;
        if (anchor?.TranslatePoint(default, Root) is { } origin)
        {
            host.Anchor = new Rect(origin, anchor.Bounds.Size);
            layer.IsHitTestVisible = true;
            layer.IsVisible = true;
            panel.Classes.Set("shown", true);
            return;
        }
        panel.Classes.Set("shown", false);
        layer.IsHitTestVisible = false;
        if (!layer.IsVisible) return;
        if (!MabiTool.Desktop.Theme.UiMotion.Active)
        {
            Hide(layer);
            return;
        }
        if (panel.Opacity <= 0.001)
        {
            Hide(layer);
            return;
        }
        // 不透明度过渡走到 0 时隐藏；期间再次打开（序号变化）则不隐藏。
        void OnFade(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property != Visual.OpacityProperty) return;
            if (tickets[layer] != current)
            {
                panel.PropertyChanged -= OnFade;
                return;
            }
            if (panel.Opacity > 0.001) return;
            panel.PropertyChanged -= OnFade;
            Hide(layer);
        }
        panel.PropertyChanged += OnFade;
    }

    private void Hide(Panel layer)
    {
        layer.IsVisible = false;
        if (ReferenceEquals(layer, PickerLayer)) PickerContent.DataContext = null;
    }

    private void OnPickerOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        watched?.ClosePickerCommand.Execute(null);
        e.Handled = true;
    }

    private void OnAtlasOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        watched?.CloseAtlasCommand.Execute(null);
        e.Handled = true;
    }

    private void OnLayerOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        watched?.CloseLayerCommand.Execute(null);
        e.Handled = true;
    }

    private void OnLayerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || watched is null) return;
        if (watched.IsPickerOpen) watched.ClosePickerCommand.Execute(null);
        else if (watched.IsAtlasOpen) watched.CloseAtlasCommand.Execute(null);
        else watched.CloseLayerCommand.Execute(null);
        e.Handled = true;
    }

    // 放大后按住拖动浏览（原工具 Flickable）。
    private void OnAtlasDragStart(object? sender, PointerPressedEventArgs e)
    {
        dragFrom = e.GetPosition(AtlasScroller);
        dragOffset = AtlasScroller.Offset;
    }

    private void OnAtlasDrag(object? sender, PointerEventArgs e)
    {
        if (dragFrom is not { } from) return;
        var delta = e.GetPosition(AtlasScroller) - from;
        AtlasScroller.Offset = new Vector(dragOffset.X - delta.X, dragOffset.Y - delta.Y);
    }
}
