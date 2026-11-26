using MabiTool.Desktop.Pages.Music;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 样本后端的清单与导入：清单内容固定，编辑与导入只记录页面发来的请求并给出成功回执，供界面与测试核对；加入队列时按所选清单带上乐器。
public sealed partial class SampleBackend
{
    /// <summary>页面发来的清单编辑，按先后记录。</summary>
    public List<MusicListEdit> MusicEdits { get; } = [];

    /// <summary>设为非空时清单编辑停用并写明该原因（模拟未连接后端或曲库不可读）。</summary>
    public string? MusicEditBlocked { get; set; }

    /// <summary>设为非空时下一次编辑被后端拒绝，回执带该错误码。</summary>
    public string? MusicEditRejection { get; set; }

    private MusicListsView SampleLists()
    {
        IReadOnlyList<PlaylistItemRow> Items(params (string Id, string ScoreId, string? Instrument)[] rows) => [.. rows.Select(row =>
        {
            var score = Scores.First(item => item.Id == row.ScoreId);
            return new PlaylistItemRow(row.Id, row.ScoreId, score.Title, row.Instrument, row.Instrument is null ? Pick("沿用清单", "沿用清單") : row.Instrument, false);
        })];
        var folder = new FolderRow("folder-show", "公演", "公演", 0);
        var campfire = new PlaylistDetail("campfire", "營火晚會", "週六晚場", "folder-show", "公演", "harp", "豎琴",
            Items(("i1", "s1", null), ("i2", "s2", "flute"), ("i3", "s3", null)));
        var practice = new PlaylistDetail("practice", "練習中", string.Empty, null, string.Empty, null, Pick("未指定", "未指定"),
            Items(("i4", "s5", null), ("i5", "s7", null)));
        return new([folder], [practice, campfire], playlist switch { "campfire" => campfire, "practice" => practice, _ => null },
            [new("piano", "鋼琴"), new("harp", "豎琴"), new("flute", "長笛")], MusicEditBlocked);
    }

    // 加入队列的乐器与真实后端同一规则：所选清单里该乐谱条目的乐器，条目没有写时取清单默认乐器；没有选清单时为空。
    private string? EnqueueInstrument(string scoreId) =>
        SampleLists().Selected is { } list && list.Items.FirstOrDefault(item => item.ScoreId == scoreId) is { } entry
            ? entry.InstrumentId ?? list.DefaultInstrumentId : null;

    public Task<BackendReply> EditMusicLibraryAsync(MusicListEdit edit, CancellationToken cancellationToken)
    {
        if (MusicEditBlocked is not null) return Task.FromResult(new BackendReply(false, MusicEditBlocked));
        MusicEdits.Add(edit);
        return Task.FromResult(MusicEditRejection is { } code
            ? new BackendReply(false, Pick("请求未完成：数据已变化，请刷新", "請求未完成：資料已變化，請重新整理"), code)
            : new BackendReply(true, Pick("已保存", "已儲存")));
    }
}
