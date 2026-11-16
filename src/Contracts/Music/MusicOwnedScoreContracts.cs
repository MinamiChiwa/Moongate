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
