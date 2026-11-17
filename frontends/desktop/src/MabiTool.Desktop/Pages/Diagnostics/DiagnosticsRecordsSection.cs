using System.Globalization;
using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Diagnostics;

/// <summary>可多选的筛选项：标识是后端的稳定码，按钮上显示中文名（模块与级别经共用映射）。</summary>
public sealed class ChoiceChip(string id, bool selected = false, Action<string>? toggle = null) : ObservableObject
{
    private bool selected = selected;

    public string Id => id;
    public string Label => DiagnosticsText.Label(id);
    public ICommand ToggleCommand { get; } = new RelayCommand(_ => toggle?.Invoke(id));

    public bool Selected
    {
        get => selected;
        set => Set(ref selected, value);
    }
}

/// <summary>时间线上的一行：时间（本地）、模块、级别、事件码、component 与 fields 的键值；有文字详情时可展开。</summary>
public sealed class RecordRow : ObservableObject
{
    private bool expanded;

    public RecordRow(DiagnosticRecordView record)
    {
        Record = record;
        Time = DiagnosticsText.LocalClock(record.OccurredAt);
        EventText = record.Event ?? record.Code ?? "-";
        Component = record.Component ?? string.Empty;
        FieldsText = string.Join("  ", record.Fields.Select(item => $"{item.Label}={item.Value}"));
        Warn = record.Level is "warning" or "error";
        ToggleCommand = new RelayCommand(_ =>
        {
            expanded = !expanded;
            Raise(nameof(Expanded));
            Raise(nameof(ToggleText));
        });
    }

    public DiagnosticRecordView Record { get; }
    public string Time { get; }
    public string Level => Record.Level;
    public string Module => Record.Module;
    public string EventText { get; }
    public string Component { get; }
    public bool HasComponent => Component.Length > 0;
    public string FieldsText { get; }
    public bool HasFields => FieldsText.Length > 0;
    public bool Warn { get; }
    public string? Detail => Record.Detail;
    public bool HasDetail => Record.Detail is { Length: > 0 };
    public bool Expanded => expanded;
    public ICommand ToggleCommand { get; }
    public string ToggleText => expanded ? Pick("收起详情", "收起詳情") : Pick("展开详情", "展開詳情");

    /// <summary>用于去重与排序的键：优先用宿主会话内单调递增的序号。</summary>
    public string Key => Record.Sequence is { Length: > 0 } sequence
        ? $"{Record.SessionId}:{sequence}"
        : $"{Record.SessionId}:{Record.OccurredAt.UtcTicks}:{Record.Module}:{Record.Event}:{Record.Level}";

    public ulong? Sequence => ulong.TryParse(Record.Sequence, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    public void RefreshText() => Raise(nameof(ToggleText));
}

/// <summary>
/// 最近记录：<c>POST /diagnostics/records/query</c>，按模块与级别筛选，从新到旧（order: desc）每页 100 条，
/// “更多”带同一顺序与 nextCursor 继续往旧的读。只在打开页面、改筛选、按“更多”或“刷新”时读取；列表按返回顺序显示，最新的在最上面。
/// </summary>
public sealed class DiagnosticsRecordsSection : ObservableObject
{
    public const int PageSize = 100;

    private readonly IDesktopBackend backend;
    private IReadOnlyList<RecordRow> rows = [];
    private string? nextCursor;
    private string error = string.Empty;
    private bool loading;
    private int generation;
    private bool disposed;

    public DiagnosticsRecordsSection(IDesktopBackend backend)
    {
        this.backend = backend;
        ModuleChoices = [.. DiagnosticsText.Modules.Select(id => new ChoiceChip(id, toggle: Toggle))];
        LevelChoices = [.. DiagnosticsText.Levels.Select(id => new ChoiceChip(id, toggle: Toggle))];
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        MoreCommand = new RelayCommand(_ => _ = MoreAsync(), _ => HasMore && !loading);
    }

    public IReadOnlyList<ChoiceChip> ModuleChoices { get; }
    public IReadOnlyList<ChoiceChip> LevelChoices { get; }
    public ICommand RefreshCommand { get; }
    public ICommand MoreCommand { get; }

    public IReadOnlyList<RecordRow> Rows => rows;
    public bool HasMore => nextCursor is not null;
    public bool Loading => loading;
    public string Error => error;
    public bool HasError => error.Length > 0;
    public bool Empty => rows.Count == 0 && !loading && error.Length == 0;

    /// <summary>当前筛选对应的请求；列表内为“或”，一项都没选表示不限。</summary>
    public DiagnosticRecordFilter Filter(string? cursor) => new(null,
        [.. ModuleChoices.Where(item => item.Selected).Select(item => item.Id)],
        [.. LevelChoices.Where(item => item.Selected).Select(item => item.Id)], cursor, PageSize,
        Order: DiagnosticRecordFilter.Descending);

    /// <summary>从第一页重新读取；筛选变化后迟到的旧回复丢弃。</summary>
    public Task RefreshAsync() => LoadAsync(append: false);

    public Task MoreAsync() => nextCursor is null || loading ? Task.CompletedTask : LoadAsync(append: true);

    private void Toggle(string id)
    {
        if (ModuleChoices.Concat(LevelChoices).FirstOrDefault(item => item.Id == id) is not { } chip) return;
        chip.Selected = !chip.Selected;
        _ = RefreshAsync();
    }

    private async Task LoadAsync(bool append)
    {
        var token = ++generation;
        var cursor = append ? nextCursor : null;
        loading = true;
        Raise(string.Empty);
        (MoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
        DiagnosticRecordsReply reply;
        try { reply = await backend.QueryDiagnosticRecordsAsync(Filter(cursor), CancellationToken.None); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reply = new(false, exception.Message, [], null);
        }
        if (disposed || token != generation) return;
        loading = false;
        if (reply.Ok)
        {
            var added = reply.Items.Select(item => new RecordRow(item));
            rows = append ? [.. rows, .. added] : [.. added];
            nextCursor = reply.NextCursor;
            error = string.Empty;
        }
        else
        {
            error = DiagnosticsText.Error(reply.Message);
            if (!append)
            {
                rows = [];
                nextCursor = null;
            }
        }
        Raise(string.Empty);
        (MoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void RebuildText()
    {
        foreach (var row in rows) row.RefreshText();
    }

    public void Dispose() => disposed = true;
}
