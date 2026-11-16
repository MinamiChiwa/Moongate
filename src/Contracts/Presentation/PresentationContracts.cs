using System.Text.Json.Serialization;

namespace MabiTool.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PresentationQuery(IReadOnlyList<string>? Topics = null, IReadOnlyList<string>? ResourceIds = null);
public sealed record PresentationClockSample(string HostClockEpoch, DateTimeOffset Utc, double MonotonicMs);
public sealed record PresentationSourceStatus(string Topic, OpenEnum<CapabilityStatus> Status, string? Reason);
public sealed record PresentationSnapshot(int SchemaVersion, string BackendEpoch, string? EventCursor,
    EventStreamOptions? Subscription, PresentationClockSample Clock, IReadOnlyList<PresentationSourceStatus> Sources,
    IReadOnlyList<ProjectedResource> Resources);
public sealed record RoomPresentation(string Owner, string HostClockEpoch, DateTimeOffset ObservedAt,
    RoomLifecycleSnapshot Session, RoomClockSample? Clock);
public sealed record NearbyPlayersSnapshot(IReadOnlyList<CharacterIdentity> Players);

public sealed record DesktopSurfaceCapabilities(bool Topmost, bool ClickThrough, bool ExcludeFromCapture, bool ColorPickerOverlay);
public sealed record DesktopSurfaceState(bool Topmost, bool ClickThrough, bool ExcludeFromCapture);
public sealed record DesktopSurfaceSnapshot(string Id, string Owner, UInt64String Revision, bool Available,
    string? Reason, DesktopSurfaceCapabilities Capabilities, DesktopSurfaceState? State);
public sealed record DesktopBridgeStatus(string State, string? Reason, IReadOnlyList<DesktopSurfaceSnapshot> Surfaces);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DesktopSurfaceChange(bool? Topmost = null, bool? ClickThrough = null, bool? ExcludeFromCapture = null);

