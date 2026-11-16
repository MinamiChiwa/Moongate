using System.Text.Json;

namespace MabiTool.Contracts;

public sealed record ProjectedResource(string Type, string Module, string ResourceId, UInt64String ResourceRevision, JsonElement Data);
public sealed record ProjectionSnapshot(string BackendEpoch, string EventCursor, IReadOnlyList<ProjectedResource> Resources);
public sealed record EventStreamOptions(IReadOnlyList<string> Topics, IReadOnlyList<string> ResourceIds, string? LastEventId);
public sealed record StreamMessage(string Event, string? Id, JsonElement Data);
public sealed record StreamControl(string Code, string Reason);
