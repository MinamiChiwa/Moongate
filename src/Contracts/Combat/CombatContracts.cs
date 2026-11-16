using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public sealed record EntityKey(string GameSessionId, UInt64String ObservationGeneration,
    UInt64String RawInstanceId, UInt64String? SpawnGeneration);
public sealed record CombatSummary(string EncounterId, EntityKey LocalEntity, string DisplayName,
    DamageAmountString AttributedDamage, double DurationMs, double? EncounterDps, double? RollingDps,
    double RollingWindowMs, string CalculationVersion, bool Final, string BoundaryReason,
    OpenEnum<ObservationQuality> Quality, IReadOnlyList<string> Reasons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Owner = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObservationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UInt64String? Revision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatHealingSummary? Healing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DpsDurationMs = null);
public enum EffectDurationKind { Finite, Infinite, Unknown }
public enum EffectPresence { Observed, PredictedExpired, ConfirmedRemoved, OutOfScope }
public sealed record EffectInstance(EntityKey EntityKey, UInt64String EffectId, UInt64String InstanceId,
    EntityKey? CasterKey, DateTimeOffset AppliedAt, DateTimeOffset? ExpiresAt, OpenEnum<EffectDurationKind> DurationKind,
    int? Stacks, string? Category, OpenEnum<EffectPresence> Presence, OpenEnum<ObservationQuality> Quality, UInt64String Revision);
public sealed record EffectsSnapshot(EntityKey EntityKey, bool Complete, IReadOnlyList<EffectInstance> Effects,
    DateTimeOffset LastObservedAt, OpenEnum<ObservationQuality> Quality,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Owner = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObservationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntityId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UInt64String? Revision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SnapshotRequired = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ChangedEntityIds = null);
