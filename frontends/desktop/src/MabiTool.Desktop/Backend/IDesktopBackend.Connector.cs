namespace MabiTool.Desktop.Backend;

// 游戏连接器：状态供侧栏胶囊、弹出层与设置页卡片显示，写操作由卡片发出；乐谱可演奏性供乐谱库与迷你窗决定“开始演奏”能否点。
// 成员都带默认实现，只提供部分接口的后端（测试替身）不必逐个实现。
public partial interface IDesktopBackend
{
    /// <summary>连接器的当前状态；桌面没连上后端、没读到或后端没有这个接口时由 <see cref="GameConnectorView.Read"/> 标明。</summary>
    GameConnectorView GetGameConnector() => GameConnectorView.Offline;

    /// <summary>
    /// 连接器写操作：<c>enable</c>、<c>disable</c>、<c>restore</c>、<c>refresh</c>、<c>set-directory</c>。
    /// <c>enable</c> 带 <paramref name="riskAcknowledged"/>（首次开启的风险说明已由用户确认）；<paramref name="gameDirectory"/> 只用于
    /// <c>set-directory</c> 与 <c>enable</c>。回执的 <see cref="BackendReply.Code"/> 是后端拒绝时的错误码。
    /// </summary>
    Task<BackendReply> CommandGameConnectorAsync(string action, bool riskAcknowledged, string? gameDirectory, CancellationToken cancellationToken) =>
        Task.FromResult(new BackendReply(false, string.Empty));

    /// <summary>
    /// 乐谱的可演奏性（只读缓存）：连接器没连上、还没查到或后端没有这个接口时为未知。查询由 <see cref="RequestScoreSupport"/> 登记。
    /// </summary>
    ScoreSupportView GameScoreSupportOf(string scoreId) => ScoreSupportView.Unknown;

    /// <summary>
    /// 登记页面当前显示的乐谱（乐谱表的一页、迷你窗的一页）。连接器已连接时后端适配层批量查询（一次最多 200 张），
    /// 查到后触发 <see cref="Changed"/>；连接器状态与乐谱来源没变时，已查过的不重复查询。
    /// </summary>
    void RequestScoreSupport(IReadOnlyList<string> scoreIds) { }
}
