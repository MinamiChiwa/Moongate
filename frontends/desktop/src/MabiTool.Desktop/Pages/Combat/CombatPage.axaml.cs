using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MabiTool.Desktop.Pages.Combat;

public sealed partial class CombatPage : UserControl
{
    public CombatPage() => InitializeComponent();

    private CombatPageModel? Model => DataContext as CombatPageModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Model?.SetAttached(true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null) Model?.SetAttached(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // 外壳切走页面时保留它（模型由外壳停用或释放），这里只停下页面自己的计时。
        Model?.SetAttached(false);
    }

    // 明细表的“在窗口中打开”：独立的 DPS 明细窗口（完整总表与图表）。
    private void OnOpenDetailClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Model is { } model) CombatDetailWindow.Open(model, TopLevel.GetTopLevel(this) as Window);
    }

    // 战斗记录行的“明细”：这条记录的明细窗口；已开满时在战斗记录卡片写明上限。
    private void OnOpenRecordClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Model is not { } model || (sender as Control)?.DataContext is not EncounterRow row) return;
        var opened = CombatDetailWindow.OpenRecord(model, row.Id, TopLevel.GetTopLevel(this) as Window);
        model.SetRecordWindowLimit(!opened, CombatDetailWindow.MaximumRecordWindows);
    }

    // 三个弹出层（定时测量设定、保存记录、重设确认）：点层外或按 Esc 关闭。
    private void CloseLayers()
    {
        if (Model is not { } model) return;
        if (model.IsTimerOpen) model.CloseTimerCommand.Execute(null);
        if (model.IsSaveOpen) model.CloseSaveCommand.Execute(null);
        if (model.IsResetOpen) model.CloseResetCommand.Execute(null);
    }

    private void OnLayerOutsidePressed(object? sender, PointerPressedEventArgs e)
    {
        CloseLayers();
        e.Handled = true;
    }

    private void OnLayerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseLayers();
        e.Handled = true;
    }
}
