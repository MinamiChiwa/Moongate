namespace MabiTool.ObservationDebugTool;

/// <summary>
/// 分段场景的时间安排（相对战斗采集开始的秒数）：0–QuietFrom 攻击，QuietFrom–QuietTo 完全停手，之后恢复攻击，
/// SplitAt 起由工具执行手动拆段，拆段后继续攻击到结束。
/// </summary>
public sealed record SegmentationOptions(double QuietFromSeconds = 40, double QuietToSeconds = 100, double SplitAtSeconds = 140, double RequiredQuietSeconds = 30)
{
    public ScenarioDisplay Phase(double elapsedSeconds, bool splitApplied, double? remainingSeconds)
    {
        if (elapsedSeconds < QuietFromSeconds)
            return new("持续攻击", $"持续攻击怪物，不要切图。第 {QuietFromSeconds:0} 秒起停手。", remainingSeconds);
        if (elapsedSeconds < QuietToSeconds)
            return new("停手", $"完全停手：不攻击、不切图，留在原地。第 {QuietToSeconds:0} 秒起恢复攻击。", remainingSeconds);
        if (elapsedSeconds < SplitAtSeconds)
            return new("恢复攻击", $"恢复攻击怪物，不要切图。第 {SplitAtSeconds:0} 秒工具执行手动拆段。", remainingSeconds);
        return splitApplied
            ? new("拆段后攻击", "手动拆段已执行，继续攻击到倒计时结束。", remainingSeconds)
            : new("等待拆段", "正在执行手动拆段，请继续攻击。", remainingSeconds);
    }
}

/// <param name="StartReason">分段开始原因；<paramref name="EndReason"/> 为结束原因，进行中的分段为 active。</param>
public sealed record SegmentOutcome(string? StartReason, string? EndReason, bool HasDamage);

/// <summary>
/// 分段验收证据：同一分段内相邻两笔本机伤害之间的最长间隔（证明长停手未触发拆段）、手动拆段的尝试与生效，
/// 以及按分段结果给出的检查项。
/// </summary>
public sealed class SegmentationEvidence(SegmentationOptions options)
{
    private readonly object gate = new();
    private (double AtMs, string Id)? lastDamage;
    private double longestQuietMs;
    private double? longestQuietFromMs;
    private int splitAttempts;
    private double? splitAppliedAtMs;

    public SegmentationOptions Options { get; } = options;
    public double LongestQuietMs { get { lock (gate) return longestQuietMs; } }
    public double? LongestQuietFromMs { get { lock (gate) return longestQuietFromMs; } }
    public int SplitAttempts { get { lock (gate) return splitAttempts; } }
    public double? SplitAppliedAtMs { get { lock (gate) return splitAppliedAtMs; } }
    public bool SplitApplied => SplitAppliedAtMs is not null;

    /// <summary>记录一帧；前后两笔本机伤害的分段 ID 相同才计入间隔。</summary>
    public void Observe(double atMs, string? activeEncounterId, bool localDamage)
    {
        if (!localDamage) return;
        lock (gate)
        {
            if (lastDamage is { } previous && activeEncounterId is not null && previous.Id == activeEncounterId && atMs - previous.AtMs > longestQuietMs)
            { longestQuietMs = atMs - previous.AtMs; longestQuietFromMs = previous.AtMs; }
            lastDamage = activeEncounterId is null ? null : (atMs, activeEncounterId);
        }
    }

    public void RecordSplitAttempt(double atMs, bool applied)
    {
        lock (gate)
        {
            splitAttempts++;
            if (applied && splitAppliedAtMs is null) splitAppliedAtMs = atMs;
        }
    }

    public IReadOnlyDictionary<string, bool> Evaluate(IReadOnlyList<SegmentOutcome> segments) => new Dictionary<string, bool>
    {
        ["quietHeldWithoutSplit"] = LongestQuietMs >= Options.RequiredQuietSeconds * 1000,
        ["noIdleTimeoutBoundary"] = segments.All(value => value.EndReason != "idle-timeout"),
        ["manualSplitApplied"] = SplitApplied,
        ["manualSplitClosedSegment"] = segments.Any(value => value.EndReason == "manual-split"),
        ["manualSplitStartedSegment"] = segments.Any(value => value.StartReason == "manual-split" && value.HasDamage),
    };
}
