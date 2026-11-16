namespace MabiTool.Contracts;

public sealed record ReplayScenario(string Id, string Description, int FrameCount, bool Fails);
public sealed record ReplayCatalog(bool Enabled, string Mode, ObservationSource Source, IReadOnlyList<ReplayScenario> Scenarios);
public sealed record ReplayRequest(string ScenarioId, int IntervalMs);
public sealed record ReplayState(string OperationId, string ScenarioId, int Step, CombatSummary Combat,
    EffectsSnapshot SelfEffects, EffectsSnapshot MonsterEffects);
