using System.Globalization;
using System.Windows.Input;
using MabiTool.Contracts;
using MabiTool.Desktop.Backend;
using MabiTool.Desktop.Infrastructure;
using MabiTool.Desktop.Localization;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Life;

/// <summary>
/// “生活”页的专注钓鱼区（原地钓鱼，变更 life-focused-fishing）。能力、运行与结束事实来自 <see cref="IDesktopBackend.GetFishing"/>；
/// 时长用小时、分钟、秒三个框，全部留空表示直到停止，合计不足 60 秒或超过 24 小时在本地提示、不发请求。
/// 开始前由页面弹出确认框（<see cref="StartRequested"/>）；“收竿结束”离开钓鱼姿态后正常结束，“立即停止”取消并释放输入。
/// 与命令行动作的互斥由页面经 <c>startBlocked</c> 告知（命令行动作进行中、其他功能占用活动锁）；钓鱼进行中页面停用采集、加工、制作。
/// </summary>
public sealed class LifeFishingPanel : ObservableObject
{
    public const int MinimumSeconds = 60, MaximumSeconds = 24 * 3600;

    private readonly IDesktopBackend backend;
    private readonly Func<string?> startBlocked;
    private FishingView view;
    private long receivedAt = Environment.TickCount64;
    private string hours = string.Empty, minutes = string.Empty, seconds = string.Empty;
    private string durationError = string.Empty, message = string.Empty;
    private bool messageFailed, busy;

    public LifeFishingPanel(IDesktopBackend backend, Func<string?> startBlocked)
    {
        this.backend = backend;
        this.startBlocked = startBlocked;
        view = backend.GetFishing();
        StartCommand = new RelayCommand(_ => RequestStart());
        StopCommand = new RelayCommand(_ => _ = CommandAsync("stop"));
        CancelCommand = new RelayCommand(_ => _ = CommandAsync("cancel"));
    }

    /// <summary>用户点“开始”且时长合法：参数是目标秒数（空表示直到停止），由页面决定是否先弹确认框。</summary>
    public event Action<int?>? StartRequested;

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CancelCommand { get; }

    public FishingView View => view;

    /// <summary>当前有进行中的运行。</summary>
    public bool IsRunning => view.Running;

    /// <summary>后端钓鱼状态变化后重读；内容没有变化时不通知。返回是否变化。</summary>
    public bool Refresh()
    {
        var next = backend.GetFishing();
        if (Equals(next, view)) return false;
        view = next;
        receivedAt = Environment.TickCount64;
        Raise(string.Empty);
        return true;
    }

    /// <summary>每秒调用：运行中的已用时长按本机时钟推进，只改文字。</summary>
    public void Tick()
    {
        if (view.Running) { Raise(nameof(ElapsedText)); }
    }

    /// <summary>页面状态（命令行动作、活动锁占用、语言）变化后重算全部文字与可用性。</summary>
    public void Invalidate() => Raise(string.Empty);

    // ───────────────────────── 文字 ─────────────────────────

    public string Title => Pick("专注钓鱼", "專注釣魚");
    public string Conditions => Pick("需要：台服客户端、游戏客户区 1280×960、窗口未最小化，并已选择游戏窗口。", "需要：台服用戶端、遊戲客戶區 1280×960、視窗未最小化，並已選擇遊戲視窗。");
    public string Hint => Pick("请先将角色停在可钓鱼位置。", "請先將角色停在可釣魚位置。");
    public string DurationLabel => Pick("钓鱼时长", "釣魚時長");
    public string HoursUnit => Pick("小时", "小時");
    public string MinutesUnit => Pick("分钟", "分鐘");
    public string SecondsUnit => Pick("秒", "秒");
    public string DurationHint => Pick("三个框都留空表示直到你停止；时长范围 60 秒到 24 小时。", "三個框都留空表示直到你停止；時長範圍 60 秒到 24 小時。");
    public string StartText => Pick("开始钓鱼", "開始釣魚");
    public string StopText => Pick("收竿结束", "收竿結束");
    public string CancelText => Pick("立即停止", "立即停止");

    /// <summary>读取状态与能力原因：连接、读取与后端给出的不可用原因；可用时为空。</summary>
    public string CapabilityNote => view.Read switch
    {
        FishingRead.Offline => Pick("未连接后端", "未連接後端"),
        FishingRead.NotRead => Pick("未读取", "未讀取"),
        FishingRead.Unsupported => Pick("后端没有提供专注钓鱼", "後端沒有提供專注釣魚"),
        _ => view.Available ? string.Empty : LifeText.Reason(view.UnavailableReason)
    };

    public bool HasCapabilityNote => CapabilityNote.Length > 0;

    /// <summary>开始按钮停用的原因：能力不可用，或与命令行动作、其他功能互斥；可以开始时为空。运行中不显示。</summary>
    public string StartBlocked => view.Running ? string.Empty : CapabilityNote.Length > 0 ? CapabilityNote : startBlocked() ?? string.Empty;

    public bool HasStartBlocked => StartBlocked.Length > 0;
    public bool CanStart => !view.Running && !busy && view.Read == FishingRead.Loaded && StartBlocked.Length == 0;
    public bool CanEditDuration => !view.Running;
    public bool CanStop => view.Running && !busy;

    public string Hours { get => hours; set { if (Set(ref hours, value)) ClearError(); } }
    public string Minutes { get => minutes; set { if (Set(ref minutes, value)) ClearError(); } }
    public string Seconds { get => seconds; set { if (Set(ref seconds, value)) ClearError(); } }

    public string DurationError => durationError;
    public bool HasDurationError => durationError.Length > 0;

    public string Message => message;
    public bool HasMessage => message.Length > 0;
    public bool MessageFailed => messageFailed;

    // ───────────────────────── 运行与结束 ─────────────────────────

    public bool HasRun => view.Run is not null;
    public string PhaseText => view.Run is { } run ? LifeText.FishingPhase(run.Phase) : string.Empty;

    /// <summary>已用时长与目标：首次抛竿前写“未开始计时”（不是 0）；目标为空写“直到停止”。</summary>
    public string ElapsedText
    {
        get
        {
            if (view.Run is not { } run) return string.Empty;
            var target = run.TargetSeconds is { } total ? Clock(total) : Pick("直到停止", "直到停止");
            if (!run.ClockStarted || run.ElapsedSeconds is not { } elapsed) return Pick($"未开始计时 · 目标 {target}", $"未開始計時 · 目標 {target}");
            var live = run.Active ? elapsed + (Environment.TickCount64 - receivedAt) / 1000.0 : elapsed;
            if (run.TargetSeconds is { } limit) live = Math.Min(live, limit);
            return Pick($"已用 {Clock((long)live)} · 目标 {target}", $"已用 {Clock((long)live)} · 目標 {target}");
        }
    }

    public string RoundsText => view.Run is { } run
        ? Pick($"上钩 {run.Rounds.Submitted} · 收竿 {run.Rounds.Reeled} · 未回升耗尽 {run.Rounds.DepletedWithoutRebound} · 无响应重抛 {run.Rounds.NoResponseRetries}",
            $"上鉤 {run.Rounds.Submitted} · 收竿 {run.Rounds.Reeled} · 未回升耗盡 {run.Rounds.DepletedWithoutRebound} · 無回應重拋 {run.Rounds.NoResponseRetries}")
        : string.Empty;

    public string MissText => view.Run is { Active: true, ConsecutiveRoundsWithoutHook: > 0 } run
        ? Pick($"连续 {run.ConsecutiveRoundsWithoutHook} 轮没有识别到上钩", $"連續 {run.ConsecutiveRoundsWithoutHook} 輪沒有辨識到上鉤") : string.Empty;

    public bool HasMiss => MissText.Length > 0;

    public string ReasonNote => view.Run is { Active: true } run ? LifeText.FishingNote(run.Reason) ?? string.Empty : string.Empty;
    public bool HasReasonNote => ReasonNote.Length > 0;

    /// <summary>结束原因与离开钓鱼姿态的确认情况；运行中为空。</summary>
    public string EndText
    {
        get
        {
            if (view.Run is not { Active: false } run) return string.Empty;
            var reason = LifeText.FishingEnd(run.StopReason ?? run.Phase);
            var pose = run.PoseLeft switch
            {
                FishingPoseState.Observed => Pick("已确认离开钓鱼姿态", "已確認離開釣魚姿態"),
                FishingPoseState.Unconfirmed => Pick("未能确认已离开钓鱼姿态，请在游戏里确认", "未能確認已離開釣魚姿態，請在遊戲裡確認"),
                FishingPoseState.NotNeeded => Pick("无需离开钓鱼姿态", "無需離開釣魚姿態"),
                _ => string.Empty
            };
            return pose.Length == 0 ? reason : reason + "；" + pose;
        }
    }

    public bool HasEnd => EndText.Length > 0;
    public bool EndFailed => view.Run is { Active: false, StopReason: var reason } && reason is not (null or "duration-reached" or "user-stop" or "cancelled" or "emergency-stop" or "host-stopped");

    private static string Clock(long totalSeconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}");
    }

    // ───────────────────────── 命令 ─────────────────────────

    private void ClearError()
    {
        if (durationError.Length == 0) return;
        durationError = string.Empty;
        Raise(nameof(DurationError)); Raise(nameof(HasDurationError));
    }

    /// <summary>三个框的时长换算：全部留空为“直到停止”；不是非负整数或合计超出范围时给出本地提示，不发请求。</summary>
    public static bool TryDuration(string hours, string minutes, string seconds, out int? total, out bool invalid)
    {
        total = null; invalid = false;
        var parts = new[] { hours, minutes, seconds }.Select(text => text.Trim()).ToArray();
        if (parts.All(text => text.Length == 0)) return true;
        long sum = 0;
        var factors = new long[] { 3600, 60, 1 };
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0) continue;
            if (parts[i].Length > 9 || !long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) { invalid = true; return false; }
            sum += value * factors[i];
        }
        if (sum is < MinimumSeconds or > MaximumSeconds) { invalid = true; return false; }
        total = (int)sum;
        return true;
    }

    private void RequestStart()
    {
        if (!CanStart) return;
        if (!TryDuration(hours, minutes, seconds, out var total, out _))
        {
            durationError = Pick("时长需要是整数小时、分钟、秒，合计 60 秒到 24 小时；三个框都留空表示直到停止", "時長需要是整數小時、分鐘、秒，合計 60 秒到 24 小時；三個框都留空表示直到停止");
            Raise(nameof(DurationError)); Raise(nameof(HasDurationError));
            return;
        }
        message = string.Empty;
        StartRequested?.Invoke(total);
    }

    /// <summary>确认后发出开始请求（带当前游戏会话与绑定修订，由后端适配层取）。</summary>
    public async Task StartAsync(int? durationSeconds) => await CommandAsync("start", durationSeconds);

    private async Task CommandAsync(string action, int? durationSeconds = null)
    {
        if (busy) return;
        busy = true; Raise(string.Empty);
        try
        {
            var reply = await backend.CommandFishingAsync(action, durationSeconds, CancellationToken.None);
            if (reply.Ok) { message = string.Empty; messageFailed = false; }
            else
            {
                message = Failure(reply);
                messageFailed = true;
            }
        }
        finally
        {
            busy = false;
            Refresh();
            Raise(string.Empty);
        }
    }

    // 后端拒绝：能力不可用时写后端给出的原因，活动被占用写明占用者（页面告知），其余按原因码。
    private string Failure(BackendReply reply)
    {
        if (reply.Code is "capability.unavailable" && view.UnavailableReason is { } reason) return LifeText.Reason(reason);
        if (reply.Code is "resource.busy") return startBlocked() ?? CodeText.Reason(reply.Code);
        return reply.Code is { } code ? LifeText.Reason(code) : reply.Message;
    }
}
