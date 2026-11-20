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
    CombatEntityObservation? LocalEntity, CombatFrame Frame,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CombatEntityCarry? Carry = null);
/// <summary>
/// Worker 与宿主之间私有的实体延续记录：某条游戏连接上自动体的类型、表 ID、最大 HP、首领标志与 HP 解码信息（KeyId 与 Base64 的 16 字节 IV）。
/// Updated 为出现后收到过 HP 更新（区分服务器不下发血量的锁血实体）。
/// 宿主按游戏进程与连接保存，捕获重启时随捕获请求回传，只在同一连接的流上生效；不进入公开快照、历史、诊断与日志。
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CombatEntityCarry(ObservedConnection Connection, IReadOnlyList<CombatCarriedEntity> Entities);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CombatCarriedEntity(UInt64String Id, int ActorType, UInt64String? SheetId, UInt64String? HealthMaximum, bool Boss,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UInt64String? KeyId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Iv = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Encrypted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Updated = null);
