using System.Windows.Input;
using Avalonia.Threading;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;

namespace MabiTool.Desktop.Pages.Diagnostics;

/// <summary>
/// 诊断页：导出排查包、调试采集、运行状态、最近记录与界面诊断五块，依次排列，各自可以折叠（状态存入前端偏好）。
/// 进入页面与按“刷新”时读取；页面可见时每 5 秒轮询运行状态，采集进行中每秒更新剩余时间。采集块与导出块连接后按握手时的权限显示，未连接时停用并在标题行写明原因。
/// </summary>
public sealed class DiagnosticsPageModel : ObservableObject, IDisposable, IPageLifecycle
{
    private readonly IDesktopBackend backend;
    private DiagnosticAccess access;
    private DispatcherTimer? timer;
    private int ticks;
    private bool active = true;
    private bool pendingRefresh;

    /// <summary>切走后保留页面（筛选、展开的详情与已读到的记录都留着）；轮询只在页面挂在窗口上时进行。</summary>
    public bool Cacheable => true;

    /// <summary>回到页面后重读运行状态，返回前页面外框显示“更新中”。</summary>
    public bool PendingRefresh
    {
        get => pendingRefresh;
        private set => Set(ref pendingRefresh, value);
    }

    public void Deactivate()
    {
        if (!active) return;
        active = false;
        StopPolling();
        backend.Changed -= OnBackendChanged;
    }

    public void Activate()
    {
        if (active) return;
        active = true;
        backend.Changed += OnBackendChanged;
        OnBackendChanged(this, EventArgs.Empty);
        _ = RefreshOnReturnAsync();
    }

    private async Task RefreshOnReturnAsync()
    {
        PendingRefresh = true;
        try { await Runtime.RefreshAsync(); }
        finally { PendingRefresh = false; }
    }

    public DiagnosticsPageModel(IDesktopBackend backend, Func<DateTimeOffset>? clock = null)
    {
        this.backend = backend;
        access = backend.GetDiagnosticAccess();
        Runtime = new(backend);
        Records = new(backend);
        Capture = new(backend, clock);
        Export = new(backend, Capture);
        Interface = new();
        Startup = new();
        RefreshCommand = new RelayCommand(_ => _ = RefreshAllAsync());
        backend.Changed += OnBackendChanged;
        UiText.LanguageChanged += OnLanguageChanged;
        _ = RefreshAllAsync();
    }

    public DiagnosticsRuntimeSection Runtime { get; }
    public DiagnosticsRecordsSection Records { get; }
    public DiagnosticsCaptureSection Capture { get; }
    public DiagnosticsExportSection Export { get; }
    public DiagnosticsInterfaceSection Interface { get; }
    public DiagnosticsStartupSection Startup { get; }
    public ICommand RefreshCommand { get; }
    public string InterfaceTitle => UiText.Pick("界面诊断", "介面診斷");
    public string UnmappedTitle => UiText.Pick("界面上还没有中文文案的标识（类别:原文）", "介面上還沒有中文文案的標識（類別:原文）");

    /// <summary>
    /// 调试采集入口：连接后仅在拥有 diagnostics.debug 且后端没有以 403 拒绝时显示；未连接时权限未知，照常显示并停用。
    /// </summary>
    public bool ShowCapture => !access.Connected || access.CanDebug && !Capture.Forbidden;

    /// <summary>导出入口：连接后仅在拥有 diagnostics.export 时显示；未连接时照常显示并停用。</summary>
    public bool ShowExport => !access.Connected || access.CanExport;
    public bool Connected => access.Connected;

    /// <summary>读取运行状态、记录第一页，有调试权限时再读当前采集。</summary>
    public Task RefreshAllAsync() => Task.WhenAll(Runtime.RefreshAsync(), Records.RefreshAsync(),
        access.CanDebug ? Capture.RefreshAsync() : Task.CompletedTask);

    /// <summary>页面显示时开始轮询；离开页面时停止。</summary>
    public void StartPolling()
    {
        if (timer is not null || !active) return;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            if (access.Connected && ShowCapture) Capture.Tick();
            if (ticks % 5 == 0) _ = Runtime.RefreshAsync();
        };
        timer.Start();
    }

    public void StopPolling()
    {
        timer?.Stop();
        timer = null;
        ticks = 0;
    }

    private void OnBackendChanged(object? sender, EventArgs e)
    {
        var next = backend.GetDiagnosticAccess();
        if (next == access) return;
        var reconnected = !access.Connected && next.Connected;
        access = next;
        Raise(string.Empty);
        if (reconnected) _ = RefreshAllAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Runtime.RebuildText();
        Records.RebuildText();
        Capture.RebuildText();
        Raise(string.Empty);
    }

    public void Dispose()
    {
        active = false;
        StopPolling();
        backend.Changed -= OnBackendChanged;
        UiText.LanguageChanged -= OnLanguageChanged;
        Runtime.Dispose();
        Records.Dispose();
        Capture.Dispose();
        Export.Dispose();
        Interface.Dispose();
    }
}
