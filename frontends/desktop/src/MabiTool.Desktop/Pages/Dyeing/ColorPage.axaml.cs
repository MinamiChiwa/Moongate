using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MabiTool.Desktop.Controls;

namespace MabiTool.Desktop.Pages.Dyeing;

public sealed partial class ColorPage : UserControl
{
    private ColorPageModel? watched;
    private readonly LayerFade atlasFade = new();
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
    // 关闭时淡出后再隐藏（LayerFade），淡出期间位置与内容保持关闭那一刻的样子；选色浮层隐藏后才清空数据上下文。
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (watched is null) return;
        switch (e.PropertyName)
        {
            case nameof(ColorPageModel.IsPickerOpen):
                if (watched.PickerCard is { } card && Anchor("TargetSwatch", card) is { } swatch) ColorPicker.Open(card.Picker, swatch);
                else ColorPicker.Close();
                break;
            case nameof(ColorPageModel.IsAtlasOpen):
                if (watched.AtlasCard is { } atlas && Anchor("AtlasThumb", atlas) is { } thumb && thumb.TranslatePoint(default, Root) is { } origin)
                    atlasFade.Show(AtlasLayer, AtlasHost, AtlasPanel, new Rect(origin, thumb.Bounds.Size));
                else atlasFade.Hide(AtlasLayer, AtlasPanel);
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
        if (watched.IsAtlasOpen) watched.CloseAtlasCommand.Execute(null);
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
