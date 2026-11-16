namespace MabiTool.ObservationDebugTool;

public sealed record ProbeOperatorSnapshot(string Stage, string Instruction, double RemainingSeconds, bool CanStart, bool IsStopped);

// The window reads the same monotonic budget that governs capture cancellation.
public sealed class ProbeCaptureWindow(int preparationSeconds, int combatSeconds, TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private long started;
    private string phase = "idle";
    private bool ready;
    private bool identity;
    private DateTimeOffset? combatStartedAt;
    public DateTimeOffset? CombatStartedAt { get { lock (gate) return combatStartedAt; } }
    public bool StartPreparation()
    {
        lock (gate)
        {
            if (phase != "idle") return false;
            phase = "preparation"; started = time.GetTimestamp(); return true;
        }
    }
    public void CaptureReady() { lock (gate) if (phase == "preparation") ready = true; }
    public void AwaitingCapture() { lock (gate) if (phase == "preparation") { ready = false; identity = false; } }
    public void IdentityReady() { lock (gate) if (phase == "preparation") identity = true; }
    public bool StartCombat()
    {
        lock (gate)
        {
            if (phase != "preparation" || !ready || !identity || Remaining() <= 0) return false;
            phase = "combat"; started = time.GetTimestamp(); combatStartedAt = time.GetUtcNow(); return true;
        }
    }
    public void Stop() { lock (gate) phase = "stopped"; }
    public bool Expired { get { lock (gate) return phase is "preparation" or "combat" && Remaining() <= 0; } }
    private double Remaining() => Math.Max(0, (phase == "combat" ? combatSeconds : preparationSeconds) - time.GetElapsedTime(started).TotalSeconds);
    public ProbeOperatorSnapshot Snapshot()
    {
        lock (gate)
        {
            if (phase == "idle") return new("尚未开始", $"点击开始准备后进行网络观察。准备最多 {preparationSeconds / 60} 分钟；找到目标后另行开始 {combatSeconds} 秒战斗。", preparationSeconds, true, false);
            if (phase == "stopped") return new("正在停止", "正在释放采集资源，请稍候。", 0, false, true);
            if (phase == "combat") return new("战斗采集中", "现在攻击，触发自身／怪物 Buff 和破防变化。倒计时结束自动停止。", Remaining(), false, false);
            return new(identity ? "准备完成" : ready ? "可以切图" : "正在启动", identity ? "身份已确认。可以慢慢寻找目标，准备好后点击开始战斗采集。" : ready ? "请切图以取得身份。切图与找怪不占用战斗时间。" : "正在等待战斗解析就绪，请等待窗口显示可以切图。", Remaining(), ready && identity && Remaining() > 0, false);
        }
    }
}
