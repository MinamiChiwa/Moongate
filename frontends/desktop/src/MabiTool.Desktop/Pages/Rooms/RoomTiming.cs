using System.Diagnostics;
using System.Globalization;
using MabiTool.Contracts;
using static MabiTool.Desktop.Localization.UiText;

namespace MabiTool.Desktop.Pages.Rooms;

/// <summary>
/// 本轮的时间基准（<c>RoomRoundSnapshot.timing</c>）。<see cref="ReceivedAt"/> 是收到响应时的本机单调时刻（<see cref="Stopwatch"/> 计数）；
/// 之后任一时刻的剩余量都以它为零点按本机单调时钟递减，不再向后端请求。
/// </summary>
public sealed record RoomTiming(double ServerNowMs, double RoundTripMs, double UncertaintyMs, int TimingOffsetMs,
    double? StartRemainingMs, double? StopRemainingMs, long ReceivedAt)
{
    public static RoomTiming? From(RoomRoundTiming? value, long receivedAt) => value is null ? null
        : new(value.ServerNowMs, value.RoundTripMs, value.UncertaintyMs, value.TimingOffsetMs, value.StartRemainingMs, value.StopRemainingMs, receivedAt);

    /// <summary>收到响应以来经过的毫秒。</summary>
    public double ElapsedMs(long now) => Math.Max(0, (now - ReceivedAt) * 1000d / Stopwatch.Frequency);

    /// <summary>距同步开始的毫秒；负值为开始已过去的毫秒。</summary>
    public double? StartRemaining(long now) => StartRemainingMs - ElapsedMs(now);

    /// <summary>距统一停止的毫秒；本轮没有停止动作时为空。</summary>
    public double? StopRemaining(long now) => StopRemainingMs - ElapsedMs(now);

    /// <summary>其他服务器目标时刻的剩余毫秒：目标 + 个人偏移 − 估计的服务器此刻。</summary>
    public double Remaining(double targetServerMs, long now) => targetServerMs + TimingOffsetMs - (ServerNowMs + ElapsedMs(now));
}

/// <summary>状态卡的倒计时进度（原工具 RefreshCountdown）：距离同步开始为蓝色，距离统一停止为绿色。</summary>
/// <param name="Fraction">剩余占总时长的比例（0–1）。</param>
/// <param name="Calibrating">还没有校时样本，只显示“正在校时”。</param>
public sealed record RoomCountdown(bool Stopping, string Label, double Fraction, string Value, bool Calibrating);

/// <summary>总指挥乐谱的估计播放进度（原工具信息浮窗 ScoreProgress）；<see cref="Fraction"/> 为空时只显示文字。</summary>
public sealed record RoomScoreProgress(double? Fraction, string Text);

/// <summary>倒计时、阶段剩余秒数与乐谱进度的换算，均为纯函数；<c>now</c> 是本机单调时刻。</summary>
public static class RoomClock
{
    public static RoomCountdown? Countdown(RoomRound? round, RoomTiming? timing, long now)
    {
        if (round is not { Active: true }) return null;
        var beforeStart = round.Phase is "preparing" or "committed";
        var stopReady = round.HasStop && round.StopCommitted;
        if (timing is null)
            return beforeStart || stopReady
                ? new(!beforeStart, Label(!beforeStart), 0, Pick("正在校时", "正在校時"), true)
                : null;
        double remaining, total;
        bool stopping;
        if (beforeStart && timing.StartRemaining(now) is { } start && start > 0)
        {
            stopping = false;
            remaining = start;
            total = round.CountdownMs > 0 ? round.CountdownMs : 5000;
        }
        // 停止时刻过去后不再显示停止倒计时（只剩 0.0 秒的空条），阶段文字改为收尾状态（RoomFlow.WrapUp）。
        else if (stopReady && timing.StopRemaining(now) is { } stop && stop > 0)
        {
            stopping = true;
            remaining = stop;
            total = Math.Max(1, timing.StopRemainingMs!.Value - (timing.StartRemainingMs ?? 0));
        }
        else return null;
        return new(stopping, Label(stopping), Math.Clamp(remaining / Math.Max(1, total), 0, 1), SecondsText(remaining), false);
    }

    /// <summary>剩余秒数，一位小数；已过去时为 0。</summary>
    public static string SecondsText(double remainingMs) =>
        Pick($"{Math.Max(0, remainingMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)} 秒",
            $"{Math.Max(0, remainingMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)} 秒");

    /// <summary>
    /// 按下开始到游戏起奏的间隔：游戏新建合奏时在帧内给出起奏偏移（ReplicatedTime.offsetInSeconds），
    /// 2026-11-14、11-15 四份独奏与合奏实采的 11 次新建合奏中 10 次为 1.5 秒、1 次为 1.0 秒。
    /// 房间服务还没有给出本轮观测的起奏时刻时，乐谱进度按“同步开始 + 这段间隔”起算。
    /// </summary>
    public const double GameStartLeadMs = 1500;

    /// <summary>
    /// 乐谱播放进度，起点是游戏实际起奏的时刻：房间服务已绑定本轮乐谱（<see cref="RoomRound.Cycle"/>）时取抓包观测的起奏时刻，
    /// 否则取同步开始 + <see cref="GameStartLeadMs"/>。总指挥一轮且已安排停止时不循环，其余按完整乐谱时长循环计轮。
    /// 时长优先取本轮绑定的整谱时长，总指挥停止演奏、识别结果消失后进度仍然保留；两者都没有时为空。
    /// </summary>
    public static RoomScoreProgress? ScoreProgress(RoomRound? round, RoomScore? score, RoomTiming? timing, long now)
    {
        var bound = round is { Active: true } ? round.Cycle : null;
        if ((bound?.DurationSeconds ?? score?.WholeSeconds) is not { } seconds || !double.IsFinite(seconds) || seconds <= 0) return null;
        var length = RoomJson.Seconds(seconds);
        var start = timing is null ? null
            : bound is not null ? timing.Remaining(bound.StartServerMs, now) : timing.StartRemaining(now) + GameStartLeadMs;
        if (round is not { Active: true } || start is null)
            return new(null, Pick($"一轮 {length} 秒 · 等待演奏", $"一輪 {length} 秒 · 等待演奏"));
        var elapsed = -start.Value / 1000;
        if (elapsed < 0) return new(null, Pick($"一轮 {length} 秒 · 等待开始", $"一輪 {length} 秒 · 等待開始"));
        var ending = round.StopRule == "first-cycle" && round.HasStop && round.StopCommitted;
        var cycle = ending ? 1 : Math.Floor(elapsed / seconds) + 1;
        var position = ending ? elapsed : elapsed % seconds;
        var shown = Math.Min(position, seconds).ToString("0.0", CultureInfo.InvariantCulture);
        var cycleText = cycle.ToString("0", CultureInfo.InvariantCulture);
        return new(Math.Clamp(position / seconds, 0, 1),
            Pick($"{shown} / {length} 秒 · 第 {cycleText} 轮 · 估计进度", $"{shown} / {length} 秒 · 第 {cycleText} 輪 · 估計進度"));
    }

    /// <summary>执行记录里的校时说明（原工具 ClockText）。</summary>
    public static string ClockText(RoomTiming? timing) => timing is null
        ? Pick("正在校时", "正在校時")
        : Pick($"网络往返 {timing.RoundTripMs.ToString("F1", CultureInfo.InvariantCulture)} ms · 校时估计误差 ±{timing.UncertaintyMs.ToString("F1", CultureInfo.InvariantCulture)} ms",
            $"網路往返 {timing.RoundTripMs.ToString("F1", CultureInfo.InvariantCulture)} ms · 校時估計誤差 ±{timing.UncertaintyMs.ToString("F1", CultureInfo.InvariantCulture)} ms");

    private static string Label(bool stopping) => stopping ? Pick("距离统一停止", "距離統一停止") : Pick("距离同步开始", "距離同步開始");
}
