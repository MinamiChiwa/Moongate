using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Controls.Templates;

namespace MabiTool.Desktop.Controls.Tables;

/// <summary>列的取值种类：数字列按数值排序、按范围筛选并右对齐；其余按文字。</summary>
public enum ColumnKind { Text, Number }

/// <summary>
/// 通用表格的一列。单元格文字取自 <see cref="Value"/> 或行对象上名为 <see cref="Path"/> 的属性；
/// 排序、筛选、检索、复制与导出都使用这段文字，<see cref="CellTemplate"/> 只改变显示。
/// 表头文字取 <see cref="HeaderKey"/> 指向的界面文字资源，没有时用 <see cref="Header"/>（例如角色名原文）。
/// </summary>
public sealed class TableColumn
{
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Properties = new();

    /// <summary>列标识；列宽、顺序、显隐与排序按它保存。</summary>
    public string Id { get; set; } = string.Empty;
    public string? Header { get; set; }
    public string? HeaderKey { get; set; }
    /// <summary>列组标题（例如“背包”）；相邻的同组列在表头上方合并显示一个组标题。</summary>
    public string? Group { get; set; }
    public string? GroupKey { get; set; }
    public string? Path { get; set; }
    public Func<object, string?>? Value { get; set; }
    /// <summary>为真时单元格文字显示为次要色（例如“未读取”）。</summary>
    public Func<object, bool>? Dim { get; set; }
    public string? DimPath { get; set; }
    /// <summary>单元格的悬停提示；为空时不显示。</summary>
    public Func<object, string?>? ToolTip { get; set; }
    public string? ToolTipPath { get; set; }
    public ColumnKind Kind { get; set; }
    public double Width { get; set; } = 120;
    public double MinWidth { get; set; } = 44;
    /// <summary>表格比各列总宽更宽时，这一列占满剩余宽度。</summary>
    public bool Fill { get; set; }
    public bool Sortable { get; set; } = true;
    public bool Filterable { get; set; } = true;
    public bool CanHide { get; set; } = true;
    /// <summary>默认不显示；用户可以在表头右键菜单里勾选。</summary>
    public bool HiddenByDefault { get; set; }
    /// <summary>每行可编辑：双击或 F2 进入编辑，Enter 确认、Esc 取消，确认后交给 <see cref="Commit"/>。</summary>
    public bool Editable { get; set; }
    /// <summary>可在表头右键菜单里改名，名称随表格设置保存。</summary>
    public bool Renamable { get; set; }
    public Action<object, string>? Commit { get; set; }
    /// <summary>文字单元格附加的样式类（空格分隔），例如 strong。</summary>
    public string? CellClasses { get; set; }
    public IDataTemplate? CellTemplate { get; set; }

    public string TextOf(object? row)
    {
        if (row is null) return string.Empty;
        if (Value is not null) return Value(row) ?? string.Empty;
        return Path is null ? string.Empty : Read(row, Path)?.ToString() ?? string.Empty;
    }

    public bool IsDim(object? row) => row is not null && (Dim?.Invoke(row) ?? (DimPath is not null && Read(row, DimPath) is true));

    public string? ToolTipOf(object? row) => row is null ? null
        : ToolTip is not null ? ToolTip(row)
        : ToolTipPath is null ? null : Read(row, ToolTipPath)?.ToString() is { Length: > 0 } tip ? tip : null;

    private static object? Read(object row, string path)
    {
        var property = Properties.GetOrAdd((row.GetType(), path), key => key.Item1.GetProperty(key.Item2));
        return property?.GetValue(row);
    }
}
