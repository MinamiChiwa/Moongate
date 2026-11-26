using MabiTool.Contracts;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Backend;

/// <summary>游戏连接器的状态，对应契约 <see cref="GameConnectorStatus"/>；<see cref="Unknown"/> 表示没有读到快照或后端给出了未收录的取值。</summary>
public enum ConnectorState { Unknown, Checking, Unavailable, Disabled, GameNotRunning, Connected, Expired }

/// <summary>连接器快照的读取情况：桌面没连上后端、已连上但还没读到、后端没有这个接口（旧宿主）、已读到。</summary>
public enum ConnectorRead { Offline, NotRead, Unsupported, Loaded }

/// <summary>
/// 游戏连接器（台服游戏自带的命令行工具）的当前状态：只含事实，文字由 <see cref="ConnectorText"/> 按界面语言生成。
/// 状态胶囊、弹出层与设置页卡片读同一份。<see cref="PreferenceEnabled"/> 是台服客户端偏好里的开关，未读到为空；
/// 命令行文件的来源、核对结果与目录来源为空表示后端给出了未收录的取值。
/// </summary>
public sealed record GameConnectorView(ConnectorRead Read, ConnectorState State, string Reason, bool? PreferenceEnabled, bool RiskAcknowledged,
    GameConnectorCliSource? CliSource, GameConnectorCliFileState? CliState, string? CliVersion, bool CliDeployed, bool CliDeployedDiffers,
    bool? GameRunning, string? GameDirectory, GameConnectorDirectorySource? DirectorySource,
    bool CanEnable, string? EnableBlocked, bool CanDisable, string? DisableBlocked, bool CanRestore, string? RestoreBlocked,
    bool CatalogLoaded, bool CatalogLoading, int CatalogCount)
{
    /// <summary>桌面没有连上后端。</summary>
    public static GameConnectorView Offline { get; } = new(ConnectorRead.Offline, ConnectorState.Unknown, string.Empty, null, false,
        null, null, null, false, false, null, null, null, false, null, false, null, false, null, false, false, 0);

    /// <summary>已连上后端，连接器快照尚未读到（或读取失败）。</summary>
    public static GameConnectorView NotRead { get; } = Offline with { Read = ConnectorRead.NotRead };

    /// <summary>后端没有提供连接器接口。</summary>
    public static GameConnectorView Unsupported { get; } = Offline with { Read = ConnectorRead.Unsupported };

    public static GameConnectorView From(GameConnectorSnapshot snapshot) => new(ConnectorRead.Loaded,
        snapshot.Status.KnownValue switch
        {
            GameConnectorStatus.Checking => ConnectorState.Checking,
            GameConnectorStatus.Unavailable => ConnectorState.Unavailable,
            GameConnectorStatus.Disabled => ConnectorState.Disabled,
            GameConnectorStatus.GameNotRunning => ConnectorState.GameNotRunning,
            GameConnectorStatus.Connected => ConnectorState.Connected,
            GameConnectorStatus.Expired => ConnectorState.Expired,
            _ => ConnectorState.Unknown
        },
        snapshot.Reason, snapshot.PreferenceEnabled, snapshot.RiskAcknowledged,
        snapshot.Cli.Source.KnownValue, snapshot.Cli.State.KnownValue, snapshot.Cli.Version, snapshot.Cli.Deployed, snapshot.Cli.DeployedDiffers,
        snapshot.Game.Running, snapshot.Game.Directory, snapshot.Game.DirectorySource.KnownValue,
        snapshot.Actions.CanEnable, snapshot.Actions.EnableBlocked, snapshot.Actions.CanDisable, snapshot.Actions.DisableBlocked,
        snapshot.Actions.CanRestore, snapshot.Actions.RestoreBlocked,
        snapshot.Catalog.Loaded, snapshot.Catalog.Loading, snapshot.Catalog.CommandCount);

    public bool IsLoaded => Read == ConnectorRead.Loaded;

    /// <summary>台服游戏正在运行（后端读到了进程）；游戏运行时不能写入客户端设置。</summary>
    public bool GameIsRunning => GameRunning == true;

    /// <summary>
    /// 开关的位置：偏好已开启且没有失效时为开。已失效的偏好按关处理，重新启用即把它写成新的有效设置；
    /// 偏好未读到时按状态判断。
    /// </summary>
    public bool Enabled => State != ConnectorState.Expired
        && (PreferenceEnabled ?? State is ConnectorState.Connected or ConnectorState.GameNotRunning);

    /// <summary>后端允许启用，且游戏没有运行（游戏运行时拒绝写入）。</summary>
    public bool CanEnableNow => IsLoaded && CanEnable && !GameIsRunning;

    public bool CanDisableNow => IsLoaded && CanDisable && !GameIsRunning;

    public bool CanRestoreNow => IsLoaded && CanRestore && !GameIsRunning;

    /// <summary>开关当前会执行的操作（开则关闭，关则启用）能否进行。</summary>
    public bool CanToggle => Enabled ? CanDisableNow : CanEnableNow;
}

/// <summary>游戏连接器的界面文字：胶囊约 6 字，状态说明约 16 字，提示约 20 字；原因码经共用映射换成中文，不出现英文代码。</summary>
public static class ConnectorText
{
    private static string Pick(string simplified, string traditional) => UiText.Pick(simplified, traditional);

    /// <summary>侧栏状态胶囊的文字；桌面没连上后端、没读到快照或状态未收录时为“未知”。</summary>
    public static string Status(GameConnectorView view) => !view.IsLoaded ? Pick("未知", "未知") : view.State switch
    {
        ConnectorState.Disabled => Pick("未启用", "未啟用"),
        ConnectorState.Connected => Pick("已连接", "已連接"),
        ConnectorState.Expired => Pick("已失效", "已失效"),
        ConnectorState.GameNotRunning => Pick("未连接游戏", "未連接遊戲"),
        ConnectorState.Unavailable => Pick("不可用", "不可用"),
        ConnectorState.Checking when view.Reason == "gameCli.gameStarting" => Pick("游戏启动中", "遊戲啟動中"),
        ConnectorState.Checking => Pick("检测中", "檢測中"),
        _ => Pick("未知", "未知")
    };

    /// <summary>卡片里的一句状态说明。</summary>
    public static string Line(GameConnectorView view) => view.Read switch
    {
        ConnectorRead.Offline => Pick("未连接后端", "未連接後端"),
        ConnectorRead.Unsupported => Pick("后端没有提供游戏连接器", "後端沒有提供遊戲連接器"),
        ConnectorRead.NotRead => Pick("未读取连接器状态", "未讀取連接器狀態"),
        _ => view.State switch
        {
            ConnectorState.Disabled => Pick("游戏连接器未启用", "遊戲連接器未啟用"),
            ConnectorState.Connected => Pick("已连接游戏", "已連接遊戲"),
            ConnectorState.Expired => Pick("连接器已失效", "連接器已失效"),
            ConnectorState.GameNotRunning => Pick("已启用，游戏未运行", "已啟用，遊戲未執行"),
            ConnectorState.Checking when view.Reason == "gameCli.gameStarting" => Pick("游戏启动中，进入游戏后连接", "遊戲啟動中，進入遊戲後連線"),
            ConnectorState.Checking => Pick("正在检测连接器", "正在檢測連接器"),
            ConnectorState.Unavailable => view.Reason.Length > 0 ? CodeText.Reason(view.Reason) : Pick("游戏连接器不可用", "遊戲連接器不可用"),
            _ => Pick("未读取连接器状态", "未讀取連接器狀態")
        }
    };

    /// <summary>卡片里状态说明下方的一句提示：下一步该做什么。</summary>
    public static string Hint(GameConnectorView view)
    {
        if (!view.IsLoaded) return view.Read == ConnectorRead.Offline ? Pick("连接后端后才能设置", "連接後端後才能設定") : string.Empty;
        // 游戏运行时不能写入客户端设置：已失效时说明如何重新启用，其余状态说明修改前先退出游戏。
        if (view.GameIsRunning)
            return view.State == ConnectorState.Expired
                ? Pick("正常退出游戏后重新启用", "正常退出遊戲後重新啟用")
                : Pick("游戏运行中，修改前请先正常退出游戏", "遊戲執行中，修改前請先正常退出遊戲");
        // 命令行文件缺失或与已核对的版本不符时，无论连接器处于哪种状态，先说明这个文件的问题。
        if (view.State != ConnectorState.Connected)
        {
            if (view.CliState == GameConnectorCliFileState.Missing || view.Reason == "gameCli.cliMissing")
                return Pick("发行包与本机都没有该文件", "發行包與本機都沒有該檔案");
            if (view.CliState == GameConnectorCliFileState.HashMismatch || view.Reason == "gameCli.cliHashMismatch")
                return Pick("文件与已核对的版本不一致", "檔案與已核對的版本不一致");
        }
        return view.State switch
        {
            ConnectorState.Disabled => Pick("启用后需从 Nexon Launcher 重启台服", "啟用後需從 Nexon Launcher 重啟台服"),
            ConnectorState.GameNotRunning => Pick("从 Nexon Launcher 启动台服并进入角色", "從 Nexon Launcher 啟動台服並進入角色"),
            ConnectorState.Connected => Pick("游戏内演奏等功能已可用", "遊戲內演奏等功能已可用"),
            ConnectorState.Expired => Pick("重新启用后从启动器重启台服", "重新啟用後從啟動器重啟台服"),
            ConnectorState.Checking => Pick("稍候自动刷新", "稍候自動重新整理"),
            ConnectorState.Unavailable when view.Reason == "gameCli.directoryUnknown" =>
                Pick("请在下方指定台服游戏目录", "請在下方指定台服遊戲目錄"),
            _ => string.Empty
        };
    }

    /// <summary>命令行工具文件的一行：来源、核对结果与是否已放入台服目录。</summary>
    public static string Cli(GameConnectorView view)
    {
        if (!view.IsLoaded) return Pick("未读取", "未讀取");
        if (view.CliSource == GameConnectorCliSource.None && view.CliState is null or GameConnectorCliFileState.Missing)
            return Pick("未找到", "未找到");
        var source = view.CliSource switch
        {
            GameConnectorCliSource.Package => Pick("随发行包", "隨發行包"),
            GameConnectorCliSource.LocalKorean => Pick("本机韩服目录", "本機韓服目錄"),
            GameConnectorCliSource.None => Pick("未找到来源", "未找到來源"),
            _ => Pick("来源未知", "來源未知")
        };
        var state = view.CliState switch
        {
            GameConnectorCliFileState.Verified => Pick("已核对", "已核對"),
            GameConnectorCliFileState.HashMismatch => Pick("版本不符", "版本不符"),
            GameConnectorCliFileState.Missing => Pick("缺失", "缺失"),
            _ => Pick("核对结果未知", "核對結果未知")
        };
        var deployed = view.CliDeployedDiffers ? Pick(" · 台服目录里的文件不同", " · 台服目錄裡的檔案不同")
            : view.CliDeployed ? Pick(" · 已放入台服目录", " · 已放入台服目錄") : string.Empty;
        return $"{source} · {state}{deployed}";
    }

    /// <summary>命令行文件一行的悬停提示：版本号；没有时为空。</summary>
    public static string? CliTip(GameConnectorView view) => view.IsLoaded && view.CliVersion is { Length: > 0 } version
        ? Pick($"命令行工具版本 {version}", $"命令列工具版本 {version}") : null;

    /// <summary>游戏是否运行；没读到进程状态时为“未知”。</summary>
    public static string Game(GameConnectorView view) => !view.IsLoaded ? Pick("未读取", "未讀取") : view.GameRunning switch
    {
        true => Pick("运行中", "執行中"),
        false => Pick("未运行", "未執行"),
        null => Pick("未知", "未知")
    };

    /// <summary>台服游戏目录与它的来源；没找到时写明。</summary>
    public static string Directory(GameConnectorView view)
    {
        if (!view.IsLoaded) return Pick("未读取", "未讀取");
        if (view.GameDirectory is not { Length: > 0 } path) return Pick("未找到", "未找到");
        var source = view.DirectorySource switch
        {
            GameConnectorDirectorySource.Bound => Pick("来自绑定的游戏窗口", "來自綁定的遊戲視窗"),
            GameConnectorDirectorySource.Remembered => Pick("已记住", "已記住"),
            GameConnectorDirectorySource.Default => Pick("默认位置", "預設位置"),
            GameConnectorDirectorySource.Manual => Pick("手动指定", "手動指定"),
            _ => null
        };
        return source is null ? path : $"{path}（{source}）";
    }

    /// <summary>开关停用的原因；开关可用或还没读到状态时为空。游戏运行中优先说明先退出游戏。</summary>
    public static string? ToggleBlocked(GameConnectorView view)
    {
        if (!view.IsLoaded || view.CanToggle) return null;
        if (view.GameIsRunning) return Reason("gameCli.gameRunning");
        var code = view.Enabled ? view.DisableBlocked : view.EnableBlocked;
        return code is { Length: > 0 } ? Reason(code) : null;
    }

    /// <summary>“恢复原设置”停用的原因；可用或还没读到状态时为空。</summary>
    public static string? RestoreBlocked(GameConnectorView view)
    {
        if (!view.IsLoaded || view.CanRestoreNow) return null;
        if (view.GameIsRunning) return Reason("gameCli.gameRunning");
        return view.RestoreBlocked is { Length: > 0 } code ? Reason(code) : null;
    }

    /// <summary>原因码或错误码对应的短句；没有文案的码给出中文概括。</summary>
    public static string Reason(string code) => CodeText.Reason(code);

    /// <summary>写操作被受理后的一句说明；启用成功后提示从启动器重新启动台服。</summary>
    public static string Done(string action) => action switch
    {
        "enable" => Pick("已启用，请从 Nexon Launcher 重新启动台服并进入角色", "已啟用，請從 Nexon Launcher 重新啟動台服並進入角色"),
        "disable" => Pick("已关闭游戏连接器", "已關閉遊戲連接器"),
        "restore" => Pick("已恢复原设置", "已還原原設定"),
        "set-directory" => Pick("已更新游戏目录", "已更新遊戲目錄"),
        _ => Pick("已刷新", "已重新整理")
    };

    /// <summary>能力不可用说明后面指向状态胶囊的一句。</summary>
    public static string Pointer => Pick("（点左上角“游戏连接器”查看）", "（點左上角「遊戲連接器」查看）");
}

/// <summary>乐谱能否经连接器在游戏里演奏：可演奏、玩家自制（不在游戏提供的乐谱列表里）、未知。</summary>
public enum ScoreSupportState { Supported, UserComposed, Unknown }

/// <summary>某张乐谱的可演奏性与后端给出的原因；连接器未连接或乐谱列表尚未取得时为未知，不因此停用按钮。</summary>
public sealed record ScoreSupportView(ScoreSupportState State, string? Reason = null)
{
    public static ScoreSupportView Unknown { get; } = new(ScoreSupportState.Unknown);

    /// <summary>玩家自制乐谱的“开始演奏”按钮停用时的悬浮提示。</summary>
    public static string UserComposedTip => UiText.Pick("台服连接器不支持播放玩家自制乐谱", "台服連接器不支援播放玩家自製樂譜");
}
