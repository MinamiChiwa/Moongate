using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MabiTool.Desktop.Pages.Inventory;

public sealed partial class InventoryPage : UserControl
{
    public InventoryPage()
    {
        InitializeComponent();
        // 数据重读后按道具类型键恢复所选行。
        ItemsTable.RowKey = row => (row as InventoryPivotRow)?.TypeKey;
    }

    private void OnOpenWindowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is InventoryPageModel model) InventoryTableWindow.Open(model, TopLevel.GetTopLevel(this) as Window);
    }

    // 系统保存对话框由用户按下“导出 CSV”触发；导出的是表格当前看到的内容。
    private void OnExportClick(object? sender, RoutedEventArgs e) => _ = ItemsTable.ExportCsvAsync();
}
