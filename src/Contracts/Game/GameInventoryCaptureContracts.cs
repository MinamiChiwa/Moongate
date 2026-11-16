using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

public sealed record ObservationInventoryProfile(string Id, ObservationSource Source,
    string AssemblySha256, string MetadataSha256, IReadOnlyList<string> Categories,
    bool FullInventory, bool IncrementalInventory, bool Simulation = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObservationInventoryInterest(string Id, string Profile);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryCaptureReport(int SchemaVersion, string InterestId, InventoryFrame Frame);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InventoryClientRequest(string GameSessionId, int ProcessId,
    DateTimeOffset ProcessStartTime, string Region = "TW");
public sealed record InventoryClientPrepared(ObservationInventoryProfile Profile);
public sealed record InventoryCatalogArtifact(string FileName, UInt64String Bytes, string Sha256);
public sealed record InventoryCaptureArtifact(string FileName, UInt64String Bytes, string Sha256);
