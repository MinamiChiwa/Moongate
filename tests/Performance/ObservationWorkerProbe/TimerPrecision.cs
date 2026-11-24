using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiTool.Contracts;
using MabiTool.ModuleSdk;

namespace MabiTool.ObservationWorkerProbe;

/// <summary>
/// 短等待的实际时长：同一目标间隔分别用系统计时器（<see cref="WaitHandle.WaitOne(int)"/>、<see cref="Task.Delay(int)"/>，
/// 改动前读取循环、写出合批与宿主合并所用）与本进程高精度可等待计时器（<see cref="PreciseTimer"/>，同步与异步两种）等待，
/// 记录实际经过时间与超出目标的部分。不调用 timeBeginPeriod，整机计时器精度保持系统默认。
/// </summary>
internal static class TimerPrecision
{
    public static async Task<JsonNode> RunAsync(int samples)
    {
        using var timer = new PreciseTimer();
        using var never = new ManualResetEvent(false);
        var targets = new[] { 1, 4, 10, 16, 20 };
        var result = new JsonObject { ["highResolution"] = timer.HighResolution, ["samplesPerCase"] = samples };
        var cases = new JsonArray();
        foreach (var target in targets)
        {
            var span = TimeSpan.FromMilliseconds(target);
            cases.Add(Case("WaitHandle.WaitOne（系统计时器）", target, await MeasureAsync(samples, () => { never.WaitOne(target); return Task.CompletedTask; })));
            cases.Add(Case("Task.Delay（系统计时器）", target, await MeasureAsync(samples, () => Task.Delay(span))));
            cases.Add(Case("PreciseTimer.Wait（高精度，同步）", target, await MeasureAsync(samples, () => { timer.Wait(span, never); return Task.CompletedTask; })));
            cases.Add(Case("PreciseTimer.DelayAsync（高精度，异步）", target, await MeasureAsync(samples, () => timer.DelayAsync(span))));
        }
        result["cases"] = cases;
        return result;
    }

    private static async Task<List<double>> MeasureAsync(int samples, Func<Task> wait)
    {
        var values = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await wait();
            values.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            // 错开相位：下一次等待不总从计时器中断之后立即开始。
            Thread.SpinWait(Random.Shared.Next(1000, 200_000));
        }
        return values;
    }

    private static JsonNode Case(string method, int target, List<double> values) => JsonSerializer.SerializeToNode(new
    {
        method, targetMs = target, elapsedMs = WriteLatency.Percentiles(values),
        overshootMs = WriteLatency.Percentiles(values.Select(value => Math.Max(0, value - target)).ToList())
    }, ContractJson.Options)!;
}
