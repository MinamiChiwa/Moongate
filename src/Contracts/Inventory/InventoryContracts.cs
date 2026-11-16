using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public enum InventoryContainer { Backpack, CharacterStorage, AccountStorage }
public enum InventoryFrameKind { Full, Delta, Invalidated }
public enum InventoryDeltaKind { Upsert, Remove }
public enum InventoryNameState { Unknown, Candidate, ClientCatalog, VerifiedSource, UserConfirmed, UserCleared }

public sealed record InventoryTypeKey(string Category, UInt64String TypeId, string? Sheet = null);
/// <param name="MasterCharacterId">登录数据 <c>AccountProfile.MasterCharacterId</c>（账户主角色 ID）；没有读出时为空。读模型据此确定账户：同一主角色的角色归入同一账户，并与游戏账户编号关联。</param>
public sealed record InventoryIdentity(string AccountId, string CharacterId, string RawName, string DisplayName, string? MasterCharacterId = null);
public sealed record InventoryItem(string Category, OpenEnum<InventoryContainer> Container, string Key,
    UInt64String TypeId, UInt64String? InstanceId, UInt64String? Quantity, JsonElement Attributes,
    string? Sheet = null, bool Equipped = false)
{
    [JsonIgnore] public InventoryTypeKey Type => new(Category, TypeId, Sheet);
}
public sealed record InventoryDelta(OpenEnum<InventoryDeltaKind> Kind, InventoryItem Item);
public sealed record InventoryCoverage(string Category, OpenEnum<InventoryContainer> Container, bool Complete,
    string? Reason = null);
public sealed record InventoryFrame(int SchemaVersion, ObservationSource Source, string GameSessionId,
    UInt64String Generation, UInt64String Sequence, DateTimeOffset ObservedAt, double MonotonicMs,
    OpenEnum<InventoryFrameKind> Kind, InventoryIdentity? Identity, IReadOnlyList<InventoryItem> Items,
    IReadOnlyList<InventoryDelta> Changes, IReadOnlyList<InventoryCoverage> Coverage, UInt64String? Gold = null,
    string? Reason = null, string? BodySha256 = null, UInt64String? BindingRevision = null,
    IReadOnlyList<InventoryResourceChange>? Resources = null);
/// <summary>
/// 钱包资源的一次观察，随库存帧附带（<see cref="InventoryFrame.Resources"/>；完整同步帧带登录数据里读到的全部钱包数值，增量帧带变化消息）。<see cref="Key"/> 是钱包字段名（如 goldFree、silverCoin、passDeep），
/// 深渊副本货币为 <c>abyssRaid:</c> 加 AbyssRaidMoneyType 名称。<see cref="Value"/> 是登录数据或变化消息给出的当前值；
/// <see cref="Increase"/>／<see cref="Decrease"/> 来自增减通知；<see cref="RefilledAt"/> 是上次补充时间；
/// <see cref="RefillNotified"/> 表示收到了补充通知。名称、说明与补充间隔取自客户端表，读不到时为空。
/// <see cref="RefillCap"/> 只随完整同步帧给出：银币、魔族貢品的补充上限，登录数据明确没有生效会员时为客户端表的基础上限；
/// 有生效会员、会员数据没有读到或无法判定时为空。持有量可以超过上限，达到或超过上限时游戏不再补充。
/// </summary>
public sealed record InventoryResourceChange(string Key, string MoneyType, UInt64String? Value = null, UInt64String? Increase = null,
    UInt64String? Decrease = null, DateTimeOffset? RefilledAt = null, bool RefillNotified = false, string? RawName = null,
    string? Description = null, double? RefillIntervalSeconds = null, int? RefillCap = null);
/// <summary>
/// 一项钱包资源的当前状态。<see cref="Scope"/> 为归属范围，角色级时 <see cref="CharacterId"/> 为所属角色（迁移时找不到角色的历史值为空）。<see cref="State"/> 为 observed（登录数据或变化消息给出过数值）或 unread（都没有，<see cref="Reason"/> 说明原因，数值为空，不是 0）。
/// <see cref="Verified"/> 表示该资源的变化消息已在留存实采中核对过。<see cref="RefillCap"/> 是最近一次登录数据判定的补充上限（未能确定时为空）；
/// 持有量可以超过上限，达到或超过上限时游戏不再补充。<see cref="NextRefillAt"/> 只在取得上次补充时间（登录数据或补充消息）、
/// 客户端表给出补充间隔且持有量低于上限或上限未知时提供。
/// </summary>
public sealed record InventoryResourceView(string Key, string MoneyType, string State, string? Reason, string? RawName,
    string? Description, UInt64String? Value, UInt64String? LastIncrease, UInt64String? LastDecrease, DateTimeOffset? ObservedAt,
    DateTimeOffset? LastRefilledAt, double? RefillIntervalSeconds, DateTimeOffset? NextRefillAt, DateTimeOffset? RefillNotifiedAt,
    bool Verified, bool Current, string? ObservedCharacterId = null, InventoryResourceScope Scope = InventoryResourceScope.Account,
    string? CharacterId = null, int? RefillCap = null);
/// <summary>钱包资源的归属范围：账户级（同账户各角色共用一份）或角色级（每个角色一份）。</summary>
public enum InventoryResourceScope { Account, Character }
/// <summary>
/// 资源视图：一个库存账户的账户级资源（与公共保管箱同一账户身份；没有游戏账户时为按角色归属的 <c>character-</c> 身份），
/// 加该账户下每个角色的角色级资源（当前登录角色在前）。每项的 <see cref="InventoryResourceView.Scope"/> 与 <see cref="InventoryResourceView.CharacterId"/>
/// 给出归属；<see cref="Characters"/> 是该账户下的角色。<see cref="CharacterId"/>／<see cref="CharacterName"/> 是当前库存身份角色（属于本账户时），
/// <see cref="Current"/> 为假时数值是上次已知值，带各自的观察时间。
/// </summary>
public sealed record InventoryResourcesView(string? AccountId, string? CharacterId, string? CharacterName, bool Current,
    UInt64String Revision, IReadOnlyList<InventoryResourceView> Items, IReadOnlyList<InventoryCharacterSlot>? Characters = null);
public sealed record InventoryNameAssociation(InventoryTypeKey Type, string? RawName, string? DisplayName,
    OpenEnum<InventoryNameState> State, string? Source, string? Evidence = null, bool? IsInstrument = null);
public sealed record InventoryItemView(InventoryItem Item, InventoryNameAssociation Name, bool Current,
    DateTimeOffset LastObservedAt, OpenEnum<ObservationQuality> Quality, ObservationSource? Source = null,
    string? CharacterId = null);
public sealed record InventoryWorkspace(string Id, string GameSessionId, InventoryIdentity? Identity,
    UInt64String Generation, UInt64String Revision, UInt64String? BindingRevision,
    OpenEnum<ObservationQuality> Quality, bool Current, DateTimeOffset? LastObservedAt,
    IReadOnlyList<InventoryItemView> Items, IReadOnlyList<InventoryCoverage> Coverage, UInt64String? Gold,
    IReadOnlyList<string> Reasons, ObservationSource? Source = null);
public sealed record InventorySnapshot(string BackendEpoch, string EventCursor, UInt64String ResourceRevision,
    InventoryWorkspace? Data, InventoryCatalogSummary Catalog);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryObservationRequest(string GameSessionId, string? ReplayScenarioId = null,
    int ReplayIntervalMs = 250);
public sealed record InventoryObservationStatus(string Id, string Owner, string GameSessionId, string State,
    UInt64String Revision, UInt64String Generation, DateTimeOffset StartedAt, DateTimeOffset? LastObservedAt,
    OpenEnum<ObservationQuality> Quality, IReadOnlyList<string> Reasons);
public sealed record InventoryCapability(string Id, OpenEnum<CapabilityStatus> Status, ObservationSource? Source,
    string? Reason, IReadOnlyList<string> Categories, string? ClientAssemblySha256 = null,
    string? ClientMetadataSha256 = null);
public sealed record InventoryCapabilities(bool Simulation, IReadOnlyList<InventoryCapability> Capabilities);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryItemQuery(string? AccountId = null, string? CharacterId = null,
    OpenEnum<InventoryContainer>? Container = null, string? Category = null, string? Search = null,
    int PageSize = 100, string? Cursor = null, bool CurrentOnly = false);
public sealed record InventoryItemPage(IReadOnlyList<InventoryItemView> Items, string? NextCursor,
    UInt64String Revision);
public sealed record InventoryCharacterView(InventoryIdentity Identity, DateTimeOffset? LastObservedAt,
    IReadOnlyList<InventoryCoverage> Coverage, bool Current, UInt64String? Gold = null);
public sealed record InventoryAggregate(InventoryTypeKey Type, UInt64String ObservedQuantity,
    UInt64String? CompleteQuantity, bool CoverageComplete, InventoryNameAssociation Name);
public sealed record InventoryCharacterSlot(string CharacterId, string RawName, string DisplayName);
public sealed record InventoryAccountView(string AccountId, string Label, UInt64String Revision,
    IReadOnlyList<InventoryCharacterView> Characters, IReadOnlyList<InventoryCharacterSlot> Slots,
    bool SlotsComplete, IReadOnlyList<InventoryAggregate> Aggregates,
    IReadOnlyList<InventoryCoverage>? SharedCoverage = null, DateTimeOffset? SharedObservedAt = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryAccountQuery(int PageSize = 25, string? Cursor = null);
public sealed record InventoryAccountPage(IReadOnlyList<InventoryAccountView> Items, string? NextCursor,
    UInt64String Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryAssociationChange(InventoryTypeKey Type, string? Name, bool Clear = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryAssociationEditRequest(string AccountId,
    IReadOnlyList<InventoryAssociationChange> Changes);
public sealed record InventoryAssociationSnapshot(string AccountId, UInt64String Revision,
    IReadOnlyList<InventoryNameAssociation> Items);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryCharacterSlotsRequest(string AccountId,
    IReadOnlyList<InventoryCharacterSlot> Slots, bool Complete);
public sealed record InventoryObservationDegraded(string ObservationId, string GameSessionId,
    UInt64String Revision, UInt64String Generation, IReadOnlyList<string> Reasons, bool SnapshotRequired = true);
public sealed record InventorySnapshotChanged(UInt64String Revision, string? AccountId, string? CharacterId,
    bool Current, bool SnapshotRequired = true);
/// <summary>一项钱包资源的定义：资源键（钱包字段名）、对应的 MoneyType 名称，以及变化消息是否已在留存实采中核对。</summary>
public sealed record InventoryResourceDefinition(string Key, string MoneyType, bool Verified,
    InventoryResourceScope Scope = InventoryResourceScope.Character);
/// <summary>
/// 钱包资源的全部定义（台服 2026-11 客户端 WalletReplicatedData 的字段，深渊副本货币按 AbyssRaidMoneyType 展开）。
/// 视图按此顺序列出；登录数据与变化消息都没有给出数值的项为未读取。
/// </summary>
public static class InventoryResources
{
    public const string AbyssPrefix = "abyssRaid:";
    /// <summary>没有取得游戏账户时按角色归属的账户 ID 前缀（与库存观察器一致）。</summary>
    public const string CharacterAccountPrefix = "character-";
    /// <summary>只读出账户主角色（<c>AccountProfile.MasterCharacterId</c>）、还没有游戏账户编号时的账户 ID 前缀；取得编号后作为该账户的别名记下，账户键不变。</summary>
    public const string MasterAccountPrefix = "master-";
    public const int MaximumChanges = 64;
    /// <summary>
    /// 归属范围对照表，依据客户端类型 <c>Shared.Accounts.AccountWallet</c>（台服 2026-11 客户端 metadata）的成员：
    /// <c>MCashSet</c>（M 币付费／免费）、<c>DecaPaid</c>、<c>DecaFree</c>、<c>CashShopPoint</c>、<c>PackagePoint</c>、<c>GuildCoin</c>、
    /// <c>WecaPaid</c>、<c>WecaFree</c>（另有 <c>AccountExpirableMonies</c>，即账户限时货币，当前没有接入）。这些按账户记；
    /// 钱包里其余货币不在账户钱包里，按角色记。不在本表里的键按角色记，视图带原因 inventory.resourceScopeUnknown。
    /// </summary>
    public static IReadOnlySet<string> AccountKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "mCashFree", "mCashPaid", "decaFree", "decaPaid", "cashShopPoint", "packagePoint", "guildCoin", "wecaFree", "wecaPaid",
    };
    /// <summary>资源键的归属范围；未知键按角色。</summary>
    public static InventoryResourceScope ScopeOf(string key) => AccountKeys.Contains(key) ? InventoryResourceScope.Account : InventoryResourceScope.Character;
    public static IReadOnlyList<InventoryResourceDefinition> Definitions { get; } = Array.AsReadOnly<InventoryResourceDefinition>(Table().ToArray());
    private static IEnumerable<InventoryResourceDefinition> Table() => new InventoryResourceDefinition[]
    {
        new("goldFree", "Gold", true), new("goldPaid", "Gold", false), new("silverCoin", "SilverCoin", true), new("passDeep", "Pass_Deep", true),
        new("heartToken", "Coin_SouthRegionToken", true), new("guildCoin", "GuildCoin", false), new("catToken", "Coin_CatToken", false),
        new("renewalTicket", "RenewalTicket", false), new("monsterKillToken", "Coin_MonsterKillToken", false),
        new("wecaFree", "Weca", false), new("wecaPaid", "Weca", false), new("decaFree", "RedCrystal", false), new("decaPaid", "RedCrystal", false),
        new("mCashFree", "FreeCrystal", false), new("mCashPaid", "Crystal", false), new("cashShopPoint", "Mileage", false),
        new("packagePoint", "SpecialCoin", false), new("memorialSagaCoin", "MemorialSagaCoin", false), new("afkGauge", "AFKGauge", false),
        new("redSoul", "RedSoul", false), new("blueSoul", "BlueSoul", false), new("yellowSoul", "YellowSoul", false),
        new(AbyssPrefix + "FossilofAbyss", "FossilofAbyss", false), new(AbyssPrefix + "FossilofAbyssHard", "FossilofAbyssHard", false),
        new(AbyssPrefix + "FossilofAbyssBari", "FossilofAbyssBari", false), new(AbyssPrefix + "FossilofAbyssAncient", "FossilofAbyssAncient", false),
        new(AbyssPrefix + "AbyssBariToken", "AbyssBariToken", false), new(AbyssPrefix + "FossilofAbyssRunda", "FossilofAbyssRunda", false),
        new(AbyssPrefix + "RaidGlasgavelenToken", "RaidGlasgavelenToken", false), new(AbyssPrefix + "RaidTabhartasToken", "RaidTabhartasToken", false),
        new(AbyssPrefix + "RaidTabhartasKillToken", "RaidTabhartasKillToken", false), new(AbyssPrefix + "RaidGairechToken", "RaidGairechToken", false),
    }.Select(d => d with { Scope = ScopeOf(d.Key) });
}
