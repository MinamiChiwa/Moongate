using MabiTool.Contracts;
using MabiTool.Modules.CombatObservation.Domain;

namespace MabiTool.Providers.Replay;

/// <summary>纯合成领域样本；不访问进程、窗口、网络捕获或桌面输入。</summary>
public sealed class ReplayProvider(string epoch, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly long started = (timeProvider ?? TimeProvider.System).GetTimestamp();
    public static ObservationSource Source { get; } = new("replay-synthetic", "1.0", "TW", "mock-v1");
    public static IReadOnlyList<ReplayScenario> Scenarios { get; } = Array.AsReadOnly<ReplayScenario>([
        new("normal", "三帧伤害、自身与怪物效果；正常完成", 3, false),
        new("partial", "中途缺失观测，末帧恢复完整观测", 3, false),
        new("failure", "一帧后模拟 Provider 失联", 1, true)
    ]);
    public static ReplayScenario Validate(ReplayRequest request)
    {
        if (request.IntervalMs is < 25 or > 2000) throw new ArgumentException("步进间隔须为 25–2000 ms");
        return Scenarios.SingleOrDefault(x => x.Id == request.ScenarioId) ?? throw new ArgumentException("回放场景不存在");
    }
    public Observation<ReplayState> Frame(string operationId, ReplayScenario scenario, int step)
    {
        if (step < 1 || step > scenario.FrameCount) throw new ArgumentOutOfRangeException(nameof(step));
        var now = clock.GetUtcNow();
        var partial = scenario.Id == "partial" && step == 2;
        var quality = partial ? ObservationQuality.Partial : ObservationQuality.Confirmed;
        var self = new EntityKey("mock-" + operationId, new(1), new(9007199254740993), new(1));
        var monster = self with { RawInstanceId = new(9007199254740995) };
        var session = new CombatSession("replay", operationId, Source, CombatReplayFrames.Entity(self, CombatEntityRole.LocalPlayer, "模擬角色"),
            new(self.GameSessionId), now, encounterIdFactory: () => "mock-" + operationId);
        for (var current = 1; current <= step; current++) session.Apply(CombatReplayFrames.Frame(self, scenario.Id, current, now.AddMilliseconds((current - step) * 1000)));
        if (step == scenario.FrameCount && !scenario.Fails) session.Close("replay-end", now, (step - 1) * 1000);
        var projection = session.Capture();
        return new(new(operationId, scenario.Id, step, projection.Workspace.Encounter!.Summary,
            projection.Workspace.SelfEffects!.Snapshot, projection.Effects.Single(x => x.Snapshot.EntityKey == monster).Snapshot), Source, now, epoch,
            clock.GetElapsedTime(started).TotalMilliseconds, 1000, new(1), new((ulong)step), quality);
    }
}
