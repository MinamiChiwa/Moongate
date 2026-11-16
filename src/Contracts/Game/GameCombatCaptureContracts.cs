using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public sealed record ObservationCombatProfile(string Id, ObservationSource Source,
    bool Damage = true, bool SelfEffects = true, bool MonsterEffects = true, bool Simulation = false, bool Healing = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObservationCombatInterest(string Id, string Profile, bool DamageEnabled,
    bool SelfEffectsEnabled, bool MonsterEffectsEnabled);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObservationCaptureInterests(bool MusicEnabled, ObservationCombatInterest? Combat,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObservationInventoryInterest? Inventory = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CombatCaptureReport(int SchemaVersion, string InterestId,
    CombatEntityObservation? LocalEntity, CombatFrame Frame);
