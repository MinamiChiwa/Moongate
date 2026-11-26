using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Music;

/// <summary>
/// 乐谱表的分页：只显示一页，并把这一页的乐谱登记给后端查询时长，不对整个乐谱库逐张请求。
/// 换清单或搜索后乐谱集合改变，回到第一页；只有时长或状态变化时保持当前页。
/// </summary>
public sealed class ScorePager(IDesktopBackend backend, int pageSize)
{
    private IReadOnlyList<ScoreRow> rows = [];
    private string signature = string.Empty;
    private IReadOnlyList<string> requested = [];

    public int PageIndex { get; private set; }
    public int PageCount => Math.Max(1, (rows.Count + pageSize - 1) / pageSize);
    public IReadOnlyList<ScoreRow> Visible => rows.Skip(PageIndex * pageSize).Take(pageSize).ToList();
    public bool HasPages => rows.Count > pageSize;
    public bool CanPrevious => PageIndex > 0;
    public bool CanNext => PageIndex + 1 < PageCount;

    public string Label => UiText.Pick($"第 {PageIndex + 1}／{PageCount} 页，共 {rows.Count} 张",
        $"第 {PageIndex + 1}／{PageCount} 頁，共 {rows.Count} 張");

    /// <summary>换成后端最新的乐谱表。</summary>
    public void Update(IReadOnlyList<ScoreRow> next)
    {
        var nextSignature = string.Join('\n', next.Select(row => row.Id));
        if (nextSignature != signature) PageIndex = 0;
        signature = nextSignature;
        rows = next;
        PageIndex = Math.Min(PageIndex, PageCount - 1);
        Request();
    }

    public void Move(int delta)
    {
        PageIndex = Math.Clamp(PageIndex + delta, 0, PageCount - 1);
        Request();
    }

    // 同一页只登记一次（时长与游戏连接器的可演奏性），避免后端每次刷新又回头登记。
    private void Request()
    {
        var ids = Visible.Select(row => row.Id).ToList();
        if (ids.SequenceEqual(requested)) return;
        requested = ids;
        backend.RequestScoreDetails(ids);
        backend.RequestScoreSupport(ids);
    }
}
