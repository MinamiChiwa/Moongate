using System.Globalization;

namespace MabiTool.ObservationDebugTool;

/// <summary>固定时间线中的一步：AtSeconds 为相对采集开始的单调秒数，Action 为工具在该时点执行的启停动作。</summary>
public sealed record TimelineStep(double AtSeconds, string Stage, string Instruction, Func<CancellationToken, Task>? Action = null, string? Code = null);

/// <summary>按单调时钟推进的场景时间线；显示当前动作、下一步预告与总倒计时。</summary>
public static class ScenarioTimeline
{
    public static async Task RunAsync(ScenarioContext context, IReadOnlyList<TimelineStep> steps, double totalSeconds, CancellationToken ct)
    {
        if (steps.Count == 0 || steps[0].AtSeconds != 0 || steps.Zip(steps.Skip(1)).Any(pair => pair.Second.AtSeconds < pair.First.AtSeconds) ||
            steps[^1].AtSeconds > totalSeconds) throw new ArgumentException("timeline.invalid");
        var clock = context.Clock;
        var begin = clock.GetTimestamp();
        var end = begin + (long)(totalSeconds * clock.TimestampFrequency);
        double Elapsed() => clock.GetElapsedTime(begin).TotalSeconds;
        var index = -1;
        context.Show(() =>
        {
            var at = Math.Max(0, Volatile.Read(ref index));
            var step = steps[at];
            var next = at + 1 < steps.Count ? steps[at + 1] : null;
            var nextText = next is null ? "最后一步：倒计时结束后自动停止并核对资源释放。"
                : $"{Math.Max(0, Math.Ceiling(next.AtSeconds - Elapsed())).ToString(CultureInfo.InvariantCulture)} 秒后：{next.Stage}";
            return new(step.Stage, step.Instruction, Math.Max(0, (end - clock.GetTimestamp()) / (double)clock.TimestampFrequency), nextText);
        });
        for (var i = 0; i < steps.Count; i++)
        {
            await WaitUntilAsync(context, () => Elapsed() >= steps[i].AtSeconds, ct);
            Volatile.Write(ref index, i);
            context.Mark(steps[i].Code ?? "timeline.step" + i.ToString(CultureInfo.InvariantCulture), steps[i].Stage);
            if (steps[i].Action is { } action) await action(ct);
        }
        await WaitUntilAsync(context, () => clock.GetTimestamp() >= end, ct);
        context.Mark("timeline.completed");
    }

    public static async Task WaitUntilAsync(ScenarioContext context, Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }
    }
}
