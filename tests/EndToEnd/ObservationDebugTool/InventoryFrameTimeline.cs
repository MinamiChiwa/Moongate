using MabiTool.Host.Game;

namespace MabiTool.ObservationDebugTool;

/// <summary>
/// Host 受理的库存帧诊断（种类、原因、条目数）按场景时间投影。首次完整同步前库存快照没有工作区，失效原因只能从这里读取。
/// </summary>
public static class InventoryFrameTimeline
{
    /// <summary>信号键：Invalidated 帧取原因码，Full/Delta 取 inventory.frame.full / inventory.frame.delta。</summary>
    public static string Signal(InventoryFrameDiagnostic frame) => frame.Kind == "Invalidated"
        ? Reason(frame.Reason) ?? "inventory.invalidated"
        : "inventory.frame." + frame.Kind.ToLowerInvariant();

    /// <summary>库存原因码只保留 inventory.&lt;名称&gt; 形式；其余文本不进入报告。</summary>
    public static string? Reason(string? code) => code is null ? null :
        System.Text.RegularExpressions.Regex.IsMatch(code, @"\Ainventory\.[A-Za-z]{1,64}\z") ? code : "probe.unrecognizedObservationReason";

    public static IReadOnlyList<InventoryFrameRecord> Project(InventoryCaptureDiagnostics? diagnostics, ScenarioContext context) =>
        diagnostics?.Frames.Select(frame => new InventoryFrameRecord(Math.Round(context.SinceStartMs(frame.HostMonotonicMs), 1), frame.Kind,
            frame.Kind == "Invalidated" ? Reason(frame.Reason) : null, frame.Items, frame.Changes,
            frame.IdentityPresent, frame.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Math.Round(frame.HostMonotonicMs - frame.WorkerMonotonicMs, 1))).ToArray() ?? [];

    /// <summary>时间窗 [fromMs, toMs]（相对点击开始）内的完整同步与最大条目数。</summary>
    public static InventoryWindowSummary Window(IReadOnlyList<InventoryFrameRecord> frames, double fromMs, double? toMs)
    {
        var inside = frames.Where(frame => frame.AtMs >= fromMs && (toMs is null || frame.AtMs <= toMs)).ToArray();
        var full = inside.Where(frame => frame.Kind == "Full").ToArray();
        return new(full.Length > 0, full.FirstOrDefault()?.AtMs, full.Length == 0 ? null : full.Max(frame => frame.Items),
            inside.Count(frame => frame.Kind == "Delta"),
            inside.Where(frame => frame.Reason is not null).GroupBy(frame => frame.Reason!)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
    }
}

/// <param name="AtMs">Host 受理时点，相对点击开始。</param>
/// <param name="QueueMs">Host 受理时点减 Worker 帧时点。</param>
public sealed record InventoryFrameRecord(double AtMs, string Kind, string? Reason, int Items, int Changes, bool IdentityPresent, string Generation, double QueueMs);
public sealed record InventoryWindowSummary(bool FullSyncObserved, double? FirstFullAtMs, int? FullItems, int Deltas, IReadOnlyDictionary<string, int> Reasons);
