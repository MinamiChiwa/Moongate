// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using MabiTool.Contracts;

namespace MabiTool.Modules.Inventory.Domain;

/// <param name="Resources">该角色的角色级钱包资源（归属范围见 <c>InventoryResources.ScopeOf</c>）。</param>
internal sealed record InventorySavedCharacter(InventoryIdentity Identity, DateTimeOffset LastObservedAt,
    IReadOnlyList<InventoryItem> Items, IReadOnlyList<InventoryCoverage> Coverage, UInt64String? Gold, ObservationSource? Source = null,
    IReadOnlyDictionary<string, InventorySavedResource>? Resources = null);
/// <summary>一项钱包资源的已知状态（账户级记在账户上，角色级记在角色上）；<see cref="Value"/> 为空表示本账户尚未收到该资源的变化消息。<see cref="CharacterId"/> 是最近一次更新的角色。</summary>
internal sealed record InventorySavedResource(string Key, string MoneyType, UInt64String? Value, UInt64String? LastIncrease,
    UInt64String? LastDecrease, DateTimeOffset? ObservedAt, DateTimeOffset? LastRefilledAt, DateTimeOffset? RefillNotifiedAt,
    string? RawName, string? Description, double? RefillIntervalSeconds, string? CharacterId = null, int? RefillCap = null);
/// <param name="GameAccountId">账户键不是游戏账户编号（<c>master-</c> 账户）而已知编号时记下的别名；按编号到达的同步据此记入本账户。</param>
internal sealed record InventorySavedAccount(string AccountId, string Label,
    IReadOnlyDictionary<string, InventorySavedCharacter> Characters, IReadOnlyList<InventoryItem> SharedItems,
    IReadOnlyList<InventoryCoverage> SharedCoverage, DateTimeOffset? SharedObservedAt,
    IReadOnlyList<InventoryCharacterSlot> Slots, bool SlotsComplete,
    IReadOnlyDictionary<string, InventoryNameAssociation> Associations, ObservationSource? SharedSource = null,
    IReadOnlyDictionary<string, InventorySavedResource>? Resources = null, string? GameAccountId = null);
internal sealed record InventorySavedState(UInt64String Revision, InventoryWorkspace? Workspace,
    UInt64String Sequence, IReadOnlyDictionary<string, InventorySavedAccount> Accounts)
{
    public static InventorySavedState Empty { get; } = new(new(0), null, new(0), new Dictionary<string, InventorySavedAccount>());
}
