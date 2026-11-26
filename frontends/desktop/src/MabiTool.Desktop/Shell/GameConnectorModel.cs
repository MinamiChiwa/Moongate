using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Shell;

/// <summary>
/// 游戏连接器的界面状态：侧栏状态胶囊、点开的弹出层与设置页卡片各持一个实例，都读后端同一份连接器状态，所以内容一致。
/// 所有判定取自后端返回的状态（<see cref="GameConnectorView"/>）：开关能否点、恢复能否点、被拒原因；这里只负责把它们排成文字与命令。
/// 首次启用（后端 <c>riskAcknowledged</c> 为假）先弹出风险确认窗口，确认后才发送带确认的 <c>enable</c>，取消不发送任何请求；
/// 后端记住确认后不再弹出。风险确认窗口只在主窗口里有一个：<paramref name="ownsRiskLayer"/> 为真的实例（外壳）自己显示它，
/// 其他实例（设置页卡片）经 <see cref="ShellRequests.ShowConnectorRisk"/> 请外壳显示。
/// </summary>
public sealed class GameConnectorModel : ObservableObject, IDisposable
{
    private readonly IDesktopBackend backend;
    private readonly bool ownsRiskLayer;
    private GameConnectorView view;
    private bool isOpen;
    private bool riskOpen;
    private bool busy;
    private bool editingDirectory;
    private string directoryInput = string.Empty;
    private string message = string.Empty;
    private bool messageFailed;
    private ConnectorState messageState;

    public GameConnectorModel(IDesktopBackend backend, bool ownsRiskLayer)
    {
        this.backend = backend;
        this.ownsRiskLayer = ownsRiskLayer;
        view = backend.GetGameConnector();
        ToggleLayerCommand = new RelayCommand(_ => IsOpen = !IsOpen);
        CloseCommand = new RelayCommand(_ => Close());
        EnableCommand = new RelayCommand(_ => RequestEnable());
        DisableCommand = new RelayCommand(_ => RequestDisable());
        RestoreCommand = new RelayCommand(_ => RequestRestore());
        ConfirmRiskCommand = new RelayCommand(_ => ConfirmRisk());
        CancelRiskCommand = new RelayCommand(_ => CancelRisk());
        ChangeDirectoryCommand = new RelayCommand(_ => BeginDirectoryEdit());
        ApplyDirectoryCommand = new RelayCommand(_ => _ = ApplyDirectoryAsync());
        CancelDirectoryCommand = new RelayCommand(_ => EditingDirectory = false);
        backend.Changed += OnBackendChanged;
        UiText.LanguageChanged += OnLanguageChanged;
        if (ownsRiskLayer) ShellRequests.ConnectorRiskRequested += OnRiskRequested;
    }

    public ICommand ToggleLayerCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ConfirmRiskCommand { get; }
    public ICommand CancelRiskCommand { get; }
    public ICommand ChangeDirectoryCommand { get; }
    public ICommand ApplyDirectoryCommand { get; }
    public ICommand CancelDirectoryCommand { get; }

    /// <summary>当前连接器状态（来自后端的最近一次读取）。</summary>
    public GameConnectorView View => view;

    // ---- 状态胶囊 ----

    /// <summary>胶囊文字：不超过约 6 个字，不出现英文代码。</summary>
    public string ChipText => ConnectorText.Status(view);

    /// <summary>胶囊的悬停提示与读屏名称：状态说明与提示。</summary>
    public string ChipTip => UiText.Pick("游戏连接器：", "遊戲連接器：") + ConnectorText.Line(view)
        + (ConnectorText.Hint(view) is { Length: > 0 } hint ? UiText.Pick("，", "，") + hint : string.Empty);

    /// <summary>胶囊前的圆点：已连接为绿色；已失效、不可用为警示色；其余为灰色。</summary>
    public bool DotConnected => view is { IsLoaded: true, State: ConnectorState.Connected };
    public bool DotWarning => view is { IsLoaded: true, State: ConnectorState.Expired or ConnectorState.Unavailable };
    public bool DotMuted => !DotConnected && !DotWarning;

    // ---- 卡片内容（弹出层与设置页相同） ----

    public string StatusLine => ConnectorText.Line(view);
    public string Hint => ConnectorText.Hint(view);
    public bool HasHint => Hint.Length > 0;
    public string CliLine => ConnectorText.Cli(view);
    public string? CliTip => ConnectorText.CliTip(view);
    public string GameLine => ConnectorText.Game(view);
    public bool GameRunning => view.GameIsRunning;
    public string DirectoryLine => ConnectorText.Directory(view);
    public string? DirectoryTip => view.GameDirectory;

    /// <summary>开关的位置：偏好已开启且没有失效为开。</summary>
    public bool Enabled => view.Enabled;

    /// <summary>开关（开则关闭、关则启用）此刻能否操作：取自后端的写操作许可，游戏运行中、请求进行中、未读取时都不能。</summary>
    public bool CanToggle => !busy && view.CanToggle;

    /// <summary>开关停用的原因；可用时为空。</summary>
    public string ToggleBlocked => ConnectorText.ToggleBlocked(view) ?? string.Empty;
    public bool HasToggleBlocked => ToggleBlocked.Length > 0;

    public bool CanRestore => !busy && view.CanRestoreNow;
    public string RestoreBlocked => ConnectorText.RestoreBlocked(view) ?? string.Empty;
    public bool HasRestoreBlocked => RestoreBlocked.Length > 0;

    /// <summary>最近一次写操作的结果（成功时提示下一步，失败时写明原因）；状态改变后清除。</summary>
    public string Message => message;
    public bool HasMessage => message.Length > 0;
    public bool MessageFailed => messageFailed;
    public bool MessageSucceeded => !messageFailed;

    /// <summary>台服游戏目录的修改入口：已连上后端且读到了状态时可用。</summary>
    public bool CanEditDirectory => !busy && view.IsLoaded;

    public bool EditingDirectory
    {
        get => editingDirectory;
        private set { if (Set(ref editingDirectory, value)) Raise(nameof(NotEditingDirectory)); }
    }

    public bool NotEditingDirectory => !editingDirectory;

    public string DirectoryInput
    {
        get => directoryInput;
        set { if (Set(ref directoryInput, value)) Raise(nameof(CanApplyDirectory)); }
    }

    public bool CanApplyDirectory => CanEditDirectory && directoryInput.Trim().Length > 0;

    // ---- 弹出层 ----

    /// <summary>状态胶囊点开的弹出层是否显示。</summary>
    public bool IsOpen
    {
        get => isOpen;
        set => Set(ref isOpen, value);
    }

    public void Close() => IsOpen = false;

    /// <summary>首次启用的风险确认窗口是否显示。</summary>
    public bool RiskOpen
    {
        get => riskOpen;
        private set => Set(ref riskOpen, value);
    }

    // ---- 操作 ----

    private void RequestEnable()
    {
        if (busy || view.Enabled || !view.CanEnableNow) return;
        // 后端还没有记下风险确认：先弹出确认窗口，确认后才发送。
        if (!view.RiskAcknowledged)
        {
            Action enable = () => _ = RunAsync("enable", true);
            if (ownsRiskLayer) ShowRisk(enable);
            else ShellRequests.ShowConnectorRisk(enable);
            return;
        }
        _ = RunAsync("enable", true);
    }

    private void RequestDisable()
    {
        if (busy || !view.Enabled || !view.CanDisableNow) return;
        _ = RunAsync("disable", false);
    }

    private void RequestRestore()
    {
        if (busy || !view.CanRestoreNow) return;
        _ = RunAsync("restore", false);
    }

    // 显示风险确认窗口；用户确认时执行 confirmed（请求方发送启用请求，结果显示在请求方的卡片里）。
    private void ShowRisk(Action confirmed)
    {
        riskConfirmed = confirmed;
        RiskOpen = true;
    }

    private Action? riskConfirmed;

    /// <summary>用户在风险确认窗口里点“我已了解，启用”：由请求方发送带确认的启用请求。</summary>
    public void ConfirmRisk()
    {
        if (!riskOpen) return;
        var confirmed = riskConfirmed;
        riskConfirmed = null;
        RiskOpen = false;
        confirmed?.Invoke();
    }

    /// <summary>用户取消或按 Esc：关闭窗口，不发送任何请求，开关保持关闭。</summary>
    public void CancelRisk()
    {
        riskConfirmed = null;
        RiskOpen = false;
    }

    private void BeginDirectoryEdit()
    {
        if (!CanEditDirectory) return;
        DirectoryInput = view.GameDirectory ?? string.Empty;
        EditingDirectory = true;
    }

    private async Task ApplyDirectoryAsync()
    {
        if (!CanApplyDirectory) return;
        var ok = await RunAsync("set-directory", false, directoryInput.Trim());
        if (ok) EditingDirectory = false;
    }

    // 一次只发一个写请求；回执写进提示行，状态以后端当前读到的为准。
    private async Task<bool> RunAsync(string action, bool riskAcknowledged, string? directory = null)
    {
        if (busy) return false;
        busy = true;
        RaiseAll();
        BackendReply reply;
        try { reply = await backend.CommandGameConnectorAsync(action, riskAcknowledged, directory, CancellationToken.None); }
        finally { busy = false; }
        view = backend.GetGameConnector();
        message = reply.Message;
        messageFailed = !reply.Ok;
        messageState = view.State;
        RaiseAll();
        return reply.Ok;
    }

    // ---- 后端状态 ----

    /// <summary>重新读取后端的连接器状态；状态与上次相同时不通知界面。后端通知时自动调用。</summary>
    public void Refresh()
    {
        var next = backend.GetGameConnector();
        if (Equals(next, view)) return;
        view = next;
        // 状态变了，之前的结果提示（例如“已启用，请重新启动台服”）已经过时。
        if (message.Length > 0 && next.State != messageState) message = string.Empty;
        RaiseAll();
    }

    private void OnBackendChanged(object? sender, EventArgs e) => Refresh();

    private void OnLanguageChanged(object? sender, EventArgs e) => RaiseAll();

    // 设置页卡片请求显示风险确认窗口：后端还没有记下确认时才显示。
    private void OnRiskRequested(Action confirmed)
    {
        if (!backend.GetGameConnector().RiskAcknowledged) ShowRisk(confirmed);
    }

    private void RaiseAll()
    {
        Raise(nameof(View));
        Raise(nameof(ChipText));
        Raise(nameof(ChipTip));
        Raise(nameof(DotConnected));
        Raise(nameof(DotWarning));
        Raise(nameof(DotMuted));
        Raise(nameof(StatusLine));
        Raise(nameof(Hint));
        Raise(nameof(HasHint));
        Raise(nameof(CliLine));
        Raise(nameof(CliTip));
        Raise(nameof(GameLine));
        Raise(nameof(GameRunning));
        Raise(nameof(DirectoryLine));
        Raise(nameof(DirectoryTip));
        Raise(nameof(Enabled));
        Raise(nameof(CanToggle));
        Raise(nameof(ToggleBlocked));
        Raise(nameof(HasToggleBlocked));
        Raise(nameof(CanRestore));
        Raise(nameof(RestoreBlocked));
        Raise(nameof(HasRestoreBlocked));
        Raise(nameof(Message));
        Raise(nameof(HasMessage));
        Raise(nameof(MessageFailed));
        Raise(nameof(MessageSucceeded));
        Raise(nameof(CanEditDirectory));
        Raise(nameof(CanApplyDirectory));
    }

    public void Dispose()
    {
        backend.Changed -= OnBackendChanged;
        UiText.LanguageChanged -= OnLanguageChanged;
        if (ownsRiskLayer) ShellRequests.ConnectorRiskRequested -= OnRiskRequested;
    }
}
