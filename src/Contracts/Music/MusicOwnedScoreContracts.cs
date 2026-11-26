// Copyright (c) 2026 MinamiChiwa（南千和）; MIT.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicOwnedScoreQuery(OpenEnum<InventoryContainer>? Container = null,
    int PageSize = 100, string? Cursor = null, bool CurrentOnly = false);

public sealed record MusicOwnedScore(InventoryTypeKey Type, OpenEnum<InventoryContainer> Container,
    string Key, UInt64String? InstanceId, UInt64String? Quantity, UInt64String? SheetId,
    string? RawTitle, string? Composer, int? ComposerRealmId, string? Arranger, int? ArrangerRealmId,
    string? AcquiredTimeRaw, bool? IsCopyingAllowed, bool? IsFavorite, bool? IsLocked, bool? IsJunked,
    JsonElement Attributes, bool MetadataComplete, IReadOnlyList<string> Reasons,
    bool Current, OpenEnum<ObservationQuality> Quality, DateTimeOffset LastObservedAt, ObservationSource? Source);

public sealed record MusicOwnedScorePage(string BackendEpoch, UInt64String InventoryRevision,
    string? WorkspaceId, UInt64String? WorkspaceRevision, string? GameSessionId,
    UInt64String? BindingRevision, UInt64String? Generation, InventoryIdentity? Identity,
    ObservationSource? Source, DateTimeOffset? LastObservedAt, bool Simulation, bool Current,
    bool Complete, OpenEnum<ObservationQuality> Quality, IReadOnlyList<InventoryCoverage> Coverage,
    IReadOnlyList<string> Reasons, IReadOnlyList<MusicOwnedScore> Items, string? NextCursor);

/// <summary>抓包乐谱来源同步的结果：<c>Synced</c> 已用背包里的乐谱刷新来源；<c>Unavailable</c> 库存不是当前或存储不可用；<c>Failed</c> 读取失败。</summary>
public enum MusicOwnedSyncStatus { Synced, Unavailable, Failed }

/// <summary>
/// 把背包抓包读到的乐谱同步为乐谱库“库存”来源的结果。<see cref="ScopeKey"/> 是当前角色的来源范围键（没有同步成功过时为空）；
/// <see cref="ScoreCount"/> 是来源里当前在游戏中的条目数；<see cref="GameProvidedSkipped"/> 是表编号非 0 的游戏提供乐谱的数量
/// （这类乐谱只由游戏列表来源提供，不入抓包来源），<see cref="UnusableSkipped"/> 是标题无效而跳过的实例数；
/// <see cref="Complete"/> 为真表示三个容器覆盖完整、本次是完整刷新（缺少的条目已标为已不在游戏里）。
/// 不可用与失败时来源保持上次内容，<see cref="SyncedAt"/> 是上次成功同步的时刻（没有时为空）。
/// </summary>
public sealed record MusicOwnedSync(string BackendEpoch, OpenEnum<MusicOwnedSyncStatus> Status, string? Reason, string? ScopeKey,
    int ScoreCount, int GameProvidedSkipped, int UnusableSkipped, bool Complete, DateTimeOffset? SyncedAt);
