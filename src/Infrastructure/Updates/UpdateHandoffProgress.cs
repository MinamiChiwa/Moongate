namespace MabiTool.Infrastructure.Updates;

internal sealed record UpdateHandoffTiming(string Stage, long ElapsedMs);
internal sealed record UpdateHandoffSnapshot(string Stage, long ElapsedMs, IReadOnlyList<UpdateHandoffTiming> Timings)
{
    internal void Apply(Exception error)
    {
        error.Data["handoffStage"] = Stage;
        error.Data["handoffElapsedMs"] = ElapsedMs;
        error.Data["handoffTimings"] = Timings;
    }
}

// 取消时固定单调时间快照，清理及迟到任务不能改变故障所在阶段。
internal sealed class UpdateHandoffProgress
{
    internal const int MaximumTimings = 32;
    private static readonly HashSet<string> stages = new(StringComparer.Ordinal)
    {
        "candidateStart", "connect", "exchange", "candidateExit", "hostHealth", "healthCheck", "challenge", "challenge.candidateTree",
        "challenge.previousTree", "offer", "accepted", "healthResult", "recordHealthy",
        "recordHealthy.candidateTree", "recordHealthy.previousTree", "recordHealthy.persist", "commitPersistence",
        "commit.candidateTree", "commit.previousTree", "commit.persist", "commit", "committed"
    };
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly List<UpdateHandoffTiming> timings = [];
    private string stage = "candidateStart";
    private long stageStarted;
    private UpdateHandoffSnapshot? frozen;

    internal UpdateHandoffProgress(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        started = stageStarted = this.clock.GetTimestamp();
    }

    internal static bool IsStage(string? value) => value is not null && stages.Contains(value);

    internal void SetStage(string next)
    {
        if (!IsStage(next)) throw new ArgumentException("Unknown handoff stage", nameof(next));
        lock (gate)
        {
            if (frozen is not null || next == stage) return;
            var now = clock.GetTimestamp();
            timings.Add(new(stage, Milliseconds(stageStarted, now)));
            if (timings.Count >= MaximumTimings) timings.RemoveAt(0);
            stage = next; stageStarted = now;
        }
    }

    internal UpdateHandoffSnapshot Freeze()
    {
        lock (gate)
        {
            if (frozen is not null) return frozen;
            var now = clock.GetTimestamp();
            return frozen = new(stage, Milliseconds(started, now),
                Array.AsReadOnly(timings.Append(new(stage, Milliseconds(stageStarted, now))).ToArray()));
        }
    }

    internal static void MergeFailureDetails(Exception primary, Exception secondary)
    {
        foreach (var key in new[] { "handoffMessage", "peerCheck", "peerOpenError", "transactionCallback", "transactionElapsedMs" })
            if (!primary.Data.Contains(key) && secondary.Data.Contains(key)) primary.Data[key] = secondary.Data[key];
    }

    private long Milliseconds(long from, long to) => Math.Max(0, (long)clock.GetElapsedTime(from, to).TotalMilliseconds);
}
