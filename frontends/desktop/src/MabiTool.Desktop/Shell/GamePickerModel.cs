using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Shell;

/// <summary>选择游戏窗口弹出层里的一行：名称是系统原文，说明是区服、进程名与进程号。</summary>
public sealed record GameWindowRow(string Id, string Name, string Detail, bool Current, string AccessibleName);

/// <summary>
/// 侧栏绑定卡打开的选择游戏窗口弹出层。每次打开和刷新都向后端重新读取候选，迟到的旧结果丢弃；
/// 点一行发送一次绑定请求，成功后关闭，失败时保持打开（原因由后端显示在活动胶囊）。
/// </summary>
public sealed class GamePickerModel : ObservableObject
{
    private readonly IDesktopBackend backend;
    private IReadOnlyList<GameWindowRow> rows = [];
    private bool isOpen;
    private bool isLoading;
    private bool isSelecting;
    private string? problem;
    private int generation;

    public GamePickerModel(IDesktopBackend backend)
    {
        this.backend = backend;
        OpenCommand = new RelayCommand(_ => _ = OpenAsync());
        CloseCommand = new RelayCommand(_ => Close());
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        SelectCommand = new RelayCommand(id => { if (id is string candidate) _ = SelectAsync(candidate); });
    }

    public ICommand OpenCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SelectCommand { get; }

    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }
    public IReadOnlyList<GameWindowRow> Rows { get => rows; private set => Set(ref rows, value); }
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) RaiseState(); } }

    /// <summary>绑定请求进行中：各行与刷新暂时停用，避免同一意图发送两次。</summary>
    public bool IsSelecting { get => isSelecting; private set { if (Set(ref isSelecting, value)) RaiseState(); } }
    public bool CanAct => !isLoading && !isSelecting;
    public bool IsEmpty => !isLoading && problem is null && rows.Count == 0;
    public string Problem => problem ?? string.Empty;
    public bool HasProblem => problem is not null;

    public async Task OpenAsync()
    {
        if (!backend.GetGameBinding().Connected) return;
        IsOpen = true;
        await RefreshAsync();
    }

    public void Close()
    {
        generation++;
        IsOpen = false;
        IsLoading = false;
        Rows = [];
    }

    public async Task RefreshAsync()
    {
        if (!isOpen || isSelecting) return;
        var current = ++generation;
        problem = null;
        Rows = [];
        IsLoading = true;
        var reply = await backend.ListGameWindowsAsync(CancellationToken.None);
        if (current != generation || !isOpen) return;
        problem = reply.Ok ? null : reply.Message;
        Rows = reply.Windows.Select(ToRow).ToList();
        IsLoading = false;
        RaiseState();
    }

    public async Task SelectAsync(string candidateId)
    {
        if (!isOpen || !CanAct || rows.All(row => row.Id != candidateId)) return;
        IsSelecting = true;
        BackendReply reply;
        try { reply = await backend.SelectGameWindowAsync(candidateId, CancellationToken.None); }
        finally { IsSelecting = false; }
        if (reply.Ok) Close();
    }

    /// <summary>后端连接中断时关闭弹出层：入口随之停用，列表已不可信。</summary>
    public void OnBackendChanged()
    {
        if (isOpen && !backend.GetGameBinding().Connected) Close();
    }

    private static GameWindowRow ToRow(GameWindowChoice choice)
    {
        var region = choice.Region == "TW" ? UiText.Pick("洛奇 Mobile 台服", "洛奇 Mobile 台服") : choice.Region;
        var detail = UiText.Pick($"{region} · {choice.ProcessName} · 进程 {choice.ProcessId}",
            $"{region} · {choice.ProcessName} · 處理程序 {choice.ProcessId}");
        var name = choice.Current
            ? UiText.Pick($"{choice.Name}，当前绑定，{detail}", $"{choice.Name}，目前綁定，{detail}")
            : $"{choice.Name}，{detail}";
        return new(choice.Id, choice.Name, detail, choice.Current, name);
    }

    private void RaiseState()
    {
        Raise(nameof(CanAct));
        Raise(nameof(IsEmpty));
        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
    }
}
