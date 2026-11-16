using System.Collections;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using MabiTool.Desktop.Infrastructure;

namespace MabiTool.Desktop.Controls;

/// <summary>
/// 列表控件的固定数据源：<c>controls:StableItems.Source="{Binding X}"</c> 代替 <c>ItemsSource="{Binding X}"</c>。
/// <list type="bullet">
/// <item>X 是可通知集合（<see cref="INotifyCollectionChanged"/>，例如页面模型持有的 <see cref="StableList{T}"/>）时直接作为
/// <c>ItemsSource</c>：集合就地增删改，控件按集合事件更新，已有容器保留。</item>
/// <item>X 是每次刷新都新给出的列表时，控件的 <c>ItemsSource</c> 始终是同一个内部 <see cref="StableList{T}"/>，
/// 只替换内容变化的项（记录按值比较）、在末尾增删；内容没变的项容器保留，按钮不会在按下与松开之间被换掉。</item>
/// </list>
/// </summary>
public static class StableItems
{
    public static readonly AttachedProperty<IEnumerable?> SourceProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, IEnumerable?>("Source", typeof(StableItems));

    private static readonly ConditionalWeakTable<ItemsControl, StableList<object>> Lists = new();

    static StableItems() => SourceProperty.Changed.AddClassHandler<ItemsControl>(OnSourceChanged);

    public static IEnumerable? GetSource(ItemsControl control) => control.GetValue(SourceProperty);
    public static void SetSource(ItemsControl control, IEnumerable? value) => control.SetValue(SourceProperty, value);

    private static void OnSourceChanged(ItemsControl control, AvaloniaPropertyChangedEventArgs e)
    {
        // 可通知集合自己会报告就地变化：直接交给控件，不复制（复制只发生在引用变化时，就地改动会被漏掉）。
        if (e.NewValue is INotifyCollectionChanged and IEnumerable observable)
        {
            if (!ReferenceEquals(control.ItemsSource, observable)) control.ItemsSource = observable;
            return;
        }
        var list = Lists.GetValue(control, _ => new StableList<object>());
        list.Sync((e.NewValue as IEnumerable)?.Cast<object>().ToList() ?? []);
        if (!ReferenceEquals(control.ItemsSource, list)) control.ItemsSource = list;
    }
}
