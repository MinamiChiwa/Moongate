using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Diagnostics;

/// <summary>一个探针的显示块：标题是探针标识，状态与原因，下面是计数与短码合并的键值表。</summary>
public sealed class ProbeBlock
{
    public ProbeBlock(ProbeView probe)
    {
        Id = probe.Id;
        Module = probe.Module;
        State = probe.State;
        Unavailable = probe.State == "unavailable";
        ReasonLine = probe.Reason is { Length: > 0 } reason ? DiagnosticsText.Reason(reason) : string.Empty;
        Rows = DiagnosticsText.Rows(probe);
        Notice = DiagnosticsText.EntryNotice(probe.TruncatedEntries, probe.RejectedEntries);
    }

    public string Id { get; }
    public string Module { get; }
    public string State { get; }
    public bool Unavailable { get; }
    public string ReasonLine { get; }
    public bool HasReason => ReasonLine.Length > 0;
    public IReadOnlyList<KeyValue> Rows { get; }
    public bool NoRows => Rows.Count == 0;
    public string Notice { get; }
    public bool HasNotice => Notice.Length > 0;
}

/// <summary>
/// 运行状态：读取 <c>GET /diagnostics/runtime</c>，只读、可轮询。探针与键随后端版本增加，按通用键值表显示；
/// 某个探针 unavailable 只标该探针，其余照常。读取失败时保留上一次的结果并说明。
/// </summary>
public sealed class DiagnosticsRuntimeSection : ObservableObject
{
    private readonly IDesktopBackend backend;
    private RuntimeView? view;
    private string error = string.Empty;
    private int generation;
    private int copyGeneration;
    private bool copied;
    private bool disposed;

    public DiagnosticsRuntimeSection(IDesktopBackend backend)
    {
        this.backend = backend;
        CopyAllCommand = new RelayCommand(_ => _ = CopyAllAsync());
        Blocks = [];
    }

    /// <summary>由页面提供的剪贴板写入；没有时“复制全部”不动作。</summary>
    public Func<string, Task>? ClipboardWriter { get; set; }

    public System.Windows.Input.ICommand CopyAllCommand { get; }
    public IReadOnlyList<ProbeBlock> Blocks { get; private set; }
    public bool HasData => view is not null;
    public bool NoData => view is null && error.Length == 0;
    public string Error => error;
    public bool HasError => error.Length > 0;
    public bool Copied => copied;

    /// <summary>采集时间与后端实例标识（backendEpoch 原样）。</summary>
    public string Header => view is null
        ? string.Empty
        : Pick($"采集时间 {DiagnosticsText.LocalTime(view.CapturedAt)}，backendEpoch {view.BackendEpoch}",
            $"採集時間 {DiagnosticsText.LocalTime(view.CapturedAt)}，backendEpoch {view.BackendEpoch}");

    /// <summary>快照里被省略的探针数；没有时为空。</summary>
    public string Notice
    {
        get
        {
            if (view is null) return string.Empty;
            var parts = new List<string>();
            if (view.TruncatedProbes > 0)
                parts.Add(Pick($"{view.TruncatedProbes} 个探针因数量上限被省略", $"{view.TruncatedProbes} 個探針因數量上限被省略"));
            if (view.RejectedProbes > 0)
                parts.Add(Pick($"{view.RejectedProbes} 个探针因标识无效或重复被省略", $"{view.RejectedProbes} 個探針因識別無效或重複被省略"));
            return string.Join("，", parts);
        }
    }

    public bool HasNotice => Notice.Length > 0;

    /// <summary>用于“复制全部”的纯文本；还没有读到快照时为空。</summary>
    public string SnapshotText => view is null ? string.Empty : DiagnosticsText.SnapshotText(view);

    public async Task RefreshAsync()
    {
        var token = ++generation;
        DiagnosticRuntimeReply reply;
        try { reply = await backend.ReadDiagnosticRuntimeAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reply = new(null, exception.Message);
        }
        if (disposed || token != generation) return;
        if (reply.Value is { } value)
        {
            view = value;
            error = string.Empty;
            Blocks = [.. value.Probes.Select(probe => new ProbeBlock(probe))];
        }
        else error = view is null
            ? DiagnosticsText.Error(reply.Message)
            : Pick($"{DiagnosticsText.Error(reply.Message)}，以下是上一次读取的结果", $"{DiagnosticsText.Error(reply.Message)}，以下是上一次讀取的結果");
        Raise(string.Empty);
    }

    /// <summary>把整份快照以纯文本放进剪贴板，并显示“已复制”几秒。</summary>
    public async Task CopyAllAsync()
    {
        if (view is null || ClipboardWriter is null) return;
        try { await ClipboardWriter(SnapshotText); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = Pick("复制失败：剪贴板不可用", "複製失敗：剪貼簿不可用");
            Raise(string.Empty);
            return;
        }
        var token = ++copyGeneration;
        copied = true;
        Raise(nameof(Copied));
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (disposed || token != copyGeneration) return;
            copied = false;
            Raise(nameof(Copied));
        }), TaskScheduler.Default);
    }

    /// <summary>界面语言切换后重排由后端状态拼出的文字。</summary>
    public void RebuildText()
    {
        if (view is { } value) Blocks = [.. value.Probes.Select(probe => new ProbeBlock(probe))];
        Raise(string.Empty);
    }

    public void Dispose() => disposed = true;
}
