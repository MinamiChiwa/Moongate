using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public enum MusicInstrumentMappingState { ClientCatalog, Unknown }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicOwnedInstrumentQuery(OpenEnum<InventoryContainer>? Container = null,
    int PageSize = 100, string? Cursor = null, bool CurrentOnly = false);

public sealed record MusicOwnedInstrument(InventoryTypeKey Type, OpenEnum<InventoryContainer> Container,
    string Key, UInt64String? InstanceId, UInt64String? Quantity, bool Equipped,
    InventoryNameAssociation Name, string? ClientInstrumentId, UInt64String? InstrumentTypeId,
    UInt64String? EquipmentToolTypeId, OpenEnum<MusicInstrumentMappingState> Mapping,
    bool Current, OpenEnum<ObservationQuality> Quality, DateTimeOffset LastObservedAt,
    ObservationSource? Source);

public sealed record MusicOwnedInstrumentPage(string BackendEpoch, UInt64String InventoryRevision,
    UInt64String CatalogRevision, string? CatalogId, string? GameSessionId,
    UInt64String? BindingRevision, UInt64String? Generation, InventoryIdentity? Identity,
    ObservationSource? Source, bool Simulation, bool Current, OpenEnum<ObservationQuality> Quality,
    IReadOnlyList<InventoryCoverage> Coverage, IReadOnlyList<string> Reasons,
    IReadOnlyList<MusicOwnedInstrument> Items, string? NextCursor);
