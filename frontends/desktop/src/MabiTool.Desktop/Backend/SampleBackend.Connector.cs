using MabiTool.Contracts;
using MabiTool.Desktop.Localization;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Backend;

// 样本后端的游戏连接器：默认未启用、风险说明尚未确认；写操作按后端的规则改变状态（游戏运行时拒绝、启用需要确认风险）。
// 已连接时乐谱“月光下的艾明馬夏”是玩家自制乐谱，其余可演奏。
public sealed partial class SampleBackend
{
    /// <summary>测试用：连接器的当前状态；写操作会按后端的规则改变它。</summary>
    internal GameConnectorView ConnectorView { get; set; } = SampleConnector(ConnectorState.Disabled);

    /// <summary>测试用：收到的连接器写操作，按先后记录（动作、是否带风险确认、目录）。</summary>
    internal List<(string Action, bool RiskAcknowledged, string? Directory)> ConnectorCommands { get; } = [];

    /// <summary>测试用：连接器已连接时各乐谱的可演奏性；没有列出的乐谱可演奏。</summary>
    internal Dictionary<string, ScoreSupportView> ScoreSupport { get; } = new(StringComparer.Ordinal)
    {
        ["s4"] = new(ScoreSupportState.UserComposed, "台服连接器不支持播放玩家自制乐谱")
    };

    /// <summary>测试用：页面登记过的“需要查询可演奏性的乐谱”，按先后记录。</summary>
    internal List<IReadOnlyList<string>> ScoreSupportRequests { get; } = [];

    /// <summary>测试用：演奏会话最近一次的错误原因码，显示在“游戏内演奏”层的当前曲目下方。</summary>
    internal string? PlayError { get; set; }

    /// <summary>按状态组出一份自洽的连接器视图：写操作的可用性与被拒原因同后端一致。</summary>
    internal static GameConnectorView SampleConnector(ConnectorState state, bool riskAcknowledged = false, bool? gameRunning = null)
    {
        var enabled = state is ConnectorState.Connected or ConnectorState.GameNotRunning;
        var running = gameRunning ?? state is ConnectorState.Connected or ConnectorState.Expired;
        var missing = state == ConnectorState.Unavailable;
        var reason = state switch
        {
            ConnectorState.Disabled => "gameCli.disabled",
            ConnectorState.GameNotRunning => "gameCli.gameNotRunning",
            ConnectorState.Expired => "gameCli.expired",
            ConnectorState.Checking => "gameCli.checking",
            ConnectorState.Unavailable => "gameCli.cliMissing",
            _ => string.Empty
        };
        string? EnableBlocked() => enabled ? "gameCli.alreadyEnabled" : running ? "gameCli.gameRunning" : missing ? "gameCli.cliMissing" : null;
        string? DisableBlocked() => !enabled ? "gameCli.notEnabled" : running ? "gameCli.gameRunning" : null;
        string? RestoreBlocked() => running ? "gameCli.gameRunning" : !riskAcknowledged ? "gameCli.noBackup" : null;
        return new(ConnectorRead.Loaded, state, reason, enabled, riskAcknowledged,
            missing ? GameConnectorCliSource.None : GameConnectorCliSource.Package,
            missing ? GameConnectorCliFileState.Missing : GameConnectorCliFileState.Verified,
            missing ? null : "0.1.0+b587207e", enabled, false, running, @"C:\Nexon\MabinogiMobile", GameConnectorDirectorySource.Default,
            EnableBlocked() is null, EnableBlocked(), DisableBlocked() is null, DisableBlocked(), RestoreBlocked() is null, RestoreBlocked(),
            state == ConnectorState.Connected, false, state == ConnectorState.Connected ? 42 : 0);
    }

    public GameConnectorView GetGameConnector() => Offline ? GameConnectorView.Offline : ConnectorView;

    public Task<BackendReply> CommandGameConnectorAsync(string action, bool riskAcknowledged, string? gameDirectory, CancellationToken cancellationToken)
    {
        ConnectorCommands.Add((action, riskAcknowledged, gameDirectory));
        if (Offline) return Task.FromResult(new BackendReply(false, Pick("未连接后端", "未連接後端")));
        var view = ConnectorView;
        string? refusal = action switch
        {
            "enable" when !riskAcknowledged && !view.RiskAcknowledged => "gameCli.riskNotAcknowledged",
            "enable" => view.CanEnableNow ? null : view.GameIsRunning ? "gameCli.gameRunning" : view.EnableBlocked ?? "gameCli.cliUnavailable",
            "disable" => view.CanDisableNow ? null : view.GameIsRunning ? "gameCli.gameRunning" : view.DisableBlocked ?? "gameCli.notEnabled",
            "restore" => view.CanRestoreNow ? null : view.GameIsRunning ? "gameCli.gameRunning" : view.RestoreBlocked ?? "gameCli.noBackup",
            _ => null
        };
        if (refusal is not null)
            return Task.FromResult(new BackendReply(false, Pick("请求未完成：", "請求未完成：") + CodeText.Reason(refusal), refusal));
        ConnectorView = action switch
        {
            "enable" => SampleConnector(ConnectorState.GameNotRunning, true, false) with { GameDirectory = gameDirectory ?? view.GameDirectory },
            "disable" or "restore" => SampleConnector(ConnectorState.Disabled, true, false) with { GameDirectory = view.GameDirectory },
            "set-directory" => view with { GameDirectory = gameDirectory, DirectorySource = GameConnectorDirectorySource.Manual },
            _ => view
        };
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new BackendReply(true, ConnectorText.Done(action)));
    }

    public ScoreSupportView GameScoreSupportOf(string scoreId) =>
        !Offline && ConnectorView.State == ConnectorState.Connected
            ? ScoreSupport.GetValueOrDefault(scoreId, new(ScoreSupportState.Supported)) : ScoreSupportView.Unknown;

    public void RequestScoreSupport(IReadOnlyList<string> scoreIds) => ScoreSupportRequests.Add(scoreIds);
}
