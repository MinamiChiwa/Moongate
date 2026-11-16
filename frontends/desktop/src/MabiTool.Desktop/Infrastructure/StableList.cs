using Avalonia.Collections;

namespace MabiTool.Desktop.Infrastructure;

/// <summary>
/// 页面绑定用的固定集合：后端刷新时按位置就地更新，只替换内容变化的位置、在末尾增删，不清空重建。
/// 列表控件因此保留已生成的项容器，数值变化只更新文字，悬停与动效留在原位置。
/// </summary>
public sealed class StableList<T> : AvaloniaList<T>
{
    /// <summary>把集合更新为 <paramref name="next"/>；返回是否有变化。相等的项（记录按值比较）不替换。</summary>
    public bool Sync(IReadOnlyList<T> next)
    {
        var changed = false;
        var shared = Math.Min(Count, next.Count);
        for (var i = 0; i < shared; i++)
        {
            if (EqualityComparer<T>.Default.Equals(this[i], next[i])) continue;
            this[i] = next[i];
            changed = true;
        }
        if (next.Count > Count)
        {
            AddRange(next.Skip(Count).ToList());
            changed = true;
        }
        else if (next.Count < Count)
        {
            RemoveRange(next.Count, Count - next.Count);
            changed = true;
        }
        return changed;
    }
}
