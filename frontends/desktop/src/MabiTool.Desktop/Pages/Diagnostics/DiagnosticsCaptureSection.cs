using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Diagnostics;

/// <summary>
/// 调试采集（需要 diagnostics.debug）：开始表单、进行中的计数与剩余时间、实时记录、结束原因。
/// 同一时刻只允许一个采集；重复开启后端返回 409 diagnostics.captureActive。实时记录最多保留最近 500 条，
/// 断线重连后用查询接口按会话补齐，按序号去重。
/// </summary>
public sealed class DiagnosticsCaptureSection : ObservableObject
{
    public const int MaxLive = 500;

    private readonly IDesktopBackend backend;
    private readonly Func<DateTimeOffset> clock;
    private readonly HashSet<string> liveKeys = [];
    private DiagnosticCaptureView? capture;
    private string level = "debug";
    private bool includeDetail;
    private string duration = "60";
    private string message = string.Empty;
    private bool busy;
    private bool forbidden;
    private bool disposed;
    private int backfillGeneration;
    private int refreshGeneration;
    private DateTimeOffset lastRefresh = DateTimeOffset.MinValue;

    public DiagnosticsCaptureSection(IDesktopBackend backend, Func<DateTimeOffset>? clock = null)
    {
        this.backend = backend;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        ModuleChoices = [.. DiagnosticsText.Modules.Select(id => new ChoiceChip(id, selected: true, toggle: Toggle))];
        ToggleModuleCommand = new RelayCommand(id => Toggle(id as string ?? string.Empty));
        SetLevelCommand = new RelayCommand(value =>
        {
            if (value is not ("debug" or "trace") || !Set(ref level, (string)value, nameof(Level))) return;
            Raise(nameof(IsDebug));
            Raise(nameof(IsTrace));
        });
        ToggleDetailCommand = new RelayCommand(_ =>
        {
            includeDetail = !includeDetail;
            Raise(nameof(IncludeDetail));
        });
        StartCommand = new RelayCommand(_ => _ = StartAsync(), _ => CanStart);
        StopCommand = new RelayCommand(_ => _ = StopAsync(), _ => Active && !busy);
        backend.DiagnosticRecordReceived += OnPushed;
        backend.DiagnosticStreamRestored += OnRestored;
    }

    public IReadOnlyList<ChoiceChip> ModuleChoices { get; }
    public ObservableCollection<RecordRow> LiveRows { get; } = [];
    public ICommand ToggleModuleCommand { get; }
    public ICommand SetLevelCommand { get; }
    public ICommand ToggleDetailCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    /// <summary>后端以 403 拒绝时置位，页面隐藏整块（权限信息本身取自握手）。</summary>
    public bool Forbidden => forbidden;

    public DiagnosticCaptureView? Current => capture;
    public bool Active => capture is { Active: true };
    public bool HasCapture => capture is not null;
    public bool ShowForm => !Active;
    public bool HasLive => LiveRows.Count > 0;
    public bool Busy => busy;

    public string Level => level;
    public string DebugLabel => DiagnosticsText.Label("debug");
    public string TraceLabel => DiagnosticsText.Label("trace");
    public bool IsDebug => level == "debug";
    public bool IsTrace => level == "trace";
    public bool IncludeDetail => includeDetail;

    public string Duration
    {
        get => duration;
        set
        {
            if (!Set(ref duration, value)) return;
            RaiseForm();
        }
    }

    public int? DurationSeconds =>
        int.TryParse(duration.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 600
            ? seconds : null;

    public bool DurationInvalid => DurationSeconds is null;
    public IReadOnlyList<string> SelectedModules => [.. ModuleChoices.Where(item => item.Selected).Select(item => item.Id)];
    public bool NoModuleSelected => SelectedModules.Count == 0;
    public bool CanStart => !busy && !Active && DurationSeconds is not null && !NoModuleSelected;

    public string Message => message;
    public bool HasMessage => message.Length > 0;

    public string StateText => capture switch
    {
        null => string.Empty,
        { Active: true } => Pick("进行中", "進行中"),
        { State: "interrupted" } c => Pick($"已中断，原因：{DiagnosticsText.EndReason(c.EndReason)}", $"已中斷，原因：{DiagnosticsText.EndReason(c.EndReason)}"),
        var c => Pick($"已结束，原因：{DiagnosticsText.EndReason(c.EndReason)}", $"已結束，原因：{DiagnosticsText.EndReason(c.EndReason)}")
    };

    public string RemainingText => capture is { Active: true } c
        ? Pick($"剩余 {DiagnosticsText.Remaining(c.ExpiresAt, clock())}", $"剩餘 {DiagnosticsText.Remaining(c.ExpiresAt, clock())}")
        : string.Empty;

    /// <summary>采集的计数与设置，按键值表显示。</summary>
    public IReadOnlyList<KeyValue> StatusRows
    {
        get
        {
            if (capture is not { } c) return [];
            var yes = Pick("是", "是");
            var no = Pick("否", "否");
            var rows = new List<KeyValue>
            {
                new(Pick("模块", "模組"), string.Join(", ", c.Modules)),
                new(Pick("级别", "級別"), c.Level),
                new(Pick("包含文字详情", "包含文字詳情"), c.IncludeDetail ? yes : no),
                new(Pick("开始时间", "開始時間"), DiagnosticsText.LocalTime(c.StartedAt)),
                new(Pick("已记录", "已記錄"), c.Records.ToString(CultureInfo.InvariantCulture)),
                new(Pick("丢弃（缓冲覆盖或文件预算）", "丟棄（緩衝覆蓋或檔案預算）"), c.DroppedRecords.ToString(CultureInfo.InvariantCulture)),
                new(Pick("实时推送丢弃（超每秒 50 条）", "即時推送丟棄（超每秒 50 筆）"), c.PushDropped.ToString(CultureInfo.InvariantCulture))
            };
            if (c.IncludeDetail)
            {
                var bytes = DiagnosticsText.Value("detailBytes", c.DetailBytes.ToString(CultureInfo.InvariantCulture));
                rows.Add(new(Pick("文字详情", "文字詳情"), c.DetailTruncated > 0
                    ? Pick($"{bytes}，{c.DetailTruncated} 条被截断", $"{bytes}，{c.DetailTruncated} 筆被截斷")
                    : Pick($"{bytes}，未截断", $"{bytes}，未截斷")));
            }
            if (c.EndedAt is { } ended) rows.Add(new(Pick("结束时间", "結束時間"), DiagnosticsText.LocalTime(ended)));
            return rows;
        }
    }

    public bool DetailTruncated => capture is { DetailTruncated: > 0 };

    /// <summary>读取最近一次或进行中的采集；从未采集时为空。</summary>
    public async Task RefreshAsync()
    {
        var token = ++refreshGeneration;
        lastRefresh = clock();
        DiagnosticCaptureReply reply;
        try { reply = await backend.GetDiagnosticCaptureAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reply = new(false, Localization.CodeText.Error(exception), null);
        }
        if (disposed || token != refreshGeneration) return;
        if (reply.Ok) Adopt(reply.Capture);
        else if (IsForbidden(reply.Message)) { forbidden = true; Raise(string.Empty); }
        else Fail(reply.Message);
    }

    public async Task StartAsync()
    {
        if (!CanStart || DurationSeconds is not { } seconds) return;
        busy = true;
        message = string.Empty;
        RaiseForm();
        DiagnosticCaptureReply reply;
        try
        {
            reply = await backend.StartDiagnosticCaptureAsync(new(SelectedModules, level, seconds, includeDetail), CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reply = new(false, Localization.CodeText.Error(exception), null);
        }
        if (disposed) return;
        busy = false;
        if (reply is { Ok: true, Capture: { } started }) Adopt(started);
        else if (reply.Message == "diagnostics.captureActive")
        {
            message = DiagnosticsText.Error(reply.Message);
            RaiseForm();
            await RefreshAsync();
        }
        else if (IsForbidden(reply.Message)) { forbidden = true; Raise(string.Empty); }
        else Fail(reply.Message);
    }

    public async Task StopAsync()
    {
        if (capture is not { Active: true } current || busy) return;
        busy = true;
        RaiseForm();
        DiagnosticCaptureReply reply;
        try { reply = await backend.StopDiagnosticCaptureAsync(current.Id, CancellationToken.None); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reply = new(false, Localization.CodeText.Error(exception), null);
        }
        if (disposed) return;
        busy = false;
        if (reply is { Ok: true, Capture: { } stopped }) Adopt(stopped);
        else if (IsForbidden(reply.Message)) { forbidden = true; Raise(string.Empty); }
        else Fail(reply.Message);
    }

    /// <summary>每秒调用一次：更新剩余时间；进行中每两秒读一次最新计数，到期后每秒读取直到后端确认结束。</summary>
    public void Tick()
    {
        if (!Active || busy) return;
        Raise(nameof(RemainingText));
        var overdue = capture!.ExpiresAt <= clock();
        if (overdue || clock() - lastRefresh >= TimeSpan.FromSeconds(2)) _ = RefreshAsync();
    }

    private void Toggle(string id)
    {
        if (ModuleChoices.FirstOrDefault(item => item.Id == id) is not { } chip) return;
        chip.Selected = !chip.Selected;
        RaiseForm();
    }

    private void Fail(string code)
    {
        message = DiagnosticsText.Error(code);
        RaiseForm();
    }

    private static bool IsForbidden(string code) => code is "permission.denied" or "http.403";

    private void Adopt(DiagnosticCaptureView? view)
    {
        var previous = capture;
        capture = view;
        if (view is { Active: true } && previous?.Id != view.Id)
        {
            ClearLive();
            _ = BackfillAsync(view);
        }
        RaiseForm();
    }

    private void RaiseForm()
    {
        Raise(string.Empty);
        (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPushed(object? sender, DiagnosticPushedRecord pushed)
    {
        if (disposed) return;
        if (capture is { Active: true } current && current.Id == pushed.CaptureId)
        {
            AddLive([new RecordRow(pushed.Record)]);
            return;
        }
        // 不认识的采集编号：可能是别处开始的采集，重新读取当前采集，采用后会补齐记录。
        if (capture?.Id != pushed.CaptureId && clock() - lastRefresh >= TimeSpan.FromSeconds(1)) _ = RefreshAsync();
    }

    private void OnRestored(object? sender, EventArgs e)
    {
        if (disposed) return;
        if (capture is { Active: true } current) _ = BackfillAsync(current);
        else if (capture is null) _ = RefreshAsync();
    }

    private void ClearLive()
    {
        LiveRows.Clear();
        liveKeys.Clear();
        Raise(nameof(HasLive));
    }

    private void AddLive(IReadOnlyList<RecordRow> rows)
    {
        foreach (var row in rows)
        {
            if (!liveKeys.Add(row.Key)) continue;
            LiveRows.Add(row);
        }
        while (LiveRows.Count > MaxLive)
        {
            liveKeys.Remove(LiveRows[0].Key);
            LiveRows.RemoveAt(0);
        }
        Raise(nameof(HasLive));
    }

    // 补齐：按会话查询，从已有的最后一条往后读，按序号去重；合并后按发生时间与序号排序，只保留最近 500 条。
    private async Task BackfillAsync(DiagnosticCaptureView view)
    {
        var token = ++backfillGeneration;
        DateTimeOffset? from = LiveRows.Count > 0 ? LiveRows.Max(row => row.Record.OccurredAt) - TimeSpan.FromSeconds(1) : null;
        var collected = new List<RecordRow>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            DiagnosticRecordsReply reply;
            try { reply = await backend.QueryDiagnosticRecordsAsync(new(view.SessionId, [], [], cursor, 500, from), CancellationToken.None); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return; }
            if (disposed || token != backfillGeneration || capture?.Id != view.Id || !reply.Ok) return;
            collected.AddRange(reply.Items.Select(item => new RecordRow(item)));
            cursor = reply.NextCursor;
            if (cursor is null) break;
        }
        var merged = LiveRows.Concat(collected.Where(row => !liveKeys.Contains(row.Key)))
            .GroupBy(row => row.Key).Select(group => group.First())
            .OrderBy(row => row.Record.OccurredAt).ThenBy(row => row.Sequence ?? 0).TakeLast(MaxLive).ToList();
        LiveRows.Clear();
        liveKeys.Clear();
        foreach (var row in merged)
        {
            liveKeys.Add(row.Key);
            LiveRows.Add(row);
        }
        Raise(nameof(HasLive));
    }

    public void RebuildText()
    {
        foreach (var row in LiveRows) row.RefreshText();
        Raise(string.Empty);
    }

    public void Dispose()
    {
        disposed = true;
        backend.DiagnosticRecordReceived -= OnPushed;
        backend.DiagnosticStreamRestored -= OnRestored;
    }
}
