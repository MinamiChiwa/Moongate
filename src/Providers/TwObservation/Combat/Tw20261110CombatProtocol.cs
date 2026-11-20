using System.Collections.Frozen;
using System.Text.Json;
using MabiTool.Contracts;

namespace MabiTool.Providers.TwObservation.Combat;

/// <summary>
/// 按 2026-11-10 台服磁盘枚举及本地实采登记；原生能力授权由捕获来源另行核验。
/// v1 为十项伤害/效果协议；v2 另加治疗三项（受疗 20645、施放 20644、本机 HP 170205）；v3 再加本机战斗状态 170196；
/// v4 再加实体血量、死亡、离场与本机主目标（SetHealth2–6、UpdateHealthMax 与其密钥广播、SetDead、DisappearingChannelObject、MainTarget.SetTarget）。
/// 当前构建的捕获与战斗来源配置使用 v4，Worker 同时受理 v1（不解析治疗）、v2（不解析战斗状态）与 v3（不解析血量与主目标）以兼容旧宿主。
/// </summary>
public static class Tw20261110CombatProtocol
{
    public const string ProfileV1 = "tw-combat-20261110-v1";
    public const string ProfileV2 = "tw-combat-20261110-v2";
    public const string ProfileV3 = "tw-combat-20261110-v3";
    public const string Profile = "tw-combat-20261110-v4";
    public const string CaptureProfile = Profile;
    public const int HealCasted = 20644, HealReceived = 20645, SetHealth = 170205, SetCombatState = 170196;
    public const int SetHealth2 = 170206, SetHealth6 = 170210, UpdateHealthMax = 170151, BroadcastKey = 210022, SetDead = 170197,
        Disappearing = 20272, SetMainTarget = 170739;
    public const string AssemblySha256 = "4304456511f58cb543169434908f46726845d2ab2ec33668170b47a4de02a549";
    public const string MetadataSha256 = "2d98c3b1f8adc6d3e5455ac24b10a3374dc2401dc1d0a6749b336f44d841f6b4";
    public static string GameBuild => TwClientFingerprint.GameBuild;
    private static readonly ObservationSource Source = new("tw-combat", "1.0.0", "TW", GameBuild);
    /// <summary>当前构建的战斗来源配置（v4，含治疗、本机战斗状态、实体血量与本机主目标）。</summary>
    public static ObservationCombatProfile CombatProfile { get; } = new(Profile, Source, Healing: true);
    /// <summary>v3 战斗来源配置：含治疗与本机战斗状态，不解析实体血量与主目标。</summary>
    public static ObservationCombatProfile CombatProfileV3 { get; } = new(ProfileV3, Source, Healing: true);
    /// <summary>v2 战斗来源配置：含治疗，不解析本机战斗状态。</summary>
    public static ObservationCombatProfile CombatProfileV2 { get; } = new(ProfileV2, Source, Healing: true);
    /// <summary>v1 战斗来源配置，供旧宿主请求与回放读取；不解析治疗。</summary>
    public static ObservationCombatProfile CombatProfileV1 { get; } = new(ProfileV1, Source);
    /// <summary>受理的捕获配置：v1–v4 均使用当前构建布局。</summary>
    public static bool IsCaptureProfile(string? value) => value is ProfileV1 or ProfileV2 or ProfileV3 or Profile;
    /// <summary>按捕获配置返回战斗来源配置；未知配置为空。</summary>
    public static ObservationCombatProfile? CombatProfileFor(string? captureProfile) => captureProfile switch
    {
        Profile => CombatProfile,
        ProfileV3 => CombatProfileV3,
        ProfileV2 => CombatProfileV2,
        ProfileV1 => CombatProfileV1,
        _ => null
    };
    // 线上协议号（tw-20261110 GCProtocol 枚举）。
    public const int PersonalData = 20590, AppearingCharacter = 20054, AppearingAutomaton = 20051;
    public const int HitPresentation = 20526, DamageFloater = 21196;
    public const int BuffAdd = 170069, BuffExpire = 170070, BuffRefresh = 170071;
    public const int SetBreakPoint = 170204, UpdateBreakPointMax = 170117;
    /// <summary>伤害等待技能表现、受疗等待 HP 更新的关联窗口：流内前后字节数与最长等待时长。</summary>
    public const int CorrelationWindowBytes = 400, CorrelationWaitMs = 250;
    /// <summary>v1 的十项协议：身份、出现、技能表现、伤害、效果三项与破防两项；按集合精确比较，不追加。</summary>
    public static IReadOnlySet<int> ProtocolsV1 { get; } = new[]
    {
        PersonalData, AppearingCharacter, AppearingAutomaton, DamageFloater, HitPresentation,
        BuffAdd, BuffExpire, BuffRefresh, SetBreakPoint, UpdateBreakPointMax
    }.ToFrozenSet();
    /// <summary>v2：v1 加治疗三项。</summary>
    public static IReadOnlySet<int> ProtocolsV2 { get; } = ProtocolsV1.Concat([HealCasted, HealReceived, SetHealth]).ToFrozenSet();
    /// <summary>v3：v2 加本机战斗状态。</summary>
    public static IReadOnlySet<int> ProtocolsV3 { get; } = ProtocolsV2.Append(SetCombatState).ToFrozenSet();
    /// <summary>v4：v3 加实体血量（SetHealth2–6、UpdateHealthMax 与密钥广播）、死亡、离场与本机主目标。</summary>
    public static IReadOnlySet<int> ProtocolsV4 { get; } = ProtocolsV3.Concat([SetHealth2, SetHealth2 + 1, SetHealth2 + 2, SetHealth2 + 3, SetHealth6,
        UpdateHealthMax, BroadcastKey, SetDead, Disappearing, SetMainTarget]).ToFrozenSet();
    public static GameStreamProfile StreamProfile { get; } = new(Known(), ProtocolsV4);
    private static IReadOnlySet<int> Known()
    {
        using var stream = typeof(Tw20261110CombatProtocol).Assembly.GetManifestResourceStream("MabiTool.Tw20261110Protocols")!;
        return JsonSerializer.Deserialize<int[]>(stream)!.ToFrozenSet();
    }
}
